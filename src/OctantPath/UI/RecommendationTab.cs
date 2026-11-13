using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;
using OctantPath.Maps;
using OctantPath.Recommendations;
using BeatSaberMarkupLanguage.Attributes;
using OctantPath.Connection;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.GameplaySetup;
using HMUI;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // 曲選択の足元に浮かぶ板。選んでいる譜面を起点に推薦を受け取る。
    // 順は OctantCompass が返した順のまま出す
    internal sealed class RecommendationTab : NotifiableBase, IInitializable, ITickable, IDisposable
    {
        // 板の大きさ（UI の単位）。並びの一覧を出すときと出さないとき（既定）で変わる。RecommendationTab.bsml の段の高さを足したもの
        private UnityEngine.Vector2 PanelSize => ListVisible ? new UnityEngine.Vector2(120f, 60.2f) : new UnityEngine.Vector2(74f, 22.4f);

        // 板に並びの一覧（とそれを操作する「取得」「全部」）を出すか。作成したミックスは曲選択のプレイリストで見えるので、既定では出さない
        [UIValue("list-visible")]
        public bool ListVisible => _settingsHolder.Current.MixListOnPanel;
        private const float FloorClearance = 0.02f;

        private float PanelScale => PanelLook.Scale(_settingsHolder.Current.RecommendationPanelScale);

        private BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen _panel;
        private LevelSelectionNavigationController _levelSelection;
        private bool _panelFailed;
        private float _nextSearch;

        private readonly RecommendationService _recommendations;
        private readonly DownloadService _downloads;
        private StandardLevelDetailViewController _levelDetail;
        private readonly ConnectionService _connection;
        private readonly CoverCache _covers;
        private readonly Signals.SignalService _signals;
        private readonly Trials.TrialService _trials;
        private readonly Installers.SettingsHolder _settingsHolder;
        private readonly PreviewPlayer _preview;
        private readonly PanelPlacement _placement;
        private bool _pinned = true;
        private readonly SettingsMenu _settingsMenu;
        private readonly TrialLamp _trialLamp;

        // 板を作ったときの画面（曲選択か OctantPath の設定ページ）。置き直すときの最初の位置はこの画面のすぐ上
        private HMUI.Screen _panelScreen;

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private ChartRef _seed;
        private string _seedLabel;

        // ミックス: 起点の譜面から作った並び
        private ChartRef _mixSeed;
        private string _mixLabel;
        // 下見: 推薦を並べて「入れ替え」で入れ替え、「作成」で並んでいる曲だけを取得してプレイリストを開く（取得を絞るため）
        private Core.Mix.MixDraft<RecommendationItem> _draft;
        private Preset _draftPreset;
        private string _draftChoiceLabel;
        private Preset _mixPreset;
        private string _mixChoiceLabel;

        // 種類の小窓: OctantCompass の一覧から作るプリセットの行と、choice の入力を選んでいる途中の状態
        private List<Preset> _menuPresets;
        private Preset _choosing;
        private List<PresetInput> _choiceInputs;
        private Dictionary<string, string> _chosen;
        private List<string> _chosenLabels;
        // 種類の小窓は、浮かぶ板ではなく中央の画面（曲の詳細）に重ねて出す
        private BeatSaberMarkupLanguage.Parser.BSMLParserParams _parserParams;
        // 中央の画面に重ねる小窓（種類を選ぶ小窓、知らせ、下見の小窓）。縁からの余白と寸法は OctantPath.Core.UI.Popups が決める
        private static readonly string ChoiceModalBsml = Core.UI.Popups.Markup().Replace(Core.UI.Popups.DraftCellToken, DraftCell.Template);

        [UIComponent("choice-modal-frame")]
        private HMUI.ImageView _choiceFrame;

        [UIComponent("toast-modal-frame")]
        private HMUI.ImageView _toastFrame;

        [UIComponent("draft-modal-frame")]
        private HMUI.ImageView _draftFrame;

        [UIComponent("choice-back-plate")]
        private HMUI.ImageView _choiceBackPlate;

        [UIComponent("draft-back-plate")]
        private HMUI.ImageView _draftBackPlate;

        // 戻るボタンの板の角の丸みを、そのまま引き伸ばしたとき（Simple）の 2/3 にする。
        // Sliced にして、角の絵の大きさ（sprite の border）が UI の単位でその半径になるよう pixelsPerUnitMultiplier を決める
        private void SharpenBackPlates()
        {
            foreach (var plate in new[] { _choiceBackPlate, _draftBackPlate })
            {
                if (plate != null)
                {
                    SharpenPlate(plate, ((UnityEngine.RectTransform)plate.transform).rect.size, "back");
                }
            }
        }

        // 板の大きさ（UI の単位）を渡す。レイアウトに入る板は、まだ並べられていなくて rect が 0 のことがあるので
        private static void SharpenPlate(HMUI.ImageView plate, UnityEngine.Vector2 size, string what)
        {
            var sprite = plate != null ? plate.sprite : null;
            if (sprite == null || sprite.border.x <= 0 || sprite.border.y <= 0 || size.x <= 0 || size.y <= 0)
            {
                return;
            }
            var stretched = UnityEngine.Mathf.Min(sprite.border.x / sprite.rect.width * size.x, sprite.border.y / sprite.rect.height * size.y);
            var radius = stretched * 2f / 3f;
            // sprite の pixelsPerUnit を Canvas の referencePixelsPerUnit で割ったもの（multiplier は含まない）
            var pixelsPerUnit = plate.pixelsPerUnit;
            plate.type = UnityEngine.UI.Image.Type.Sliced;
            plate.pixelsPerUnitMultiplier = sprite.border.x / (radius * pixelsPerUnit);
            Plugin.Log.Debug($"[popup] {what} plate radius {stretched:0.##} -> {radius:0.##} (multiplier {plate.pixelsPerUnitMultiplier:0.##})");
        }


        // 小窓の外周の枠線: ゲームの枠の sprite を Sliced で張る。見つからなければ出さない。押下は受けない
        private void DrawPopupFrames()
        {
            var sprite = new[] { "RoundRect10BorderFade", "RoundRectBigStroke", "RoundRectSmallStroke" }
                .Select(name => UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Sprite>().FirstOrDefault(s => s.name == name))
                .FirstOrDefault(s => s != null);
            foreach (var frame in new[] { _choiceFrame, _toastFrame, _draftFrame })
            {
                if (frame == null)
                {
                    continue;
                }
                frame.raycastTarget = false;
                if (sprite == null)
                {
                    frame.gameObject.SetActive(false);
                    continue;
                }
                frame.sprite = sprite;
                frame.type = UnityEngine.UI.Image.Type.Sliced;
            }
        }

        // 小窓の中の説明（hover-hint）を小窓の前に出す。ゲームは説明の板を、Canvas と ScreenBase を両方持つ親（画面）の子にして出す
        // （HMUI の HoverHintController.GetScreenTransformForHoverHint）。小窓は自分の Canvas で画面より前に描く（BSML の FixModalBlockerLayer）ので、
        // 説明が小窓の裏に隠れた（下見の行の ▶・-・⇔ など。2026-10-30）。小窓に ScreenBase（中身のない印）を付けて、説明を小窓の子にする
        private void RaiseModalHints()
        {
            foreach (var modal in new[] { _choiceModal, _draftModal })
            {
                if (modal != null && modal.GetComponent<ScreenBase>() == null)
                {
                    modal.gameObject.AddComponent<ScreenBase>();
                }
            }
        }

        private string _toastText = "";
        private int _toastId;

        [UIValue("toast-text")]
        public string ToastText => _toastText;

        // 中央の画面に知らせを出し、数秒で消す
        private async void Toast(string text, float seconds = 3f)
        {
            _toastText = text;
            NotifyPropertyChanged(nameof(ToastText));
            _parserParams?.EmitEvent("show-toast");
            var id = ++_toastId;
            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (id == _toastId)
            {
                _parserParams?.EmitEvent("hide-toast");
            }
        }

        // OctantCompass が使えないときは知らせを出して true を返す
        private bool ToastIfUnavailable()
        {
            var status = _connection.Status;
            if (status.State == Core.Connection.ConnectionState.Ready && _connection.Presets != null)
            {
                return false;
            }
            var text = status.State != Core.Connection.ConnectionState.Ready
                ? $"{Lang.T("connection.unreachable")}\n<size=80%>{Core.Connection.StatusText.Hint(status) ?? Core.Connection.StatusText.Describe(status)}</size>"
                : _connection.PresetsProblem ?? Lang.T("presets.none");
            Toast(text);
            _ = _connection.RefreshNowAsync();
            return true;
        }

        [UIValue("draft-cells")]
        private List<object> _draftCells = new List<object>();

        [UIComponent("draft-list")]
        private CustomCellListTableData _draftList;

        [UIComponent("draft-modal")]
        private ModalView _draftModal;

        [UIComponent("choice-modal")]
        private ModalView _choiceModal;

        // 小窓をすぐ閉じる（アニメーションなし）。曲選択を開き直すときに残らないように、画面が変わる前に呼ぶ
        private void HideModals()
        {
            try
            {
                if (_draftModal != null && _draftModal.isShown)
                {
                    _draftModal.Hide(false);
                }
                if (_choiceModal != null && _choiceModal.isShown)
                {
                    _choiceModal.Hide(false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[mix] could not hide the popups: {ex.Message}");
            }
        }

        // 下見で選んでいる曲（板の並びの選択とは別）と、下見の小窓に出す文
        private RecommendationItem _draftSelected;
        private string _draftMessage = "";

        [UIValue("draft-title")]
        public string DraftTitle => _draft == null ? (_busy ? Lang.T("mix.making") : "") : Lang.T("draft.title", new { name = TypeName(_draftPreset, _draftChoiceLabel) });

        [UIValue("draft-seed")]
        // 起点を使わない種類（おすすめなど。OctantCompass へ起点を送らない）では、起点の行は空にする
        public string DraftSeed => _mixUsesSeed && _mixLabel != null ? Lang.T("seed.from", new { song = _mixLabel }) : "";

        // 今作っている（作った）種類が起点を使うか。PresetRules.Request が起点を送るかと同じ
        private bool _mixUsesSeed;

        // 今作っている（作った）種類。下見の小窓の戻るで、ひとつ前へ戻すのに使う
        private Preset _draftingPreset;

        [UIValue("choice-seed")]
        public string ChoiceSeed => _seed != null ? Lang.T("seed.from", new { song = _seedLabel }) : _songText != null ? Lang.T("choice.seedUnusable") : Lang.T("seed.noneChoice");

        [UIObject("choice-tiles")]
        private UnityEngine.GameObject _choiceGrid;

        // 種類の小窓の見出しの行（「曲」「譜面」など）
        private void AddHeading(string text)
        {
            var label = BeatSaberMarkupLanguage.BeatSaberUI.CreateText((UnityEngine.RectTransform)_choiceGrid.transform, text, UnityEngine.Vector2.zero, new UnityEngine.Vector2(Core.UI.Popups.ChoiceTileWidth, Core.UI.Popups.ChoiceHeadingHeight));
            label.fontSize = Core.UI.Popups.ChoiceHeadingFontSize;
            label.alignment = TMPro.TextAlignmentOptions.BottomLeft;
            label.color = new UnityEngine.Color(0.75f, 0.85f, 1f, 1f);
            // 見出しは押下を受けない（下のタイルの押せる範囲を取らない）
            label.raycastTarget = false;
        }

        // 種類のタイルの説明の始まり（欄の幅の割合）と、名前の列と説明の間に最低限あける幅（UI の単位。推測で決めた値）
        private const float ChoiceHintColumn = 0.34f;
        private const float ChoiceColumnGap = 1.5f;

        // 種類の小窓のタイル（ゲームの丸いボタン）。上に大きく名前、下に小さく説明
        private void ShowTiles(List<(string Label, string Hint, bool Enabled)> tiles) => ShowTiles(tiles, null);

        // groups があれば、見出しが変わるところに見出しの行を入れる（並びは OctantCompass の順のまま）
        private void ShowTiles(List<(string Label, string Hint, bool Enabled)> tiles, List<string> groups)
        {
            if (_choiceGrid == null)
            {
                return;
            }
            foreach (UnityEngine.Transform child in _choiceGrid.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }
            // 1 列に並べる。名前の列と説明の列の区切りを縦にそろえる（<pos> で説明の始まりを固定）。
            // タイルは下見の行と同じ高さで、小窓に入りきらない分はスクロールでめくる
            var height = Core.UI.Popups.ChoiceTileHeight;
            var fontSize = Core.UI.Popups.ChoiceTileFontSize;
            for (var i = 0; i < tiles.Count; i++)
            {
                var index = i;
                var tile = tiles[i];
                var group = groups != null && i < groups.Count ? groups[i] : null;
                if (!string.IsNullOrEmpty(group) && (i == 0 || groups[i - 1] != group))
                {
                    AddHeading(group);
                }
                try
                {
                    var made = Tile.Create(_choiceGrid, tile.Label, Core.UI.Popups.ChoiceTileWidth, height, () => ChoiceSelect(null, index), TMPro.TextAlignmentOptions.Left, fontSize);
                    // 名前の列は欄の 34% まで。長い名前は「…」で詰めて、説明にかぶせない
                    made.SetColumns(tile.Label, 1.1f, tile.Hint, 0.8f, "#BBBBBB", ChoiceHintColumn, ChoiceColumnGap);
                    made.Interactable = tile.Enabled;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[mix] could not make the tile {tile.Label}: {ex}");
                }
            }
            // 並べ直したら一番上から見せる
            ChoiceScroll()?.ScrollTo(0f, false);
        }

        // 種類の小窓の scroll-view。小窓が閉じている間は親が非アクティブで、GetComponentInParent は非アクティブの親を見ないので null になった
        // （起動時に「scroll view was not found」）。非アクティブも含めて探す
        private HMUI.ScrollView ChoiceScroll() =>
            _choiceGrid != null ? _choiceGrid.GetComponentsInParent<HMUI.ScrollView>(true).FirstOrDefault() : null;

        // 種類の小窓の scroll-view: 見える欄（viewport）を右の 8 だけ詰め、スクロールの帯はその空きに置く。
        // タイルは下見の一覧の行と同じ幅・同じ左端になる。帯は scroll-view の右の端に寄る（下見の一覧の帯と同じ位置）
        private void FitChoiceScroll()
        {
            var scroll = ChoiceScroll();
            if (scroll == null)
            {
                Plugin.Log.Warn("[popup] the type popup's scroll view was not found");
                return;
            }
            var gap = Core.UI.Popups.ListScrollbarWidth;
            // 見える欄: scroll-view いっぱいから右の帯の分だけ詰める
            var viewport = scroll.viewportTransform;
            viewport.anchorMin = UnityEngine.Vector2.zero;
            viewport.anchorMax = UnityEngine.Vector2.one;
            viewport.offsetMin = UnityEngine.Vector2.zero;
            viewport.offsetMax = new UnityEngine.Vector2(-gap, 0f);
            // 中身: 見える欄と同じ幅にして上から並べる。タイルの幅は見える欄と同じ（BSML は中身の幅をタイルの幅に縮めて真ん中に置くので、欄の幅とずれると左右に寄った）
            var content = scroll.contentTransform;
            var fitter = content.GetComponent<UnityEngine.UI.ContentSizeFitter>();
            if (fitter != null)
            {
                fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
            }
            content.anchorMin = new UnityEngine.Vector2(0f, 1f);
            content.anchorMax = new UnityEngine.Vector2(1f, 1f);
            content.pivot = new UnityEngine.Vector2(0f, 1f);
            content.offsetMin = new UnityEngine.Vector2(0f, content.offsetMin.y);
            content.offsetMax = new UnityEngine.Vector2(0f, content.offsetMax.y);
            // スクロールの帯の入れ物: scroll-view の右端に幅 8 で固定する（前は全面に引き伸ばして、帯が真ん中に来た）
            var indicator = scroll.GetComponentInChildren<HMUI.VerticalScrollIndicator>(true);
            if (indicator != null && indicator.transform.parent is UnityEngine.RectTransform bar && bar != scroll.transform)
            {
                bar.anchorMin = new UnityEngine.Vector2(1f, 0f);
                bar.anchorMax = new UnityEngine.Vector2(1f, 1f);
                bar.pivot = new UnityEngine.Vector2(1f, 0.5f);
                bar.anchoredPosition = UnityEngine.Vector2.zero;
                bar.sizeDelta = new UnityEngine.Vector2(gap, 0f);
            }
        }

        [UIValue("draft-detail")]
        public string DraftDetail => _draftMessage;

        // 下見の帯を見せている間。「作成」では取得から曲選択を開き直すまで、▶ では 1 曲を取得している間。小窓は開いたまま進み具合を見せる
        private bool _creating;

        [UIObject("progress-track")]
        private UnityEngine.GameObject _progressTrack;

        [UIObject("progress-fill")]
        private UnityEngine.GameObject _progressFill;

        // 作成中の取得の進み具合。曲ごとの終わりだけで伸ばすと、1〜2 曲では取り終えるまで伸びず、終わった瞬間に満ちていた（2026-10-18）。
        // なので結果画面の帯と同じく、おもに時間で伸ばし、受け取った割合と終わった曲数の大きい方へ寄せる（TickCreateProgress）
        private readonly SmoothProgress _createSmooth = new SmoothProgress();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _createReceived = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        private int _createDone;
        private int _createTotal;
        private bool _createDownloading;
        private float _createStartedAt;

        // 1 曲を取るのにかかる時間の目安（秒）。推測で 3 秒から始め、作成のたびに実際の時間へ寄せる
        private float _createSecondsPerSong = 3f;

        private void StartCreateProgress(int total)
        {
            _createReceived.Clear();
            _createDone = 0;
            _createTotal = total;
            _createDownloading = total > 0;
            _createStartedAt = UnityEngine.Time.unscaledTime;
            _createSmooth.Stop();
            if (_createDownloading)
            {
                _createSmooth.Start();
            }
            DrawCreateProgress();
            ShowCreateTrack();
        }

        // 帯は行に置いたままにし、作成中だけ見せる（出し入れすると、一覧が帯の高さだけ動いた）
        private void ShowCreateTrack()
        {
            foreach (var part in new[] { _progressTrack, _progressFill })
            {
                var background = part != null ? part.GetComponent<BeatSaberMarkupLanguage.Components.Backgroundable>()?.Background : null;
                if (background != null)
                {
                    background.enabled = _creating;
                }
            }
        }

        // 取り終えた（このあと曲の読み直しが続く）。帯は残りをなめらかに埋めて、満ちたまま残す
        private void FinishCreateDownloads()
        {
            if (!_createDownloading)
            {
                return;
            }
            _createDownloading = false;
            var perSong = (UnityEngine.Time.unscaledTime - _createStartedAt) / _createTotal;
            _createSecondsPerSong = UnityEngine.Mathf.Lerp(_createSecondsPerSong, UnityEngine.Mathf.Max(0.3f, perSong), 0.3f);
        }

        private void TickCreateProgress()
        {
            if (!_creating)
            {
                return;
            }
            float? fraction = null;
            if (_createDownloading)
            {
                fraction = (float)Math.Max(_createDone, _createReceived.Values.Sum()) / _createTotal;
            }
            if (_createSmooth.Update(fraction, _createSecondsPerSong * _createTotal))
            {
                DrawCreateProgress();
            }
        }

        private void DrawCreateProgress()
        {
            if (_progressFill == null)
            {
                return;
            }
            var shown = _createSmooth.Visible ? _createSmooth.Shown : _createDownloading ? 0f : 1f;
            PanelLook.SetFill(_progressFill, shown, Core.UI.Popups.DraftProgressWidth);
        }

        private void SetProgress(int done, int total, string text)
        {
            _createDone = done;
            if (total > 0 && done >= total)
            {
                FinishCreateDownloads();
            }
            _draftMessage = text;
            NotifyPropertyChanged(nameof(DraftDetail));
            NotifyStatus();
        }

        [UIValue("draft-idle")]
        public bool DraftIdle => !_busy;

        [UIValue("draft-create-interactable")]
        public bool DraftCreateInteractable => !_busy && _draft != null && _draft.Picked.Count > 0;

        [UIAction("draft-close")]
        private void DraftClose()
        {
            // 自分で閉じた下見は、その曲をまた遊んでも開き直さない
            _draftResumeHash = null;
            _draftResumeArmed = false;
            _preview.Stop();
            _preview.StopPrefetch();
            _parserParams?.EmitEvent("hide-draft");
        }

        [UIAction("draft-retype")]
        private void DraftRetype()
        {
            _preview.Stop();
            _preview.StopPrefetch();
            _parserParams?.EmitEvent("hide-draft");
            MixClick();
        }

        // 下見の小窓の左上の戻る: ひとつ前の小窓へ。choice の入力がある種類はその選択へ、なければ種類の小窓へ
        [UIAction("draft-back")]
        private void DraftBack()
        {
            if (_busy)
            {
                return;
            }
            _preview.Stop();
            _preview.StopPrefetch();
            _parserParams?.EmitEvent("hide-draft");
            var preset = _draftingPreset;
            if (preset != null && PresetRules.Choices(preset).Count > 0)
            {
                _menuPresets = null;
                _parserParams?.EmitEvent("show-choice");
                PresetChosen(preset);
                return;
            }
            MixClick();
        }

        // 種類の小窓の左上の戻る: choice を選んでいる途中ならひとつ前の選択へ、種類を選んだあとなら種類の一覧へ、それ以外は閉じる
        [UIAction("choice-back")]
        private void ChoiceBack()
        {
            if (_choosing != null && _choiceInputs != null && _chosen != null && _chosen.Count > 0)
            {
                _chosen.Remove(_choiceInputs[_chosen.Count - 1].Name);
                _chosenLabels.RemoveAt(_chosenLabels.Count - 1);
                ShowNextChoice();
                return;
            }
            if (_choosing != null)
            {
                MixClick();
                return;
            }
            _choosing = null;
            _menuPresets = null;
            _parserParams?.EmitEvent("hide-choice");
        }

        [UIAction("draft-create")]
        private void DraftCreate()
        {
            if (!_busy && _draft != null)
            {
                _ = CreateMixAsync();
            }
        }

        // 下見の曲を選ぶ: 取らずに試聴だけを流す。一番下の「曲を足す」の行なら、曲を足す
        // custom-list は選んだ行のデータ（DraftCell）を渡してくる
        [UIAction("draft-select")]
        private void DraftSelectCell(TableView tableView, object cell)
        {
            var items = _draft?.Picked;
            if (cell is DraftCell addRow && addRow.IsAddRow)
            {
                AddItem();
                return;
            }
            if (cell is DraftCell draftCell && items != null)
            {
                DraftSelect(tableView, items.ToList().IndexOf(draftCell.Item));
            }
        }

        private void DraftSelect(TableView tableView, int index)
        {
            var items = _draft?.Picked;
            if (items == null || index < 0 || index >= items.Count)
            {
                return;
            }
            var item = items[index];
            _draftSelected = item;
            if (item.Version?.PreviewUrl != null)
            {
                _preview.Play(item.Version.PreviewUrl);
            }
            _draftMessage = "";
            // 一覧は作り直さず、行の見た目だけを移す（DraftCell.SetPlaying）
            var loading = _preview.Progress(item.Version?.PreviewUrl).HasValue;
            _draftSelectedCell = null;
            foreach (var draftCell in _draftCells.OfType<DraftCell>())
            {
                draftCell.SetPlaying(draftCell.Item == item, loading);
                if (draftCell.Item == item)
                {
                    _draftSelectedCell = draftCell;
                }
            }
            NotifyDraft();
        }

        // 選んだ曲の行（毎フレーム探さないために覚える。一覧を作り直したら FillDraftList が差し替える）
        private DraftCell _draftSelectedCell;

        // 下見で選んだ曲の試聴を取っている間、その行に進み具合を出す（DraftCell.ShowLoading）
        private void TickPreviewLoading()
        {
            var item = _draftSelected;
            var cell = _draftSelectedCell;
            if (item == null || cell == null || cell.Item != item)
            {
                return;
            }
            cell.ShowLoading(_preview.Progress(item.Version?.PreviewUrl), _preview.TypicalSeconds);
        }

        private void NotifyDraft()
        {
            NotifyStatus();
            NotifyPropertyChanged(nameof(DraftTitle));
            NotifyPropertyChanged(nameof(DraftSeed));
            NotifyPropertyChanged(nameof(DraftDetail));
            NotifyPropertyChanged(nameof(DraftIdle));
            NotifyPropertyChanged(nameof(DraftCreateInteractable));
        }

        private void ShowDraftModal()
        {
            _parserParams?.EmitEvent("hide-choice");
            _parserParams?.EmitEvent("show-draft");
        }

        private void FillDraftList()
        {
            if (_draftList == null)
            {
                return;
            }
            var items = _draft != null ? _draft.Picked.ToList() : new List<RecommendationItem>();
            var actions = new DraftCell.Actions { Swap = ReplaceItem, Discard = DiscardItem, Play = PlayDraftItem };
            _draftCells = items.Select(i => (object)new DraftCell(i, i == _draftSelected, !_busy, CanPlayDraft(i), DraftState, _covers, actions)).ToList();
            // 一番下に「曲を足す」の行（並べられる上限までは出す。入れ替え用が尽きていれば、押したときに追加を受け取る）
            if (_draft != null && !_draft.Full)
            {
                _draftCells.Add(DraftCell.AddRow(_covers));
            }
            _draftSelectedCell = _draftSelected != null ? _draftCells.OfType<DraftCell>().FirstOrDefault(c => c.Item == _draftSelected) : null;
            // 試聴を取っている途中の行は、作り直しても取っている途中の見た目から出す（全体の塗りが一瞬出ないように）
            if (_draftSelected != null && _preview.Progress(_draftSelected.Version?.PreviewUrl).HasValue)
            {
                _draftSelectedCell?.StartLoading();
            }
            _draftList.Data = _draftCells;
            // 選び直しても一覧の位置を変えない。ReloadDataKeepingPosition は位置を行の頭へ切り下げるので
            // （一覧の高さが行の倍でないと、一番下では選ぶたびに上へずれた）、位置そのものを覚えて戻す
            var scroll = _draftList.TableView.scrollView;
            var position = scroll != null ? scroll.position : 0f;
            _draftList.TableView.ReloadData();
            _draftList.TableView.ScrollToPosition(position, false);
            var index = _draftSelected != null ? items.IndexOf(_draftSelected) : -1;
            if (index >= 0)
            {
                _draftList.TableView.SelectCellWithIdx(index);
            }
            else
            {
                _draftList.TableView.ClearSelection();
            }
        }

        // カバーが届いたら、行の絵だけを差し替える（一覧を作り直すと BSML を読み直して重い）
        private void RefreshDraftCovers()
        {
            foreach (var draftCell in _draftCells.OfType<DraftCell>())
            {
                draftCell.RefreshCover();
            }
        }

        private System.Threading.Tasks.Task LoadDraftCoversAsync()
        {
            var draft = _draft;
            return LoadCoversAsync(draft?.Picked.ToList() ?? new List<RecommendationItem>(), () => draft == _draft, RefreshDraftCovers);
        }

        // カバーを 1 枚ずつ取り、取れるたびに refresh で表を作り直す。wanted が偽になったらやめる
        private async System.Threading.Tasks.Task LoadCoversAsync(List<RecommendationItem> items, Func<bool> wanted, Action refresh)
        {
            foreach (var item in items)
            {
                if (_lifetime.IsCancellationRequested || !wanted())
                {
                    return;
                }
                var url = item.Version?.CoverUrl;
                if (url == null || _covers.TryGet(url, out _))
                {
                    continue;
                }
                await _covers.GetAsync(url, _lifetime.Token);
                if (wanted())
                {
                    refresh();
                }
            }
        }

        private Core.Mix.MixQueue<RecommendationItem> _mix;
        private List<RecommendationItem> _items = new List<RecommendationItem>();
        private RecommendationItem _selected;
        private string _lastPlayedHash;
        private ChartRef _lastPlayedChart;
        // 普通のプレイ（StandardLevel）を始めて、まだ結果画面のおすすめを出していない。カスタム曲でなくても立てる（出せないと見せるため）
        private bool _resultsPending;
        // 最後に始めた曲が、ミックスのプレイリストを表示している中で始めたミックスの曲なら、その曲（ほかは null）。
        // 次の曲へ進むのも、結果画面で選んだ曲をミックスに入れるのも、このときだけ（ほかの一覧で遊んだときは、その一覧のまま）
        private RecommendationItem _playedInMix;
        // ミックスのプレイリスト（曲選択を開き直すときの行き先）
        private BeatmapLevelPack _mixPack;
        // 結果画面で選んだ曲を、まだミックスのプレイリストのファイルに書いていない
        private bool _mixPlaylistStale;
        // 遊び終えたあと、曲選択に戻ったら次の曲へ進む。結果画面の間は進まない（結果画面の間に曲選択を開き直すと画面が壊れた）
        private bool _advancePending;
        // 結果画面で選んだ曲（ミックスがないときも、これを次の曲にする）
        private RecommendationItem _pendingPick;
        // 曲選択が続けて出ている時刻。戻った直後、結果画面が出る前の一瞬では進まないように使う
        private float _songListSince = -1f;
        private const float AdvanceDelay = 0.5f;

        // 結果画面のおすすめ
        private readonly ResultsPanel _resultsPanel;

        // ユーザーが曲選択でミックスの外の曲を選んでいる
        private readonly GameScenesManager _gameScenesManager;
        private readonly PlaylistSync _playlist;
        private bool _busy;
        private string _message = "";

        [UIComponent("list")]
        private CustomListTableData _list;

        public RecommendationTab(RecommendationService recommendations, DownloadService downloads, [InjectOptional] StandardLevelDetailViewController levelDetail, ConnectionService connection, CoverCache covers, Signals.SignalService signals, Trials.TrialService trials, Installers.SettingsHolder settingsHolder, PreviewPlayer preview, PanelPlacement placement, GameScenesManager gameScenesManager, PlaylistSync playlist, SettingsMenu settingsMenu, TrialLamp trialLamp)
        {
            _settingsMenu = settingsMenu;
            _trialLamp = trialLamp;
            _gameScenesManager = gameScenesManager;
            _playlist = playlist;
            _placement = placement;
            _preview = preview;
            _settingsHolder = settingsHolder;
            _trials = trials;
            _signals = signals;
            _covers = covers;
            _recommendations = recommendations;
            _downloads = downloads;
            _levelDetail = levelDetail;
            _connection = connection;
            _resultsPanel = new ResultsPanel(covers, preview, ChooseFromResults, PreviewedFromResults, () => _ = ShowOtherResultsAsync(), () => PanelScale, () => _settingsHolder.Current.PanelOpacity);
        }

        public void Initialize()
        {
            _connection.StatusChanged += HandleConnectionChanged;
            _settingsHolder.Changed += HandleSettingsChanged;
            _gameScenesManager.transitionDidFinishEvent += HandleTransitionDidFinish;
            _downloads.Changed += HandleDownloadsChanged;
            SongCore.Loader.OnLevelPacksRefreshed += HandleLevelPacksRefreshed;
            _signals.ProblemChanged += HandleSignalProblemChanged;
            _trials.StatusChanged += HandleSignalProblemChanged;
            _connection.PresetsChanged += HandlePresetsChanged;
            if (_levelDetail == null)
            {
                _levelDetail = UnityEngine.Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>().FirstOrDefault();
            }
            Plugin.Log.Debug($"[panel] level selection {(_levelSelection != null ? "found" : "not found yet")}");
            if (_levelDetail == null)
            {
                Plugin.Log.Warn("StandardLevelDetailViewController was not found; the seed cannot be read");
                return;
            }
            _levelDetail.didChangeDifficultyBeatmapEvent += HandleSelectionChanged;
            _levelDetail.didChangeContentEvent += HandleContentChanged;
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _resultsPanel.Destroy();
            if (_panel != null)
            {
                _panel.HandleReleased -= HandlePanelReleased;
                UnityEngine.Object.Destroy(_panel.gameObject);
            }
            _connection.StatusChanged -= HandleConnectionChanged;
            _settingsHolder.Changed -= HandleSettingsChanged;
            _gameScenesManager.transitionDidFinishEvent -= HandleTransitionDidFinish;
            _downloads.Changed -= HandleDownloadsChanged;
            SongCore.Loader.OnLevelPacksRefreshed -= HandleLevelPacksRefreshed;
            _signals.ProblemChanged -= HandleSignalProblemChanged;
            _trials.StatusChanged -= HandleSignalProblemChanged;
            _connection.PresetsChanged -= HandlePresetsChanged;
            if (_levelDetail != null)
            {
                _levelDetail.didChangeDifficultyBeatmapEvent -= HandleSelectionChanged;
                _levelDetail.didChangeContentEvent -= HandleContentChanged;
            }
        }

        // 板は、曲選択か OctantPath の設定ページを初めて開いたときに作る（メニューを組み立てている間には作らない）。
        // 置き場所は、その画面（どちらもゲームの真ん中の画面）のすぐ上で、同じ距離・同じ曲がり方にそろえる
        private void CreatePanel(HMUI.Screen screen)
        {
            var rect = (UnityEngine.RectTransform)screen.transform;
            var (position, rotation, radius) = Placement(screen, true);

            // 背景は、並びの一覧を出すときだけ敷く。出さないときはボタンのマスそのもので板の形を作る（2026-11-03）
            _panel = BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen.CreateFloatingScreen(PanelSize, true, position, rotation, radius, ListVisible);
            _panelScreen = screen;
            _panel.name = "OctantPathRecommendations";
            _panel.transform.localScale = UnityEngine.Vector3.one * PanelScale;
            // 持ち手は上の縁の真ん中のつまみ。固定している間は出さない（OBSControl と同じ）
            _panel.HandleSide = BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen.Side.Top;
            PanelLook.PlaceKnob(_panel);
            PanelLook.PaintKnob(_panel);
            _panel.ShowHandle = false;
            _panel.HandleReleased += HandlePanelReleased;
            if (ListVisible)
            {
                PanelLook.MakeTranslucent(_panel, _settingsHolder.Current.PanelOpacity);
            }
            BeatSaberMarkupLanguage.BSMLParser.Instance.Parse(Lang.Fill(BeatSaberMarkupLanguage.Utilities.GetResourceContent(typeof(RecommendationTab).Assembly, "OctantPath.UI.RecommendationTab.bsml")), _panel.gameObject, this);
            _panel.gameObject.SetActive(false);
            // マスの角: 板全体の外側の角（左上・右上・左下・右下）だけを丸め、マスどうしが接する内側の角は尖らせる（2026-11-03）
            PanelLook.ShapeCorners(_savePlate, TileCornerRadius, true, false, false, false);
            PanelLook.ShapeCorners(_autoNextPlate, TileCornerRadius, false, false, false, false);
            PanelLook.ShapeCorners(_ceilingPlate, TileCornerRadius, false, false, false, false);
            PanelLook.ShapeCorners(_topGapPlate, TileCornerRadius, false, false, false, false);
            PanelLook.ShapeCorners(_pinPlate, TileCornerRadius, false, true, false, false);
            PanelLook.ShapeCorners(_songPlate, TileCornerRadius, false, false, false, false);
            for (var i = 0; i < ShortcutPlates.Length; i++)
            {
                PanelLook.ShapeCorners(ShortcutPlates[i], TileCornerRadius, false, false, i == 0, i == ShortcutPlates.Length - 1);
            }
            PanelLook.ShapeCorners(_statusPlate, TileCornerRadius, false, false, true, true);
            if (_songLabel != null)
            {
                _songLabel.margin = new UnityEngine.Vector4(SongRowPad, 0f, SongRowLampSpace, 0f);
            }
            AddSongAccent();
            if (_songRow != null)
            {
                _trialLamp.CreatePanelLamp(_songRow);
            }
            ReadSeedAndNotify();
            PaintTiles();
            FillList();
            AddPopups();
            Plugin.Log.Debug($"[panel] created at {position} above the song list screen at {rect.position} (radius {radius})");
        }

        // 板の置き場所。既定は screen（曲選択の画面）のすぐ上で、少し手前へ（下向きに）傾ける。
        // useSaved なら、持ち手で動かして残した位置を使う
        private (UnityEngine.Vector3 Position, UnityEngine.Quaternion Rotation, float Radius) Placement(HMUI.Screen screen, bool useSaved)
        {
            var rect = (UnityEngine.RectTransform)screen.transform;
            var halfPanel = PanelSize.y * PanelScale / 2f;
            var (position, rotation, radius) = PanelLook.Above(screen, PanelSize.y * PanelScale);
            if (useSaved && _placement.TryLoad(out var savedPosition, out var savedRotation))
            {
                position = savedPosition;
                rotation = savedRotation;
            }
            // 床より下に来るときは、床に貼り付ける（向きは曲選択の画面にそろえる）
            if (position.y - halfPanel < FloorClearance)
            {
                var yaw = rect.rotation.eulerAngles.y;
                position = new UnityEngine.Vector3(position.x, FloorClearance, position.z);
                rotation = UnityEngine.Quaternion.Euler(90f, yaw, 0f);
            }
            return (position, rotation, radius);
        }

        // 中央の画面（曲の詳細）に重ねる小窓（種類を選ぶ小窓、知らせ、下見の小窓）を置く。
        // 曲の詳細そのものには何も重ねない（種類を選ぶ入口とランプは浮かぶ板の曲の行に移した。2026-11-08）
        private void AddPopups()
        {
            try
            {
                if (_levelDetail == null)
                {
                    Plugin.Log.Warn("[popups] the level detail was not found; the type popup is not added");
                    return;
                }
                _parserParams = BeatSaberMarkupLanguage.BSMLParser.Instance.Parse(Lang.Fill(ChoiceModalBsml), _levelDetail.gameObject, this);
                DrawPopupFrames();
                RaiseModalHints();
                SharpenBackPlates();
                PanelLook.Brighten(_progressTrack);
                PanelLook.Brighten(_progressFill);
                ShowCreateTrack();
                FitChoiceScroll();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[popups] could not be added: {ex}");
            }
        }

        // 曲選択（ソロの曲の一覧）が出ている間だけ板を出す
        public void Tick()
        {
            _resultsPanel.Tick();
            TickAdvance();
            TickPreviewLoading();
            TickCreateProgress();
            if (_panelFailed)
            {
                return;
            }
            if (_levelSelection == null)
            {
                // 見つかるまで、1 秒に 1 回だけ探す（毎フレーム探すと重い）
                if (UnityEngine.Time.unscaledTime < _nextSearch)
                {
                    return;
                }
                _nextSearch = UnityEngine.Time.unscaledTime + 1f;
                _levelSelection = UnityEngine.Resources.FindObjectsOfTypeAll<LevelSelectionNavigationController>().FirstOrDefault();
                if (_levelSelection == null && !_settingsMenu.IsOpen)
                {
                    return;
                }
            }
            var inSongList = _levelSelection != null && _levelSelection.isActivated && _levelSelection.isInViewControllerHierarchy;
            // OctantPath の設定ページを開いている間も出す（板の大きさを変えながら見られるように。2026-10-16）。
            // 曲選択を一度も開いていなくても、設定ページの画面の位置から板を作れる
            var inSettings = _settingsMenu.IsOpen;
            var show = inSongList || inSettings;
            if (_panel == null)
            {
                if (!show)
                {
                    return;
                }
                try
                {
                    var screen = inSongList ? _levelSelection.screen : _settingsMenu.Screen;
                    if (screen == null)
                    {
                        return;
                    }
                    CreatePanel(screen);
                }
                catch (Exception ex)
                {
                    _panelFailed = true;
                    Plugin.Log.Error($"[panel] could not be created: {ex}");
                    return;
                }
            }
            if (_panel.gameObject.activeSelf != show)
            {
                if (!show)
                {
                    HideModals();
                }
                _panel.gameObject.SetActive(show);
                Plugin.Log.Debug($"[panel] {(show ? "shown" : "hidden")}");
            }
        }

        // 設定画面で大きさを変えたら、その場で板に反映する
        private void HandleSettingsChanged()
        {
            if (_panel != null)
            {
                _panel.transform.localScale = UnityEngine.Vector3.one * PanelScale;
                if (ListVisible)
                {
                    PanelLook.MakeTranslucent(_panel, _settingsHolder.Current.PanelOpacity);
                }
            }
            _resultsPanel.Rescale();
            PaintTiles();
            NotifyStatus();
        }

        [UIValue("pin-hint")]
        public string PinHint => Lang.T(_pinned ? "panel.move" : "panel.pin") + ": " + Lang.T("panel.pinHint");

        [UIAction("pin-click")]
        private void PinClick()
        {
            _pinned = !_pinned;
            if (_panel != null)
            {
                _panel.ShowHandle = !_pinned;
            }
            NotifyPropertyChanged(nameof(PinHint));
            NotifyStatus();
            NotifyPropertyChanged(nameof(StatusHint));
            ShowPin();
        }


        // 板を最初の位置（作ったときの画面のすぐ上）へ戻し、残した置き場所を消す
        private void ResetPlacement()
        {
            if (_panel == null || _panelScreen == null)
            {
                return;
            }
            var (position, rotation, _) = Placement(_panelScreen, false);
            _panel.transform.SetPositionAndRotation(position, rotation);
            _placement.Clear();
            Plugin.Log.Debug($"[panel] put back at {position}");
        }

        private void HandlePanelReleased(object sender, BeatSaberMarkupLanguage.FloatingScreen.FloatingScreenHandleEventArgs args)
        {
            _placement.Save(args.Position, args.Rotation);
            Plugin.Log.Debug($"[panel] moved to {args.Position}");
        }

        // 板の知らせ。何かする必要があること（失敗）と、ほかの画面では見えないこと（お試しの曲が新しく入った）だけを出す。
        // 進み具合と成功は出さない（進み具合は中央の小窓に出て、成功は曲選択の画面で見える。2026-11-03）。
        // 下の段を押すと、板の操作の知らせとお試しの曲の知らせは消す（消した文は覚えない）。signal の送信が止まった知らせは、
        // 止まっている間ずっと出す（スコアが送られていないことを隠さない。2026-11-03）
        private string Notice => new[] { _message, _signals.Problem, _trials.Status }.FirstOrDefault(t => !string.IsNullOrEmpty(t));

        // 下の段: 移動にしている間は「最初の位置に戻す」、知らせがあれば知らせ、押したことが済んだ直後の数秒はその結果（Flash）、
        // どれもなければ OctantCompass との接続の状態（色付き）。結果も出さないと、できたのかが分からなかった。
        // 作成や取得の進み具合は中央の小窓に出ているので、ここには出さない（2026-11-03）
        [UIValue("status-text")]
        public string StatusRowText => !_pinned ? Lang.T("panel.putBack")
            : Notice != null ? Core.UI.PanelStatus.Clip(Notice, Core.UI.PanelStatus.NoticeWidth)
            : !string.IsNullOrEmpty(_flash) ? Core.UI.PanelStatus.Clip(_flash, Core.UI.PanelStatus.NoticeWidth)
            : _connection.Status.State != Core.Connection.ConnectionState.Ready ? Core.Connection.StatusText.Colored(_connection.Status)
            : Core.UI.PanelStatus.Clip(_connection.PresetsProblem ?? Lang.T("presets.none"), Core.UI.PanelStatus.NoticeWidth);

        // 下の段に文を出すか（出さないときは推薦の近道）。つながっていて知らせもなければ、何も書かずに近道を出す
        // （「接続済」は曲のことではないのに曲の行の近くに出て、紛らわしかった。2026-11-08）
        [UIValue("status-visible")]
        public bool StatusVisible => !_pinned || Notice != null || !string.IsNullOrEmpty(_flash)
            || _connection.Status.State != Core.Connection.ConnectionState.Ready || _shortcuts.Count == 0;

        [UIValue("shortcuts-visible")]
        public bool ShortcutsVisible => !StatusVisible;

        // 下の段を描き直す（文か近道か、近道の中身）
        private void NotifyStatus()
        {
            ResolveShortcuts();
            NotifyPropertyChanged(nameof(StatusRowText));
            NotifyPropertyChanged(nameof(StatusVisible));
            NotifyPropertyChanged(nameof(ShortcutsVisible));
            PaintShortcuts();
        }

        // 済んだことの短い知らせ。下の段に数秒だけ出して、接続の状態に戻す
        private string _flash;
        private int _flashId;

        private async void Flash(string text, float seconds = 3f)
        {
            _flash = text;
            NotifyStatus();
            var id = ++_flashId;
            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (id == _flashId)
            {
                _flash = null;
                NotifyStatus();
            }
        }

        [UIValue("status-hint")]
        public string StatusHint => Lang.T(_pinned ? "panel.refreshHint" : "panel.resetHint");

        private bool _refreshing;

        // 下の段を押した: 移動中なら最初の位置へ戻す。そうでなければ知らせを消して、接続を確かめ直す（別のボタンは置かない。2026-11-03）。
        // ペアリングは設定の「OctantCompass との接続」で行う
        [UIAction("status-click")]
        private async void StatusClick()
        {
            if (!_pinned)
            {
                ResetPlacement();
                return;
            }
            _message = "";
            _trials.ClearStatus();
            NotifyStatus();
            if (_refreshing)
            {
                return;
            }
            _refreshing = true;
            try
            {
                await _connection.RefreshNowAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[panel] could not check the connection again: {ex.Message}");
            }
            finally
            {
                _refreshing = false;
                NotifyStatus();
            }
        }

        [UIValue("mix-interactable")]
        public bool MixInteractable => !_busy && _mix != null;

        // 並びの一覧を出すときだけ: 一覧で選んでいる曲（曲名 / アーティスト [譜面作者] と難易度）
        [UIValue("detail-text")]
        public string DetailText
        {
            get
            {
                var item = _selected;
                if (item == null)
                {
                    return "";
                }
                var difficulty = item.Candidate.Reason?.Difficulty;
                var aim = difficulty?.ClearChance != null ? "  " + Lang.T("item.clearChance", new { chance = difficulty.ClearChance.Value.ToString("P0", CultureInfo.InvariantCulture) }) : "";
                return Core.UI.PanelStatus.Clip($"{Core.UI.DisplayText.Line(item.Title)} / {Core.UI.DisplayText.Line(item.Artist)} [{Core.UI.DisplayText.Line(item.Mapper)}]  {Lang.T("item.suggested", new { characteristic = item.Candidate.Characteristic, difficulty = item.Candidate.Difficulty })}{aim}", Core.UI.PanelStatus.NoticeWidth);
            }
        }

        private string MixName() => MixName(_mixPreset, _mixChoiceLabel);

        // 「種類の名前: choice の名前」
        private static string TypeName(Preset preset, string choiceLabel) => (preset?.Label ?? "") + (string.IsNullOrEmpty(choiceLabel) ? "" : ": " + choiceLabel);

        private string MixName(Preset preset, string choiceLabel)
        {
            var name = TypeName(preset, choiceLabel);
            return preset != null && PresetRules.UsesSeed(preset) && _mixSeed != null ? Lang.T("mix.nameFrom", new { song = _mixLabel, name }) : Lang.T("mix.name", new { name });
        }


        [UIValue("filter-text")]
        public string FilterText => _mix == null || _mix.Filter == Core.Mix.MixFilter.All ? Lang.T("filter.all") : FilterName(_mix.Filter);

        private static string FilterName(Core.Mix.MixFilter filter) => Lang.T(filter == Core.Mix.MixFilter.Familiar ? "filter.familiar" : filter == Core.Mix.MixFilter.New ? "filter.new" : "filter.all");

        private void HandleSelectionChanged(StandardLevelDetailViewController controller) => ReadSeedAndNotify();

        private void HandleContentChanged(StandardLevelDetailViewController controller, StandardLevelDetailViewController.ContentType type) => ReadSeedAndNotify();

        private void HandleConnectionChanged(Core.Connection.ConnectionStatus status)
        {
            NotifyStatus();
        }

        private void HandleSignalProblemChanged() => NotifyStatus();

        // 置いたものを確かめると「置いた」の印が消えるので、ミックスの並び・板の一覧・下見の行の「導入済み」を付け直す
        private void HandleDownloadsChanged()
        {
            var shown = (_mix?.All ?? Enumerable.Empty<RecommendationItem>()).Concat(_items).Concat(_draft?.Seen ?? Enumerable.Empty<RecommendationItem>());
            foreach (var item in shown.Distinct())
            {
                item.Installed = LevelLookup.IsInstalled(item.Hash);
            }
            FillList();
            FillDraftList();
            Notify();
            NotifyDraft();
        }

        private void ReadSeedAndNotify()
        {
            try
            {
                ReadSeed();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"could not read the selected chart: {ex.Message}");
                _seed = null;
                _songText = null;
            }
            NotifyPropertyChanged(nameof(SongText));
            NotifyStatus();
        }

        private void ReadSeed()
        {
            _seed = null;
            _songText = null;
            var level = _levelDetail != null ? _levelDetail.beatmapLevel : null;
            if (level == null)
            {
                return;
            }
            var mappers = level.allMappers != null && level.allMappers.Length > 0 ? $" [{Core.UI.DisplayText.Line(string.Join(", ", level.allMappers))}]" : "";
            var chart = _levelDetail.beatmapKey.beatmapCharacteristic != null ? "  " + Core.Maps.DifficultyLabel.Chart(_levelDetail.beatmapKey.beatmapCharacteristic.serializedName, _levelDetail.beatmapKey.difficulty.ToString()) : "";
            _songText = (Core.UI.DisplayText.Line(level.songName), Core.UI.DisplayText.Line(level.songAuthorName) + mappers + chart);
            var hash = Core.Maps.MapHash.Normalize(SongCore.Collections.GetCustomLevelHash(level.levelID));
            if (hash == null)
            {
                return;
            }
            var key = _levelDetail.beatmapKey;
            if (key.beatmapCharacteristic == null)
            {
                return;
            }
            _seed = new ChartRef { Hash = hash, Characteristic = key.beatmapCharacteristic.serializedName, Difficulty = key.difficulty.ToString() };
            _seedLabel = $"{Core.UI.DisplayText.Line(level.songName)}（{Core.Maps.DifficultyLabel.Chart(_seed.Characteristic, _seed.Difficulty)}）";
        }

        // ミックス: 種類（OctantCompass のプリセット）を小窓で選ぶ。起点は今選んでいる譜面
        private void MixClick()
        {
            if (ToastIfUnavailable())
            {
                return;
            }
            ReadSeed();
            var presets = ShownPresets();
            if (presets.Count == 0)
            {
                _message = _connection.PresetsProblem ?? Lang.T("presets.none");
                Notify();
                return;
            }
            _menuPresets = presets;
            _choosing = null;
            _choiceInputs = null;
            // 起点が要る種類は、譜面を選んでいないときは押せない
            var groups = presets.Any(p => !string.IsNullOrEmpty(p.Group)) ? presets.Select(p => p.Group).ToList() : null;
            ShowTiles(presets.Select(p => (p.Label, SeedBlocked(p) ? SeedBlockedHint : p.Hint, !SeedBlocked(p))).ToList(), groups);
            NotifyPropertyChanged(nameof(ChoiceSeed));
            _parserParams?.EmitEvent("show-choice");
        }

        private List<Preset> ShownPresets() => PresetRules.Shown(_connection.Presets?.Presets, true);

        private void HandlePresetsChanged() => Notify();

        // 曲の行: 選んでいる曲（曲名と、作者 [譜面作者] 難易度）。行ごと押すと種類の小窓が出る（♪ の印は、行を押せば足りるので置かない。2026-11-08）
        private (string Title, string Meta)? _songText;

        [UIValue("song-text")]
        public string SongText
        {
            get
            {
                if (_songText == null)
                {
                    return Lang.T("panel.noSong");
                }
                // 起点にできない曲は、2 行目を短い一文に替える（長い説明は幅 40 で途中で切れた。理由は灰色のマスの説明に出る。2026-11-08）
                var meta = _seed == null ? Lang.T("choice.seedUnusable") : _songText.Value.Meta;
                return $"{Core.UI.PanelStatus.Clip(_songText.Value.Title, SongTitleWidth)}\n<size=75%><color=#BBBBBB>{Core.UI.PanelStatus.Clip(meta, SongMetaWidth)}</color></size>";
            }
        }

        // 曲の行の文字の幅の目安（半角 1、全角 2。推測で決めた値: 幅 61 のマスに、大きさ 3 で約 30、その 75% で約 40）
        private const int SongTitleWidth = 32;
        private const int SongMetaWidth = 40;

        [UIValue("song-hint")]
        public string SongHint => Lang.T("panel.songHint");

        [UIAction("song-click")]
        private void SongClick() => MixClick();

        [UIComponent("song-plate")]
        private HMUI.ImageView _songPlate;

        // 曲の行（ランプは TrialLamp がこの右端に置く）と、その文。文の左右の余白（UI の単位）。右はランプ（7）の分をあける
        [UIObject("song-row")]
        private UnityEngine.GameObject _songRow;

        [UIComponent("song-label")]
        private ClickableText _songLabel;

        private const float SongRowPad = 3.5f;

        // 曲の行の左端の青い縦線。板が寂しく見えたので、行に締まりを出す（2026-11-08）。
        // 手札一覧の再生中の帯（DraftCell の playing-bar）と同じ太さ・色：角のない四角で、行の高さいっぱい。左端に隙間を空けずに付ける
        // 曲の行の地（song-plate）を写して作り、並び（stack）には加えずに左端に置く
        private const float SongAccentWidth = 0.8f;
        private static readonly UnityEngine.Color SongAccentColor = new UnityEngine.Color32(0x4C, 0x9A, 0xFF, 0xFF);

        private void AddSongAccent()
        {
            if (_songRow == null || _songPlate == null)
            {
                return;
            }
            try
            {
                var line = UnityEngine.Object.Instantiate(_songPlate.gameObject, _songRow.transform, false);
                line.name = "OctantPathSongAccent";
                var layout = line.GetComponent<UnityEngine.UI.LayoutElement>() ?? line.AddComponent<UnityEngine.UI.LayoutElement>();
                layout.ignoreLayout = true;
                var rect = (UnityEngine.RectTransform)line.transform;
                rect.anchorMin = new UnityEngine.Vector2(0f, 0f);
                rect.anchorMax = new UnityEngine.Vector2(0f, 1f);
                rect.pivot = new UnityEngine.Vector2(0f, 0.5f);
                rect.sizeDelta = new UnityEngine.Vector2(SongAccentWidth, 0f);
                rect.anchoredPosition = UnityEngine.Vector2.zero;
                var image = line.GetComponent<HMUI.ImageView>();
                image.sprite = DraftCell.SquareSprite();
                image.type = UnityEngine.UI.Image.Type.Simple;
                image.gradient = false;
                image.color = SongAccentColor;
                image.raycastTarget = false;
                // 文の下に描く（文を押せなくしない）
                line.transform.SetSiblingIndex(1);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[panel] could not add the song row line: {ex.Message}");
            }
        }
        private const float SongRowLampSpace = 8f;

        // 推薦の近道: 下の段の手札 3 つ（Core.UI.Shortcuts）。押すと、選んでいる曲を起点にその手札で下見を出す
        private List<Preset> _shortcuts = new List<Preset>();

        [UIComponent("shortcut-0-plate")] private HMUI.ImageView _shortcut0Plate;
        [UIComponent("shortcut-1-plate")] private HMUI.ImageView _shortcut1Plate;
        [UIComponent("shortcut-2-plate")] private HMUI.ImageView _shortcut2Plate;
        [UIComponent("shortcut-0-label")] private ClickableText _shortcut0Label;
        [UIComponent("shortcut-1-label")] private ClickableText _shortcut1Label;
        [UIComponent("shortcut-2-label")] private ClickableText _shortcut2Label;

        private HMUI.ImageView[] ShortcutPlates => new[] { _shortcut0Plate, _shortcut1Plate, _shortcut2Plate };
        private ClickableText[] ShortcutLabels => new[] { _shortcut0Label, _shortcut1Label, _shortcut2Label };

        [UIComponent("shortcut-0-mark")] private HMUI.ImageView _shortcut0Mark;
        [UIComponent("shortcut-1-mark")] private HMUI.ImageView _shortcut1Mark;
        [UIComponent("shortcut-2-mark")] private HMUI.ImageView _shortcut2Mark;

        private HMUI.ImageView[] ShortcutMarks => new[] { _shortcut0Mark, _shortcut1Mark, _shortcut2Mark };

        // 近道の名前の右の下向きの山形（押すと下に推薦の板が開く。2026-11-09）。大きさと、名前との隙間（UI の単位）
        private const float ShortcutMarkSize = 1.6f;
        private const float ShortcutMarkGap = 0.7f;

        // 山形を名前の右に置く。名前は真ん中寄せのまま、右の余白（margin）で山形の分だけ左へ寄せ、名前と山形の組を真ん中に見せる
        private static void PlaceShortcutMark(ClickableText label, HMUI.ImageView mark, UnityEngine.Color color)
        {
            if (label == null || mark == null)
            {
                return;
            }
            var room = ShortcutMarkSize + ShortcutMarkGap;
            label.margin = new UnityEngine.Vector4(0f, 0f, room, 0f);
            var width = string.IsNullOrEmpty(label.text) ? 0f : label.GetPreferredValues(label.text).x;
            var room0 = label.rectTransform.rect.width - room;
            if (room0 > 0f)
            {
                width = UnityEngine.Mathf.Min(width, room0);
            }
            var layout = mark.GetComponent<UnityEngine.UI.LayoutElement>() ?? mark.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            layout.ignoreLayout = true;
            var rect = mark.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
            rect.sizeDelta = new UnityEngine.Vector2(ShortcutMarkSize, ShortcutMarkSize);
            // 名前の真ん中は -room / 2。その右端から隙間をあけて山形の真ん中
            rect.anchoredPosition = new UnityEngine.Vector2(-room / 2f + width / 2f + ShortcutMarkGap + ShortcutMarkSize / 2f, 0f);
            mark.raycastTarget = false;
            mark.color = color;
            mark.gameObject.SetActive(!string.IsNullOrEmpty(label.text));
        }

        // 近道の手札の名前の幅の目安（半角 1、全角 2。推測で決めた値）
        private const int ShortcutLabelWidth = 14;

        private void ResolveShortcuts()
        {
            var settings = _settingsHolder.Current;
            var shown = _connection.Presets != null ? ShownPresets() : new List<Preset>();
            _shortcuts = Core.UI.Shortcuts.Resolve(settings.ShortcutPresets, settings.RecentPresets, shown);
        }

        // 近道のマスに手札の名前と説明を入れる。起点が要る手札は、曲を選んでいないと灰色にする（押すと知らせる）
        private void PaintShortcuts()
        {
            var plates = ShortcutPlates;
            var labels = ShortcutLabels;
            for (var i = 0; i < labels.Length; i++)
            {
                var preset = i < _shortcuts.Count ? _shortcuts[i] : null;
                if (plates[i] != null)
                {
                    plates[i].color = TileColor();
                }
                var label = labels[i];
                if (label == null)
                {
                    continue;
                }
                label.text = preset == null ? "" : Core.UI.PanelStatus.Clip(preset.Label, ShortcutLabelWidth);
                var usable = preset != null && !_busy && !SeedBlocked(preset);
                label.DefaultColor = usable ? TileText : TileDisabledText;
                label.color = label.DefaultColor;
                PlaceShortcutMark(label, ShortcutMarks[i], label.DefaultColor);
                var hint = label.GetComponent<HMUI.HoverHint>();
                if (hint != null)
                {
                    hint.text = preset == null ? "" : SeedBlocked(preset) ? SeedBlockedHint : preset.Hint ?? preset.Label;
                }
            }
        }

        [UIAction("shortcut-0-click")] private void Shortcut0Click() => ShortcutClick(0);
        [UIAction("shortcut-1-click")] private void Shortcut1Click() => ShortcutClick(1);
        [UIAction("shortcut-2-click")] private void Shortcut2Click() => ShortcutClick(2);

        private void ShortcutClick(int index)
        {
            if (_busy || index >= _shortcuts.Count || ToastIfUnavailable())
            {
                return;
            }
            ReadSeed();
            var preset = _shortcuts[index];
            if (SeedBlocked(preset))
            {
                Toast(SeedBlockedMessage);
                return;
            }
            Plugin.Log.Info($"[shortcut] {preset.Id}");
            PresetChosen(preset);
            // choice の入力がある手札は、種類の小窓で続けて選ばせる（PresetChosen が小窓の中身を替える）
            if (_choosing == preset)
            {
                NotifyPropertyChanged(nameof(ChoiceSeed));
                _parserParams?.EmitEvent("show-choice");
            }
        }

        // 今は押せない手札か。起点が必須なのに起点がない。または、起点を任意で使う手札（似た曲など）なのに、選んでいる曲が起点にできない
        // （オリジナル曲など）。後者は起点なしでも動くが、遊んだ履歴から選ぶことになり、選んでいる曲に似た曲だと思って押すと食い違う（2026-11-08）
        private bool SeedBlocked(Preset preset) =>
            _seed == null && (PresetRules.NeedsSeed(preset) || (_songText != null && PresetRules.UsesSeed(preset)));

        private string SeedBlockedHint => _songText != null ? Lang.T("choice.seedUnusable") : Lang.T("choice.needsSeed");

        // 押せない手札を押したときの知らせ。マスの説明（SeedBlockedHint）と同じ理由を出す（2026-11-08）
        private string SeedBlockedMessage => _songText != null ? Lang.T("choice.seedUnusable") : Lang.T("choice.needsSeedFirst");

        // 手札を使った: 最近使ったものの先頭に置く（近道の空いた枠を埋める）
        private void RememberPreset(Preset preset)
        {
            var recent = Core.UI.Shortcuts.Touch(_settingsHolder.Current.RecentPresets, preset?.Id);
            if (!recent.SequenceEqual(_settingsHolder.Current.RecentPresets ?? new List<string>()))
            {
                _settingsHolder.Save("recentPresets", new Newtonsoft.Json.Linq.JArray(recent));
            }
        }

        // 種類を選んだ: 起点は今選んでいる譜面（起点を使わない種類でも、あれば覚えておく）。choice の入力があれば続けて選ばせる
        private void PresetChosen(Preset preset)
        {
            if (_busy)
            {
                return;
            }
            if (SeedBlocked(preset))
            {
                _parserParams?.EmitEvent("hide-choice");
                _message = SeedBlockedMessage;
                Notify();
                return;
            }
            // 起点は今選んでいる譜面だけ。選んでいなければ、前に選んでいた曲を起点に残さない（ホームの手札が消したのと同じ）
            _mixSeed = _seed;
            _mixLabel = _seed != null ? _seedLabel : null;
            // 種類の一覧の小窓を外を押して閉じると、一覧（_menuPresets）が残る。残ったままだと、この手札の choice を選んだときに
            // 一覧の別の手札が始まる
            _menuPresets = null;
            RememberPreset(preset);
            var choices = PresetRules.Choices(preset);
            _mixUsesSeed = PresetRules.UsesSeed(preset);
            _draftingPreset = preset;
            if (choices.Count == 0)
            {
                ShowDraftModal();
                BuildMix(preset, null, null);
                return;
            }
            _choosing = preset;
            _choiceInputs = choices;
            _chosen = new Dictionary<string, string>();
            _chosenLabels = new List<string>();
            ShowNextChoice();
        }

        private void ShowNextChoice()
        {
            var input = _choiceInputs[_chosen.Count];
            ShowTiles(input.Options.Select(o => (o.Label ?? o.Value, (string)null, true)).ToList());
            NotifyPropertyChanged(nameof(ChoiceSeed));
        }

        private void ChoiceSelect(TableView tableView, int index)
        {
            if (_menuPresets != null)
            {
                var presets = _menuPresets;
                _menuPresets = null;
                if (index >= 0 && index < presets.Count)
                {
                    PresetChosen(presets[index]);
                }
                return;
            }
            if (_choosing == null || _choiceInputs == null || _chosen == null || _chosen.Count >= _choiceInputs.Count)
            {
                return;
            }
            var input = _choiceInputs[_chosen.Count];
            if (index < 0 || index >= input.Options.Count)
            {
                return;
            }
            _chosen[input.Name] = input.Options[index].Value;
            _chosenLabels.Add(input.Options[index].Label ?? input.Options[index].Value);
            if (_chosen.Count < _choiceInputs.Count)
            {
                ShowNextChoice();
                return;
            }
            ShowDraftModal();
            var preset = _choosing;
            _choosing = null;
            BuildMix(preset, _chosen, string.Join("・", _chosenLabels));
        }

        // 並びの見せ方: 全部 → おなじみ → 新しい曲（並べ替えない）
        [UIAction("filter-click")]
        private void FilterClick()
        {
            if (_mix == null)
            {
                return;
            }
            _mix.Filter = _mix.Filter == Core.Mix.MixFilter.All ? Core.Mix.MixFilter.Familiar
                : _mix.Filter == Core.Mix.MixFilter.Familiar ? Core.Mix.MixFilter.New
                : Core.Mix.MixFilter.All;
            _items = _mix.Visible();
            FillList();
            Notify();
        }

        private async void BuildMix(Preset preset, Dictionary<string, string> choices, string choiceLabel)
        {
            if (_busy || (PresetRules.NeedsSeed(preset) && _mixSeed == null))
            {
                return;
            }
            _draft = null;
            _draftSelected = null;
            _draftMessage = "";
            FillDraftList();
            SetBusy(true, "");
            try
            {
                var draftCount = Core.Mix.MixDraftRules.Size(_settingsHolder.Current.MixDraftCount);
                var request = Core.Mix.MixDraftRules.FirstRequest(preset, _mixSeed, choices, draftCount, _settingsHolder.Current.DifficultyCeiling);
                var result = await _recommendations.RequestAsync(request, _lifetime.Token);
                if (result.Error != null)
                {
                    // 失敗したら今の並びを残し、下見の小窓は閉じて知らせだけを出す
                    _message = result.Error;
                    _draftMessage = result.Error;
                    HideModals();
                    Toast(result.Error);
                    Plugin.Log.Info($"[mix] {preset.Id} failed: {result.Error}");
                    return;
                }
                _draftPreset = preset;
                _draftChoiceLabel = choiceLabel ?? PresetRules.GenreName(preset, result.Items.Select(i => i.Candidate.Reason));
                var displayable = RecommendationDisplay.Shown(result.Items);
                if (displayable.Count < result.Items.Count)
                {
                    Plugin.Log.Info($"[mix] left out {result.Items.Count - displayable.Count} candidates BeatSaver does not know");
                }
                _draft = new Core.Mix.MixDraft<RecommendationItem>(displayable, draftCount);
                _draftSeed = request.Seed;
                _draftChoices = choices != null ? new Dictionary<string, string>(choices) : null;
                _draftSelected = null;
                _draftMessage = Lang.T(_draft.Picked.Count == 0 ? "recommend.none" : "draft.hint");
                Plugin.Log.Info($"[mix] {preset.Id} requestId={result.Response?.RequestId} seed={request.Seed?.Hash} {request.Seed?.Characteristic} {request.Seed?.Difficulty} choices={(choices == null ? "" : string.Join(",", choices.Select(c => c.Key + "=" + c.Value)))} candidates={_draft.Picked.Count} error={result.Error}");
                FillDraftList();
                foreach (var shown in _draft.Picked)
                {
                    _signals.RecordRecommendation(shown.RequestId, shown.Preset, shown.Candidate.Reason?.Hand, shown.Hash, shown.Candidate.Characteristic, shown.Candidate.Difficulty, "shown");
                }
                _ = LoadDraftCoversAsync();
                // 1 曲目の試聴をすぐ流し、取り終えたら残りの曲の試聴を先読みする
                if (_draft.Picked.Count > 0)
                {
                    DraftSelect(null, 0);
                }
                PrefetchDraftPreviews();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                _message = Lang.T("mix.failed", new { error = ex.Message });
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        [UIAction("select-cell")]
        private void SelectCell(TableView tableView, int index)
        {
            var item = index >= 0 && index < _items.Count ? _items[index] : null;
            if (item == null)
            {
                return;
            }
            // 選んだだけでは取らない。入っている曲（前もって置いたものを含む）は曲選択で選び、ほかは試聴だけを流す
            _selected = item;
            _mix?.Select(item);
            if (item.Installed || _downloads.IsPlaced(item.Hash) || _downloads.IsInFlight(item.Hash))
            {
                _ = PlayItemAsync(item);
                return;
            }
            if (item.Version?.PreviewUrl != null)
            {
                _preview.Play(item.Version.PreviewUrl);
            }
            _message = _downloads.IsAvailable(item) ? "" : Lang.T("item.unavailable");
            FillList();
            Notify();
        }

        [UIAction("play-click")]
        private void PlayClick()
        {
            if (_selected != null)
            {
                _ = PlayItemAsync(_selected);
            }
        }

        [UIValue("play-interactable")]
        public bool PlayInteractable => !_busy && _selected != null && !_selected.Installed && !_downloads.IsPlaced(_selected.Hash) && !_downloads.IsInFlight(_selected.Hash) && _downloads.IsAvailable(_selected);

        // 下見の行の状態の文
        private string DraftState(RecommendationItem i) => i.Installed ? "  " + Lang.T("item.installed") : _downloads.IsInFlight(i.Hash) ? "  " + Lang.T("item.downloading") : _downloads.IsPlaced(i.Hash) ? "  " + Lang.T("item.downloaded") : _downloads.IsAvailable(i) ? "" : "  " + Lang.T("item.cannotDownload");

        // 下見の行の ▶ を押せるか。取得中の曲でなければ、ゲームに入っている曲・取得済みの曲・これから取得できる曲のどれでも押せる
        private bool CanPlayDraft(RecommendationItem i) => !_busy && !_downloads.IsInFlight(i.Hash) && (i.Installed || _downloads.IsPlaced(i.Hash) || _downloads.IsAvailable(i));

        // 下見の曲の試聴を、並んだ順に先読みする（選んだ曲を取り終えてから 1 曲ずつ）。下見が閉じるか変わったらやめる
        private void PrefetchDraftPreviews()
        {
            var draft = _draft;
            if (draft == null)
            {
                return;
            }
            var urls = draft.Picked.Select(i => i.Version?.PreviewUrl).Where(u => u != null).ToList();
            _preview.Prefetch(urls, () => draft == _draft && _draftModal != null && _draftModal.isShown);
        }

        // 下見の行の ▶ を押したとき。その曲がゲームに入っていなければ、取得して（取得済みならそのまま）ゲームに読み込ませ、
        // 曲選択でその曲を選ぶ（PLAY はユーザーが押す）。取得するときは進み具合を下見の帯に出す。
        // ユーザーが選んで取った曲なので、台帳には「手動」の枠で残す。
        // 下見の小窓は閉じるが下見は残し、その曲を遊んで曲選択に戻ったら小窓を開き直す（TickAdvance）
        private async void PlayDraftItem(RecommendationItem item)
        {
            if (item == null || _busy || _draft == null || _downloads.IsInFlight(item.Hash))
            {
                return;
            }
            var draft = _draft;
            var recorded = false;
            if (!item.Installed)
            {
                var download = !_downloads.IsPlaced(item.Hash);
                if (download)
                {
                    // 「作成」と同じ帯を使う
                    _creating = true;
                    StartCreateProgress(1);
                }
                SetBusy(true, "");
                _draftMessage = Lang.T(download ? "item.downloadingTitle" : "item.loadingTitle", new { title = item.Title });
                NotifyDraft();
                try
                {
                    // 取得したら（取得済みなら取らずに）ゲームに読み込ませ、譜面が読めたかを確かめる
                    var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Manual, true, _lifetime.Token,
                        received: (got, fraction) => _createReceived[got.Hash] = fraction);
                    FinishCreateDownloads();
                    var outcome = outcomes?.FirstOrDefault();
                    if (outcome != null && outcome.Path != null)
                    {
                        _signals.RecordRecommendation(item.RequestId, item.Preset, item.Candidate.Reason?.Hand, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "selected");
                        recorded = true;
                    }
                    if (outcome == null || outcome.Error != null || !item.Installed)
                    {
                        _draftMessage = outcome == null ? Lang.T("download.notWhilePlaying") : Lang.T("download.failed", new { error = outcome.Error ?? Lang.T("download.notLoaded") });
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex);
                    _draftMessage = Lang.T("download.failed", new { error = ex.Message });
                    return;
                }
                finally
                {
                    if (download)
                    {
                        _creating = false;
                        ShowCreateTrack();
                    }
                    SetBusy(false, null);
                    NotifyDraft();
                }
                if (draft != _draft || _draftModal == null || !_draftModal.isShown)
                {
                    // 読み直している間に下見を閉じたか作り直した
                    return;
                }
            }
            _preview.Stop();
            _preview.StopPrefetch();
            HideModals();
            var selected = SongSelect.Select(item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, null);
            // 選べた曲だけ、遊び終えたら下見を開き直す
            _draftResumeHash = selected ? item.Hash : null;
            _draftResumeArmed = false;
            if (selected && !recorded)
            {
                // 入っていた曲は取得の分岐を通らないので、ここで記録する
                _signals.RecordRecommendation(item.RequestId, item.Preset, item.Candidate.Reason?.Hand, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "selected");
            }
            SongSelect.RedrawCovers(new[] { item.Hash });
            _message = selected ? "" : Lang.T("select.failed");
            if (selected)
            {
                Flash(Lang.T("status.selected"));
            }
            Plugin.Log.Info($"[mix] playing {item.Hash} from the draft (selected={selected})");
            Notify();
        }

        // 下見から 1 曲だけ遊んだ曲。その曲を始めたら Armed にし、遊び終えて曲選択に戻ったら下見の小窓を開き直す
        private string _draftResumeHash;
        private bool _draftResumeArmed;
        private bool _draftResumePending;

        // 下見の曲を外すだけ（入れ替えない）。下見の曲数が 1 つ減る
        private void DiscardItem(RecommendationItem removed)
        {
            if (removed == null || _draft == null || _busy || !_draft.Remove(removed))
            {
                return;
            }
            _signals.RecordRecommendation(removed.RequestId, removed.Preset, removed.Candidate.Reason?.Hand, removed.Hash, removed.Candidate.Characteristic, removed.Candidate.Difficulty, "skipped");
            if (removed == _draftSelected)
            {
                _draftSelected = null;
                _preview.Stop();
            }
            _draftMessage = "";
            FillDraftList();
            NotifyDraft();
        }

        // 入れ替え用の次の曲を、下見の一番下に足す（下見の曲数が 1 つ増える）。足した曲を選び、試聴を流す。
        // 入れ替え用が尽きていれば、追加を受け取る（届いたら、もう一度押すと足せる）
        private void AddItem()
        {
            if (_draft == null || _busy)
            {
                FillDraftList();
                return;
            }
            var added = _draft.Add();
            if (added == null)
            {
                _draftMessage = Lang.T(_draft.Full ? "draft.full" : "draft.fetchingSwaps");
                FillDraftList();
                NotifyDraft();
                if (!_draft.Full)
                {
                    _ = TopUpDraftAsync();
                }
                return;
            }
            _signals.RecordRecommendation(added.RequestId, added.Preset, added.Candidate.Reason?.Hand, added.Hash, added.Candidate.Characteristic, added.Candidate.Difficulty, "shown");
            _draftSelected = added;
            _draftMessage = "";
            if (added.Version?.PreviewUrl != null)
            {
                _preview.Play(added.Version.PreviewUrl);
            }
            FillDraftList();
            // 足した行と「曲を足す」の行が見えるように、一覧の一番下まで送る
            _draftList?.TableView.ScrollToCellWithIdx(_draftCells.Count - 1, TableView.ScrollPositionType.End, false);
            NotifyDraft();
            _ = LoadDraftCoversAsync();
            if (_draft.RunningLow)
            {
                _ = TopUpDraftAsync();
            }
        }

        private bool _toppingUp;
        private ChartRef _draftSeed;
        private Dictionary<string, string> _draftChoices;

        // 同じ種類・起点・選択肢で、今まで出したものを除いて追加を受け取り、入れ替え用に足す
        private async System.Threading.Tasks.Task TopUpDraftAsync()
        {
            var draft = _draft;
            if (_toppingUp || draft == null || _draftPreset == null)
            {
                return;
            }
            _toppingUp = true;
            try
            {
                var request = Core.Mix.MixDraftRules.TopUpRequest(_draftPreset, _draftSeed, _draftChoices, Core.Mix.MixDraftRules.Size(_settingsHolder.Current.MixDraftCount), draft.Seen.Select(i => i.Hash), _settingsHolder.Current.DifficultyCeiling);
                var result = await _recommendations.RequestAsync(request, _lifetime.Token);
                if (draft != _draft)
                {
                    return;
                }
                var added = result.Error == null ? draft.AddReserve(RecommendationDisplay.Shown(result.Items)) : 0;
                Plugin.Log.Info($"[mix] topped up the draft reserve with {added} (error={result.Error})");
                if (_draftMessage == Lang.T("draft.fetchingSwaps"))
                {
                    _draftMessage = added > 0 ? "" : Lang.T("draft.noMoreSwaps");
                }
                NotifyDraft();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[mix] could not top up the draft: {ex.Message}");
            }
            finally
            {
                _toppingUp = false;
            }
        }

        // 下見の曲を外し、入れ替え用の次の曲をその場所に入れる
        private void ReplaceItem(RecommendationItem removed)
        {
            if (removed == null || _draft == null || _busy)
            {
                return;
            }
            var wasSelected = removed == _draftSelected;
            var next = _draft.Replace(removed);
            _signals.RecordRecommendation(removed.RequestId, removed.Preset, removed.Candidate.Reason?.Hand, removed.Hash, removed.Candidate.Characteristic, removed.Candidate.Difficulty, "skipped");
            if (next != null)
            {
                _signals.RecordRecommendation(next.RequestId, next.Preset, next.Candidate.Reason?.Hand, next.Hash, next.Candidate.Characteristic, next.Candidate.Difficulty, "shown");
            }
            // 入れ替えた曲を選んだ状態にして、その試聴をすぐ流す
            _draftSelected = next;
            _draftMessage = next != null ? "" : Lang.T("draft.fetchingSwaps");
            if (next?.Version?.PreviewUrl != null)
            {
                _preview.Play(next.Version.PreviewUrl);
            }
            else if (wasSelected)
            {
                _preview.Stop();
            }
            FillDraftList();
            NotifyDraft();
            if (next != null)
            {
                _ = LoadDraftCoversAsync();
            }
            // 入れ替え用が少なくなったら、裏で追加を受け取る
            if (_draft.RunningLow)
            {
                _ = TopUpDraftAsync();
            }
        }

        // 下見を確定する: 並んでいる曲だけを取得し、一時的なプレイリストにして曲選択で開く
        private async System.Threading.Tasks.Task CreateMixAsync()
        {
            var picked = _draft.Picked.ToList();
            var targets = picked.Where(i => !i.Installed && _downloads.IsAvailable(i)).ToList();
            // 小窓は開いたまま進み具合を見せ、曲選択を開き直す直前にアニメーションなしで閉じる（アニメーションの途中で開き直すと前の画面の上に残った）
            _preview.Stop();
            _creating = true;
            StartCreateProgress(targets.Count);
            SetProgress(0, targets.Count, targets.Count > 0 ? Lang.T("create.downloading", new { done = 0, total = targets.Count }) : Lang.T("create.makingPlaylist"));
            SetBusy(true, "");
            // 受け取った割合が何回届いたか（帯が伸びないと言われたときに、届いていないのかを見分ける）
            var reports = 0;
            try
            {
                _preview.Stop();
                if (targets.Count > 0)
                {
                    var outcomes = await _downloads.InstallManyAsync(targets, Core.Maps.FrameEntry.Mix, true, _lifetime.Token,
                        (done, total) => SetProgress(done, total, done < total ? Lang.T("create.downloading", new { done, total }) : Lang.T("create.loadingSongs")),
                        (item, fraction) =>
                        {
                            System.Threading.Interlocked.Increment(ref reports);
                            _createReceived[item.Hash] = fraction;
                        });
                    FinishCreateDownloads();
                    if (outcomes == null)
                    {
                        _message = _draftMessage = Lang.T("download.notWhilePlaying");
                        return;
                    }
                    foreach (var item in picked)
                    {
                        item.Installed = LevelLookup.IsInstalled(item.Hash);
                    }
                    Plugin.Log.Info($"[mix] created: downloaded {outcomes.Count(o => o.Error == null)} of {targets.Count} in {UnityEngine.Time.unscaledTime - _createStartedAt:0.0} s with {reports} progress reports");
                }
                var ready = picked.Where(i => i.Installed).ToList();
                if (ready.Count == 0)
                {
                    _message = _draftMessage = Lang.T("create.nothingDownloaded");
                    return;
                }
                _mixPreset = _draftPreset;
                _mixChoiceLabel = _draftChoiceLabel;
                _draft = null;
                _mix = new Core.Mix.MixQueue<RecommendationItem>(ready, i => i.Installed);
                _items = _mix.Visible();
                _selected = ready[0];
                _mix.Select(_selected);
                HideModals();
                var pack = _playlist.WriteCurrentMix(MixName(), ready.Select(i => (i.Hash, i.Title, i.Map?.Key, i.Mapper)).ToList());
                _mixPack = pack;
                _mixPlaylistStale = false;
                var opened = pack != null && SongSelect.OpenPack(pack, _selected.Hash, _selected.Candidate.Characteristic, _selected.Candidate.Difficulty);
                var missing = picked.Count - ready.Count;
                // 開けたなら曲選択に出ているので、板には何も書かない。開けなかったときと、取れなかった曲があるときだけ書く
                _message = (opened ? "" : Lang.T("create.made")) + (missing > 0 ? Lang.T("create.missing", new { missing }) : "");
                if (opened && missing == 0)
                {
                    Flash(Lang.T("status.opened"));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                _message = _draftMessage = Lang.T("create.failed", new { error = ex.Message });
            }
            finally
            {
                _creating = false;
                ShowCreateTrack();
                SetProgress(0, 0, _draftMessage);
                FillList();
                SetBusy(false, null);
            }
        }

        // 並びの曲にする: 入っていなければ取ってから、曲選択でその曲を選ぶ。そのあと先の曲を前もって取る。
        // inMix が false のとき（ミックスの外で遊んだ曲の結果画面で選んだ曲）は、今の一覧で選ぶだけ。並びの ▶ は動かさない
        private async System.Threading.Tasks.Task PlayItemAsync(RecommendationItem item, bool inMix = true)
        {
            if (inMix)
            {
                _selected = item;
                _mix?.Select(item);
            }
            Notify();
            if (!item.Installed)
            {
                _message = "";
                FillList();
                Notify();
                // 取得中なら、その取得が終わるのを待ってから、今すぐ使うので読み直して確かめる
                var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Mix, true, _lifetime.Token);
                var outcome = outcomes?.FirstOrDefault();
                if (outcome == null || outcome.Error != null || !item.Installed)
                {
                    _message = outcome == null ? Lang.T("download.notWhilePlaying") : Lang.T("download.failed", new { error = outcome.Error ?? Lang.T("download.notLoaded") });
                    FillList();
                    Notify();
                    return;
                }
                if (inMix && _selected != item)
                {
                    // 待っている間に別の曲を選んだ
                    return;
                }
            }
            if (!inMix)
            {
                _message = SelectedOrFailed(SongSelect.Select(item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, null));
                SongSelect.RedrawCovers(new[] { item.Hash });
                FillList();
                Notify();
                return;
            }
            if (_mix != null && _mixPlaylistStale)
            {
                // 結果画面で選んだ曲を、ミックスのプレイリストにも入れる（入っていないと一覧で選べず、開き直しになる）
                _mixPack = _playlist.WriteCurrentMix(MixName(), _mix.All.Select(i => (i.Hash, i.Title, i.Map?.Key, i.Mapper)).ToList()) ?? _mixPack;
                _mixPlaylistStale = false;
            }
            _message = SelectedOrFailed(SongSelect.Select(item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, _mix != null ? _mixPack : null));
            SongSelect.RedrawCovers(new[] { item.Hash });
            FillList();
            Notify();
            _ = PrefetchAsync();
        }

        // 前もって取ると SongCore が曲の一覧を作り直し、曲選択の選択が外れる。そのときは今の曲を選び直す
        private async void HandleLevelPacksRefreshed()
        {
            // 曲選択の一覧が作り直されるのを待ってから選ぶ
            await System.Threading.Tasks.Task.Delay(500);
            var item = _selected;
            if (item == null || !item.Installed || _levelSelection == null || !_levelSelection.isActivated)
            {
                return;
            }
            // 選択が外れたときだけ選び直す。ユーザーが別の曲を選んでいたら、それを上書きしない
            var current = _levelSelection.beatmapLevel;
            if (current != null)
            {
                return;
            }
            Plugin.Log.Info($"[mix] reselecting {item.Hash} after the song list was rebuilt");
            SongSelect.SelectInCurrentList(item.Hash);
            SongSelect.RedrawCovers(new[] { item.Hash });
        }

        // 今の曲から先の、まだ入っていない曲を前もって取る
        private async System.Threading.Tasks.Task PrefetchAsync()
        {
            if (_mix == null || !_settingsHolder.Current.AutoDownload)
            {
                return;
            }
            var targets = _mix.ToPrefetch(_settingsHolder.Current.MixPrefetchCount).Where(_downloads.IsAvailable).ToList();
            if (targets.Count == 0)
            {
                return;
            }
            // 置くところまで。読み直しは、遊び終えて戻ったときか、その曲を使うときにまとめて行う（画面を揺らさない）
            var outcomes = await _downloads.InstallManyAsync(targets, Core.Maps.FrameEntry.Mix, false, _lifetime.Token);
            if (outcomes != null)
            {
                Plugin.Log.Info($"[mix] prefetched {outcomes.Count(o => o.Error == null)} of {targets.Count} (placed, not loaded yet)");
                FillList();
            }
        }

        // 遊び終わって曲選択に戻ったら、並びの次の曲へ進む
        private void HandleTransitionDidFinish(GameScenesManager.SceneTransitionType type, ScenesTransitionSetupDataSO setupData, DiContainer container)
        {
            if (setupData is StandardLevelScenesTransitionSetupDataSO standard)
            {
                _lastPlayedHash = Core.Maps.LevelIds.Hash(standard.beatmapKey.levelId);
                _lastPlayedChart = _lastPlayedHash == null || standard.beatmapKey.beatmapCharacteristic == null ? null
                    : new ChartRef { Hash = _lastPlayedHash, Characteristic = standard.beatmapKey.beatmapCharacteristic.serializedName, Difficulty = standard.beatmapKey.difficulty.ToString() };
                _resultsPending = true;
                _playedInMix = MixItemInMixList(_lastPlayedHash);
                if (_playedInMix != null && _playedInMix != _selected)
                {
                    // ミックスのプレイリストで、ゲームの一覧から並びの別の曲を選んで始めた。そこを今の曲にする
                    _selected = _playedInMix;
                    _mix.Select(_selected);
                        FillList();
                    Notify();
                }
                // 下見から遊んだ曲を始めた。ほかの曲を始めたら、下見には戻らない
                _draftResumeArmed = _draftResumeHash != null && _draftResumeHash == _lastPlayedHash;
                if (!_draftResumeArmed)
                {
                    _draftResumeHash = null;
                }
                _draftResumePending = false;
                // 前の結果画面のおすすめは出さない
                _resultsPanel.Show("", new List<RecommendationItem>(), null);
                _resultsPlayed = null;
                _advancePending = false;
                _pendingPick = null;
                return;
            }
            if (type == GameScenesManager.SceneTransitionType.Pop && _draftResumeArmed)
            {
                _draftResumeArmed = false;
                _draftResumePending = true;
            }
            if (type == GameScenesManager.SceneTransitionType.Pop && _resultsPending)
            {
                _resultsPending = false;
                var played = _lastPlayedChart;
                _lastPlayedChart = null;
                if (played != null)
                {
                    _ = LoadResultsAsync(played);
                }
                else
                {
                    // カスタム曲でない（hash がない）譜面。推薦は問い合わせず、出せないことだけを出す
                    _resultsPanel.ShowUnavailable(Lang.T("next.titlePlain"), Lang.T("next.notCustom"));
                }
            }
            if (type != GameScenesManager.SceneTransitionType.Pop || _lastPlayedHash == null || _playedInMix == null)
            {
                return;
            }
            _lastPlayedHash = null;
            // 自動で次へ進むのを切っているときは、結果画面で選んだ曲だけ選ぶ（ChooseFromResults）
            _advancePending = _settingsHolder.Current.MixAutoAdvance;
        }

        // 曲選択がミックスのプレイリストを表示しているとき、その hash のミックスの曲（ほかは null）
        private RecommendationItem MixItemInMixList(string hash)
        {
            if (_mix == null || _mixPack == null || hash == null || _levelSelection == null)
            {
                return null;
            }
            var shown = _levelSelection.selectedBeatmapLevelPack;
            if (shown == null || shown.packID != _mixPack.packID)
            {
                return null;
            }
            return _mix.All.FirstOrDefault(i => i.Hash == hash);
        }

        [UIValue("auto-next-hint")]
        public string AutoNextHint => Lang.T(_settingsHolder.Current.MixAutoAdvance ? "panel.autoNextOnHint" : "panel.autoNextOffHint");

        [UIComponent("auto-next-plate")]
        private HMUI.ImageView _autoNextPlate;

        [UIComponent("auto-next-icon")]
        private ClickableImage _autoNextIcon;

        [UIComponent("auto-next-dot")]
        private HMUI.ImageView _autoNextDot;

        [UIAction("auto-next-click")]
        private void AutoNextClick()
        {
            _settingsHolder.Save("mixAutoAdvance", !_settingsHolder.Current.MixAutoAdvance);
            ShowAutoNext();
        }

        // 札の色を今の設定に合わせる
        private void ShowAutoNext()
        {
            PaintTile(_autoNextPlate, _autoNextIcon, _autoNextDot, _settingsHolder.Current.MixAutoAdvance, true);
            NotifyPropertyChanged(nameof(AutoNextHint));
        }

        [UIValue("ceiling-hint")]
        public string CeilingHint => Lang.T(_settingsHolder.Current.DifficultyCeiling ? "panel.ceilingOnHint" : "panel.ceilingOffHint");

        [UIValue("ceiling-text")]
        public string CeilingText => Lang.T(_settingsHolder.Current.DifficultyCeiling ? "draft.ceilingOn" : "draft.ceilingOff");

        [UIComponent("ceiling-plate")]
        private HMUI.ImageView _ceilingPlate;

        [UIComponent("ceiling-icon")]
        private ClickableImage _ceilingIcon;

        [UIComponent("ceiling-dot")]
        private HMUI.ImageView _ceilingDot;

        // 浮かぶ板の上限の札。下見の小窓が開いていれば、下見のボタンと同じく下見を取り直す（2026-10-30）
        [UIAction("ceiling-click")]
        private void CeilingClick()
        {
            ToggleCeiling();
            if (_draftModal != null && _draftModal.gameObject.activeInHierarchy)
            {
                RedrawDraft();
            }
        }

        // 下見の小窓のボタン: 切り替えて、同じ種類・選択肢で下見を取り直す（今の起点で）
        [UIAction("draft-ceiling")]
        private void DraftCeiling()
        {
            ToggleCeiling();
            RedrawDraft();
        }

        private void ToggleCeiling()
        {
            _settingsHolder.Save("difficultyCeiling", !_settingsHolder.Current.DifficultyCeiling);
            ShowCeiling();
        }

        private void RedrawDraft()
        {
            if (_draftPreset != null && !_busy)
            {
                _preview.Stop();
                _preview.StopPrefetch();
                BuildMix(_draftPreset, _draftChoices, _draftChoiceLabel);
            }
        }

        // 上限の札の色と文字を今の設定に合わせる（「自動で次へ」の札と同じ色）
        private void ShowCeiling()
        {
            PaintTile(_ceilingPlate, _ceilingIcon, _ceilingDot, _settingsHolder.Current.DifficultyCeiling, true);
            NotifyPropertyChanged(nameof(CeilingHint));
            NotifyPropertyChanged(nameof(CeilingText));
        }

        // 板全体の外側の角の半径（UI の単位。推測で決めた値）
        private const float TileCornerRadius = 2f;

        // マスの色。ゲームの曲選択の左のタブに寄せる: 切は暗い地に白っぽい文字、入は少し明るい地に青い文字、押せないときは灰色の文字。
        // 地の濃さは設定の「推薦の板の不透明度」（panelOpacity。結果画面の板の背景と同じ値）
        // マスの地はオンでもオフでも同じ暗い色。オン・オフは文字の色だけで示す（地の色が何種類も混ざると締まりがなかった、2026-11-08）
        private static readonly UnityEngine.Color TileColorBase = new UnityEngine.Color(0.08f, 0.08f, 0.11f, 1f);
        // 色の意味は 1 つずつ（2026-11-09）: 青 = 入っている（切り替えだけ）、白 = 押せる、薄い灰 = 切り替えの切、暗い灰 = いま押せない。
        // 切り替えの入は、色に加えて下端の点でも示す（色だけに頼らない）
        private static readonly UnityEngine.Color TileOnText = new UnityEngine.Color(0f, 0.75f, 1f, 1f);
        private static readonly UnityEngine.Color TileText = new UnityEngine.Color(0.92f, 0.92f, 0.94f, 1f);
        private static readonly UnityEngine.Color TileOffText = new UnityEngine.Color(0.7f, 0.7f, 0.74f, 1f);
        private static readonly UnityEngine.Color TileDisabledText = new UnityEngine.Color(0.36f, 0.36f, 0.39f, 1f);

        private float TileAlpha => PanelLook.Opacity(_settingsHolder.Current.PanelOpacity);

        private UnityEngine.Color TileColor()
        {
            var color = TileColorBase;
            color.a = TileAlpha;
            return color;
        }

        // 上の段のマス。icon は入なら青、切なら薄い灰（offColor）、押せないなら暗い灰。dot は入のときだけ出す（null ならない）
        private void PaintTile(HMUI.ImageView plate, ClickableImage icon, HMUI.ImageView dot, bool on, bool enabled, UnityEngine.Color? offColor = null)
        {
            if (plate != null)
            {
                plate.color = TileColor();
            }
            if (icon != null)
            {
                icon.DefaultColor = !enabled ? TileDisabledText : on ? TileOnText : offColor ?? TileOffText;
                // 手を当てたときは少し明るく（押せないときは変えない）
                icon.HighlightColor = enabled ? UnityEngine.Color.Lerp(icon.DefaultColor, UnityEngine.Color.white, 0.5f) : icon.DefaultColor;
                icon.color = icon.DefaultColor;
            }
            if (dot != null)
            {
                PlaceDot(dot);
                dot.color = TileOnText;
                dot.raycastTarget = false;
                dot.gameObject.SetActive(on && enabled);
            }
        }

        // 入の印の点の大きさと、マスの下端からの高さ（UI の単位）
        private const float DotSize = 0.7f;
        private const float DotLift = 0.3f;

        // 点はマスの並びから外し、下端の真ん中に置く（絵の位置は動かさない）。
        // BSML の余白（pad-bottom）は整数しか読めず、0.35 と書くと板ごと作れなかった（2026-11-10）
        private static void PlaceDot(HMUI.ImageView dot)
        {
            var layout = dot.GetComponent<UnityEngine.UI.LayoutElement>() ?? dot.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            layout.ignoreLayout = true;
            var rect = dot.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new UnityEngine.Vector2(0.5f, 0f);
            rect.sizeDelta = new UnityEngine.Vector2(DotSize, DotSize);
            rect.anchoredPosition = new UnityEngine.Vector2(0f, DotLift);
        }

        private void PaintTiles()
        {
            ShowSave();
            ShowAutoNext();
            ShowCeiling();
            ShowPin();
            if (_topGapPlate != null)
            {
                _topGapPlate.color = TileColor();
            }
            if (_statusPlate != null)
            {
                _statusPlate.color = TileColor();
            }
            // 曲の行は、上下の段のマスより明るく青みのある地にして、板の主役に見せる（灰色だけだと 9 枚のマスが同じ重さに並んで締まりがなかった、2026-11-08）
            if (_songPlate != null)
            {
                // 左（青い線の側）が明るく、右へ暗くなる横のグラデーション。頂点の色を変えるだけなので重くはない
                _songPlate.color = new UnityEngine.Color(1f, 1f, 1f, TileAlpha);
                _songPlate.color0 = SongRowLeftColor;
                _songPlate.color1 = SongRowRightColor;
                _songPlate.gradient = true;
            }
            PaintShortcuts();
        }

        private static readonly UnityEngine.Color SongRowLeftColor = new UnityEngine.Color(0.20f, 0.30f, 0.48f, 1f);
        private static readonly UnityEngine.Color SongRowRightColor = new UnityEngine.Color(0.11f, 0.15f, 0.24f, 1f);

        [UIComponent("pin-plate")]
        private HMUI.ImageView _pinPlate;

        [UIComponent("pin-icon")]
        private ClickableImage _pinIcon;

        [UIComponent("pin-dot")]
        private HMUI.ImageView _pinDot;

        [UIComponent("status-plate")]
        private HMUI.ImageView _statusPlate;

        // 上の段の、左の 3 つとピンの間の地だけのマス
        [UIComponent("top-gap-plate")]
        private HMUI.ImageView _topGapPlate;

        // ピンは「移動中」を入として灯す（ほかの切り替えと同じ見せ方）
        private void ShowPin()
        {
            PaintTile(_pinPlate, _pinIcon, _pinDot, !_pinned, true);
        }

        // 下見から 1 曲だけ遊んで曲選択に戻った: 下見の小窓を開き直す。結果画面でおすすめを選んだ（次の曲へ進む）ときは開かない
        private void ResumeDraft()
        {
            _draftResumePending = false;
            // 開き直すのは 1 回だけ（開かなかったときも）。同じ曲をまた遊んでも、もう開かない
            var from = _draftResumeHash;
            _draftResumeHash = null;
            if (_draft == null || _busy || _advancePending || _pendingPick != null || _placing != null)
            {
                return;
            }
            Plugin.Log.Info($"[mix] back from {from}; reopening the draft");
            _draftSelected = null;
            _draftMessage = "";
            ShowDraftModal();
            FillDraftList();
            NotifyDraft();
        }

        // 曲選択が続けて出たら、次の曲へ進む
        private void TickAdvance()
        {
            var inSongList = _levelSelection != null && _levelSelection.isActivated && _levelSelection.isInViewControllerHierarchy;
            if (!inSongList)
            {
                _songListSince = -1f;
                return;
            }
            if (_songListSince < 0f)
            {
                _songListSince = UnityEngine.Time.unscaledTime;
            }
            if (_draftResumePending && UnityEngine.Time.unscaledTime - _songListSince >= AdvanceDelay && !_gameScenesManager.isInTransition)
            {
                ResumeDraft();
            }
            if (!_advancePending || _placing != null || UnityEngine.Time.unscaledTime - _songListSince < AdvanceDelay || _gameScenesManager.isInTransition)
            {
                return;
            }
            _advancePending = false;
            _ = AdvanceAsync();
        }

        // 結果画面のおすすめ: 遊んだ譜面を起点に数曲だけ受け取る（取得はしない）。種類は今のミックスの種類（起点を使うもの）か、一覧で最初の起点を使うもの
        private const int ResultsCount = 3;

        // いまの結果画面のおすすめの起点と、そこで出した曲（題を押して入れ替えるときに除く）。次に遊び始めると null
        private ChartRef _resultsPlayed;
        private readonly HashSet<string> _resultsSeen = new HashSet<string>();
        private bool _resultsLoading;
        // 結果画面の読み込みの番号。古い読み込みの終わりが、新しい読み込みの印を消さないように
        private int _resultsLoad;

        // 結果画面の板の題を押した: 同じ起点・種類で、出した曲を除いて別の曲を受け取り、並びを入れ替える。
        // 出さなかった曲は skipped にしない（押さなかっただけで、外すと決めたのではない。OctantVector 法 4.2）
        private async System.Threading.Tasks.Task ShowOtherResultsAsync()
        {
            var played = _resultsPlayed;
            if (played == null || _resultsLoading || _placing != null || _pendingPick != null)
            {
                return;
            }
            _preview.Stop();
            _resultsPanel.SetDetail(Lang.T("next.againLoading"));
            await LoadResultsAsync(played, true);
        }

        // 結果画面の行を押して試聴した。曲への興味の印として残す（推薦にどう使うかは OctantVector が決める。2026-11-04）
        private void PreviewedFromResults(RecommendationItem item)
        {
            _signals.RecordRecommendation(item.RequestId, item.Preset, item.Candidate.Reason?.Hand, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "previewed");
        }

        // again: 題を押して入れ替える。出せなければ、今の並びは残して、そう書くだけ
        private async System.Threading.Tasks.Task LoadResultsAsync(ChartRef played, bool again = false)
        {
            if (!again)
            {
                _resultsPlayed = played;
                _resultsSeen.Clear();
            }
            _resultsLoading = true;
            var load = ++_resultsLoad;
            try
            {
                var presets = ShownPresets();
                var preset = _mixPreset != null && PresetRules.UsesSeed(_mixPreset) && PresetRules.Choices(_mixPreset).Count == 0 ? _mixPreset
                    : presets.FirstOrDefault(p => PresetRules.UsesSeed(p) && PresetRules.Choices(p).Count == 0);
                if (preset == null)
                {
                    var status = _connection.Status;
                    var why = status.State != Core.Connection.ConnectionState.Ready ? Core.Connection.StatusText.Describe(status) : Lang.T("recommend.none");
                    if (again)
                    {
                        // 入れ替えられないときは、今の並びを残して理由だけ書く
                        _resultsPanel.SetDetail(why);
                        return;
                    }
                    _resultsPanel.ShowUnavailable(Lang.T("next.titlePlain"), why);
                    return;
                }
                var request = PresetRules.Request(preset, played, null, ResultsCount, _settingsHolder.Current.DifficultyCeiling);
                var exclude = new HashSet<string>(_resultsSeen) { played.Hash };
                if (_mix != null)
                {
                    exclude.UnionWith(_mix.All.Select(i => i.Hash).Where(h => h != null));
                }
                request.Exclude = exclude.Select(h => new ExcludeEntry { Hash = h, Reason = _resultsSeen.Contains(h) ? "shown" : "in_mix" }).ToList();
                var result = await _recommendations.RequestAsync(request, _lifetime.Token);
                if (_resultsPlayed != played)
                {
                    // 待っている間に次の曲を遊び始めた
                    return;
                }
                if (again && (_pendingPick != null || _placing != null))
                {
                    // 待っている間に ▶ で曲を選んだ。選んだ曲の印と進み具合を、入れ替えで消さない
                    return;
                }
                var items = result.Error == null ? RecommendationDisplay.Shown(result.Items) : new List<RecommendationItem>();
                if (items.Count == 0 && again)
                {
                    Plugin.Log.Info($"[results] no other recommendations: {result.Error ?? "empty"}");
                    _resultsPanel.SetDetail(result.Error ?? Lang.T("next.againNone"));
                    return;
                }
                if (items.Count == 0)
                {
                    Plugin.Log.Info($"[results] no recommendations: {result.Error ?? (result.Items.Count == 0 ? "empty" : $"BeatSaver does not know all {result.Items.Count}")}");
                    // 出せなくても板（枠）は残し、理由を出す（2026-10-25）
                    _resultsPanel.ShowUnavailable(Lang.T("next.title", new { preset = preset.Label }), result.Error ?? Lang.T("recommend.none"));
                    return;
                }
                foreach (var item in items)
                {
                    _resultsSeen.Add(item.Hash);
                    _signals.RecordRecommendation(item.RequestId, item.Preset, item.Candidate.Reason?.Hand, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "shown");
                }
                // 選ばなければ進む曲。ミックスのプレイリストで遊んだときだけ進む
                var next = _playedInMix != null && _settingsHolder.Current.MixAutoAdvance ? _mix?.Next() : null;
                _resultsPanel.Show(Lang.T("next.title", new { preset = preset.Label }), items, next != null ? Lang.T("next.hintWithNext", new { next = next.Title }) : Lang.T("next.hint"));
                Plugin.Log.Info($"[results] {preset.Id} requestId={result.Response?.RequestId} seed={played.Hash} candidates={items.Count} leftOut={result.Items.Count - items.Count} again={again}");
                // 押したらすぐ聞けるように、並んだ曲の試聴を先読みする（次の曲を遊び始めて板のおすすめが替わったらやめる）
                _preview.Prefetch(items.Select(i => i.Version?.PreviewUrl).Where(u => u != null).ToList(), () => _resultsPanel.Holds(items));
                // カバーを待つ間も、題を押せば入れ替えられるように
                _resultsLoading = false;
                await LoadCoversAsync(items, () => true, _resultsPanel.Refresh);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[results] {ex}");
                if (again && _resultsPlayed == played)
                {
                    // 「探しています…」のままにしない
                    _resultsPanel.SetDetail(Lang.T("recommend.failed", new { detail = ex.Message }));
                }
            }
            finally
            {
                // カバーを待っている間に、題を押して次の読み込みが始まっていたら、その印は消さない
                if (load == _resultsLoad)
                {
                    _resultsLoading = false;
                }
            }
        }

        // 結果画面でおすすめの行の ▶ を押した: 並びの今の曲のすぐ後ろに入れて、次の曲にする（行を押しただけなら試聴だけ。ResultsPanel）。
        // 入っていれば、そのまま Continue を押す。入っていなければ置くところまで取り（進み具合は板の帯に出す）、置けたら Continue を押す。
        // 読み直しと曲選択での選択は、曲選択に戻ってから行う（TickAdvance）。取っている間に選び直しても受け付けない
        private void ChooseFromResults(RecommendationItem item)
        {
            if (_placing != null)
            {
                return;
            }
            _signals.RecordRecommendation(item.RequestId, item.Preset, item.Candidate.Reason?.Hand, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "selected");
            // ミックスのプレイリストで遊んだ曲のときだけ、ミックスに入れる。ほかの一覧で遊んだときは、その一覧で選ぶだけ（AdvanceAsync）
            if (_mix != null && _playedInMix != null)
            {
                _mix.InsertNext(item);
                _items = _mix.Visible();
                _mixPlaylistStale = true;
            }
            _pendingPick = item;
            _advancePending = true;
            _resultsPanel.MarkChosen(item);
            var download = !item.Installed && _downloads.IsAvailable(item);
            _resultsPanel.SetDetail(Lang.T(download ? "next.setDownloading" : "next.set", new { title = item.Title }));
            if (download)
            {
                _ = PlaceAsync(item);
            }
            else
            {
                _resultsPanel.PressContinue();
            }
        }

        // 結果画面で選んだ曲を取っているところ。取り終わるまで、曲選択に戻っても次へ進まない
        private RecommendationItem _placing;

        private async System.Threading.Tasks.Task PlaceAsync(RecommendationItem item)
        {
            _placing = item;
            _resultsPanel.SetProgress(0);
            string error = null;
            // 受け取った割合が何回届いたか（帯が伸びないと言われたときに、届いていないのかを見分ける）
            var reports = 0;
            var started = UnityEngine.Time.unscaledTime;
            try
            {
                var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Mix, false, _lifetime.Token, received: (_, fraction) =>
                {
                    System.Threading.Interlocked.Increment(ref reports);
                    _resultsPanel.SetProgress(fraction);
                });
                var outcome = outcomes?.FirstOrDefault();
                error = outcome == null ? Lang.T("download.notWhilePlaying") : outcome.Path == null && !outcome.AlreadyThere ? outcome.Error ?? "" : null;
                FillList();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                _placing = null;
                _resultsPanel.SetProgress(null);
            }
            Plugin.Log.Info($"[mix] placing {item.Hash} took {UnityEngine.Time.unscaledTime - started:0.0} s with {reports} progress reports");
            if (error != null)
            {
                _resultsPanel.HideProgress();
            }
            // 取っている間に遊び直した（Restart）なら、選んだ曲は捨てられている。新しい結果画面の Continue を押さない
            if (_pendingPick != item)
            {
                Plugin.Log.Info($"[mix] {item.Hash} was placed but is no longer the pick");
                return;
            }
            if (error != null)
            {
                Plugin.Log.Warn($"[mix] could not place {item.Hash}: {error}");
                _resultsPanel.SetDetail(Lang.T("download.failed", new { error }));
                return;
            }
            _resultsPanel.PressContinue();
        }

        private async System.Threading.Tasks.Task AdvanceAsync()
        {
            var pick = _pendingPick;
            _pendingPick = null;
            if (_mix == null && pick == null)
            {
                return;
            }
            // 曲選択に戻ったところなので、前もって置いた曲をここでまとめて読み直す
            if (_downloads.HasPlaced)
            {
                await _downloads.RefreshAsync(_lifetime.Token);
                foreach (var item in (_mix?.All ?? Enumerable.Empty<RecommendationItem>()).Append(pick).Where(i => i != null))
                {
                    item.Installed = LevelLookup.IsInstalled(item.Hash);
                }
            }
            if (pick != null && (_mix == null || !_mix.All.Contains(pick)))
            {
                Plugin.Log.Info($"[mix] picked {pick.Hash} outside the mix");
                await PlayItemAsync(pick, false);
                return;
            }
            var next = pick ?? _mix.Next();
            if (next != null)
            {
                Plugin.Log.Info($"[mix] next {next.Hash}");
                await PlayItemAsync(next);
            }
            else
            {
                FillList();
            }
        }

        [UIComponent("save-plate")]
        private HMUI.ImageView _savePlate;

        [UIComponent("save-icon")]
        private ClickableImage _saveIcon;

        // 保存のマスの絵の色: 並びがあれば白、なければ暗い灰（押しても何もしない）。押すとすぐ実行なので、灯りも点もない。
        // 同じ曲の組み合わせがもう保存してあれば緑（ランプの「残した」と同じ色）にして、押しても保存しない（2026-11-10。押すたびにプレイリストが増えた）
        private void ShowSave()
        {
            var saved = MixSaved();
            PaintTile(_savePlate, _saveIcon, null, false, _mix != null && !_busy && !saved, TileText);
            if (saved && _saveIcon != null)
            {
                _saveIcon.DefaultColor = SavedColor;
                _saveIcon.HighlightColor = SavedColor;
                _saveIcon.color = SavedColor;
            }
            NotifyPropertyChanged(nameof(SaveHint));
        }

        private static readonly UnityEngine.Color SavedColor = new UnityEngine.Color(0.5f, 1f, 0.6f, 1f);

        [UIValue("save-hint")]
        public string SaveHint => Lang.T(MixSaved() ? "panel.savedHint" : "panel.saveHint");

        // 保存済みかどうかは、並びの曲の組み合わせが変わったときだけプレイリストを読んで確かめる
        private string _savedCheckKey;
        private bool _savedCheck;

        private bool MixSaved()
        {
            if (_mix == null)
            {
                return false;
            }
            var hashes = MixHashes();
            var key = string.Join(",", hashes.OrderBy(h => h, StringComparer.Ordinal));
            if (key != _savedCheckKey)
            {
                _savedCheckKey = key;
                try
                {
                    _savedCheck = _playlist.HasSavedMix(hashes);
                }
                catch (Exception e)
                {
                    Plugin.Log.Warn($"[playlist] could not check saved mixes: {e.Message}");
                    _savedCheck = false;
                }
            }
            return _savedCheck;
        }

        private List<string> MixHashes() => _mix.All.Where(i => i.Hash != null).Select(i => i.Hash.ToLowerInvariant()).ToList();

        // 並びをプレイリストに保存する。保存したプレイリストは OctantPath のお試しの表示ではないので、
        // その曲は自動削除から守られる（OctantPath 法 3.4 のプレイリストの扱い）
        [UIAction("save-click")]
        private void SaveClick()
        {
            if (_mix == null || _busy || MixSaved())
            {
                return;
            }
            _playlist.SaveMix(MixName(), _mix.All.Select(i => (i.Hash, i.Title, i.Map?.Key, i.Mapper)).ToList());
            _savedCheckKey = null;
            // 保存はほかの画面では見えないので、下の段に短く知らせる（板には残さない）
            _message = "";
            Flash(Lang.T("save.done"));
            Notify();
        }

        private void FillList()
        {
            if (_list == null)
            {
                return;
            }
            _list.Data = _items.Select(i =>
            {
                var playing = i == _selected ? "▶ " : "";
                var state = i.Installed ? "" : _downloads.IsInFlight(i.Hash) ? "  " + Lang.T("item.downloading") : _downloads.IsPlaced(i.Hash) ? "  " + Lang.T("item.waitingToLoad") : _downloads.IsAvailable(i) ? "  " + Lang.T("item.notFetched") : "  " + Lang.T("item.cannotDownload");
                _covers.TryGet(i.Version?.CoverUrl, out var cover);
                return new CustomListTableData.CustomCellInfo($"{playing}{Core.UI.DisplayText.Line(i.Title)}", $"{Core.UI.DisplayText.Line(i.Artist)} [{Core.UI.DisplayText.Line(i.Mapper)}]  {i.Candidate.Difficulty}{state}", cover);
            }).ToList();
            _list.TableView.ReloadData();
            var index = _selected != null ? _items.IndexOf(_selected) : -1;
            if (index >= 0)
            {
                _list.TableView.SelectCellWithIdx(index);
                _list.TableView.ScrollToCellWithIdx(index, TableView.ScrollPositionType.Center, false);
            }
            else
            {
                _list.TableView.ClearSelection();
            }
        }

        // 曲選択で選べたら下の段に短く知らせ、選べなければ失敗の知らせを返す
        private string SelectedOrFailed(bool selected)
        {
            if (selected)
            {
                Flash(Lang.T("status.selected"));
                return "";
            }
            return Lang.T("select.failed");
        }

        private void SetBusy(bool busy, string message)
        {
            var changed = _busy != busy;
            _busy = busy;
            // 下見の行の ⇔ と ▶ は、取得や作成の途中は出さない。行は選んでもカバーが届いても作り直さないので、ここで作り直す
            if (changed && _draft != null)
            {
                FillDraftList();
            }
            if (message != null)
            {
                _message = message;
            }
            Notify();
        }

        private void Notify()
        {
            ShowSave();
            NotifyStatus();
            NotifyPropertyChanged(nameof(MixInteractable));
            NotifyPropertyChanged(nameof(PlayInteractable));
            NotifyPropertyChanged(nameof(DetailText));
            NotifyPropertyChanged(nameof(FilterText));
            NotifyDraft();
        }
    }
}
