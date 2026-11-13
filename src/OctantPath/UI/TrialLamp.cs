using System;
using System.Globalization;
using System.Linq;
using BeatSaberMarkupLanguage.Attributes;
using OctantPath.Core.Languages;
using OctantPath.Core.Maps;
using OctantPath.Core.Signals;
using OctantPath.Core.Trials;
using OctantPath.Installers;
using OctantPath.Maps;
using Zenject;

namespace OctantPath.UI
{
    // お試しのランプ。曲選択の上に浮かぶ板の曲の行の右端（RecommendationTab が置き場所を渡す）と、結果画面の曲名の行の右端に置く。
    // 曲の詳細に重ねていたが、ジャケットを大きくする MOD や数値を増やす MOD のあるなしで隠れたり重なったりしたので、
    // OctantPath だけが描く浮かぶ板へ移した（2026-11-08）。結果画面では、ゲームの UI が右を「進む」にしているので右に置く
    // （BeatSaver Voting の板は結果画面の外の右に浮かぶので、行の右端なら重ならない。2026-11-07）。
    // ランプは MOD が読み込まれていることが分かるよう、お試しでない曲でも消灯（○）で出す。お試し中は橙、残すときは緑に灯る。
    // お試し中に押すと「残す」になり、次にゲームを起動したとき CustomLevels に移る（ゲーム中は SongCore が読んでいるので、その場では移さない）。
    // 「残す」は好意の印ではないので、signal にも好みにも入れない（OctantSpecs open.md の U7）
    internal sealed class TrialLamp : IInitializable, IDisposable
    {
        // 結果画面の曲名の行（LevelBar）の右の外側
        private const float ResultsGap = 1f;


        // 押せる範囲は、曲の行の右端の幅 8 × 行の高さいっぱい（絵は小さいまま、その真ん中に出す）。枠や地は描かない
        private const float PanelLampWidth = 8f;
        private static readonly UnityEngine.Vector2 PanelLampSize = new UnityEngine.Vector2(PanelLampWidth, 0f);

        private readonly OwnershipLedger _ledger;
        private readonly Signals.SignalService _signals;
        private readonly SettingsHolder _settings;
        private readonly OctantPathFolders _folders;
        private readonly DownloadService _downloads;
        private readonly GameScenesManager _gameScenesManager;
        private readonly PlayerDataModel _playerDataModel;
        // 浮かぶ板のランプ（選んでいる曲）
        private readonly LampButton _detail;
        private readonly LampButton _results;
        private StandardLevelDetailViewController _levelDetail;
        // BeatSaverVoting の投票の記録。投票は signal の正本に入るのがメニューに戻ったときなので、ランプはこのファイルを直接読み、
        // 書き換わったら（結果画面や曲の詳細で投票した）すぐ描き直す（Upvote がすぐに出なかった。2026-11-07）。
        // 書き換わりは OS の知らせ（FileSystemWatcher）で受け、ゲームの主スレッドに戻してから描く
        private readonly string _votedSongs = System.IO.Path.Combine(IPA.Utilities.UnityGame.UserDataPath, "votedSongs.json");
        private System.IO.FileSystemWatcher _votesWatcher;

        public TrialLamp(OwnershipLedger ledger, Signals.SignalService signals, SettingsHolder settings, OctantPathFolders folders, DownloadService downloads, GameScenesManager gameScenesManager, PlayerDataModel playerDataModel, [InjectOptional] StandardLevelDetailViewController levelDetail)
        {
            _ledger = ledger;
            _signals = signals;
            _settings = settings;
            _folders = folders;
            _downloads = downloads;
            _gameScenesManager = gameScenesManager;
            _playerDataModel = playerDataModel;
            _levelDetail = levelDetail;
            _detail = new LampButton("panel", Toggle);
            _results = new LampButton("results", Toggle);
        }

        public void Initialize()
        {
            _downloads.Changed += Refresh;
            _gameScenesManager.transitionDidFinishEvent += HandleTransitionDidFinish;
            _playerDataModel.playerData.favoriteLevelsSetDidChangeEvent += Refresh;
            WatchVotes();
            if (_levelDetail == null)
            {
                _levelDetail = UnityEngine.Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>().FirstOrDefault();
            }
            if (_levelDetail == null)
            {
                Plugin.Log.Warn("[lamp] StandardLevelDetailViewController was not found; the selected song is not known, so the panel lamp stays unlit");
                return;
            }
            _levelDetail.didChangeDifficultyBeatmapEvent += HandleSelectionChanged;
            _levelDetail.didChangeContentEvent += HandleContentChanged;
        }

        public void Dispose()
        {
            _downloads.Changed -= Refresh;
            _gameScenesManager.transitionDidFinishEvent -= HandleTransitionDidFinish;
            _playerDataModel.playerData.favoriteLevelsSetDidChangeEvent -= Refresh;
            if (_votesWatcher != null)
            {
                _votesWatcher.EnableRaisingEvents = false;
                _votesWatcher.Dispose();
                _votesWatcher = null;
            }
            if (_levelDetail != null)
            {
                _levelDetail.didChangeDifficultyBeatmapEvent -= HandleSelectionChanged;
                _levelDetail.didChangeContentEvent -= HandleContentChanged;
            }
        }

        // 投票の記録の書き換わりを待つ。待てなくても、曲を選び直したときやメニューに戻ったときには読み直す
        private void WatchVotes()
        {
            try
            {
                _votesWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(_votedSongs), System.IO.Path.GetFileName(_votedSongs))
                {
                    NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
                };
                _votesWatcher.Changed += HandleVotesChanged;
                _votesWatcher.Created += HandleVotesChanged;
                _votesWatcher.Renamed += HandleVotesChanged;
                _votesWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[lamp] cannot watch votedSongs.json; votes show after the song is selected again: {ex.Message}");
                _votesWatcher?.Dispose();
                _votesWatcher = null;
            }
        }

        // 知らせは別のスレッドで来る。Unity の部品は主スレッドでしか触れないので、主スレッドに渡す
        private void HandleVotesChanged(object sender, System.IO.FileSystemEventArgs e) =>
            IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(Refresh);

        private void HandleSelectionChanged(StandardLevelDetailViewController controller)
        {
            _detail.Hash = SelectedHash();
            Refresh();
        }

        private void HandleContentChanged(StandardLevelDetailViewController controller, StandardLevelDetailViewController.ContentType type) => HandleSelectionChanged(controller);

        // 遊び終えて戻ったら、結果画面のランプにその曲を出す（遊んだ回数で残るようになったかもしれない）
        private void HandleTransitionDidFinish(GameScenesManager.SceneTransitionType type, ScenesTransitionSetupDataSO setupData, DiContainer container)
        {
            if (setupData is StandardLevelScenesTransitionSetupDataSO standard)
            {
                _results.Hash = LevelIds.Hash(standard.beatmapKey.levelId);
                EnsureResultsButton();
            }
            Refresh();
        }

        private void Refresh()
        {
            foreach (var lamp in new[] { _detail, _results })
            {
                try
                {
                    lamp.Draw(lamp.Hash == null ? TrialLampState.Hidden : Read(lamp.Hash));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warn($"[lamp] could not read the {lamp.Name} song: {ex.Message}");
                    lamp.Draw(TrialLampState.Hidden);
                }
            }
        }

        private string SelectedHash()
        {
            var level = _levelDetail != null ? _levelDetail.beatmapLevel : null;
            return level == null ? null : MapHash.Normalize(SongCore.Collections.GetCustomLevelHash(level.levelID));
        }

        // 片付け（Janitor）と同じ決まりで、次の起動で残るかを読む。ただし合計の上限で消えるかは、ここでは見ない
        private TrialLampState Read(string hash)
        {
            var map = _ledger.Load()?.Maps.FirstOrDefault(m => m.Hash == hash && m.State == MapStates.Trial);
            if (map == null)
            {
                return TrialLampState.Hidden;
            }
            var settings = _settings.Current;
            var state = ImportState.Load(_folders.Root, out _);
            if (state == null)
            {
                // 起動日が分からなければ、残りの日数を出せない。理由をログに残して消す（例外のログに埋もれさせない）
                Plugin.Log.Warn("[lamp] the import state cannot be read; the launch days are unknown, so the lamp is not shown");
                return TrialLampState.Hidden;
            }
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var days = state.LaunchDays.Where(d => string.CompareOrdinal(d, today) <= 0);
            var plays = _signals.Events.Where(e => e.Type == SignalTypes.Play).ToList();
            var votes = _signals.LastVotes();
            try
            {
                // 片付けと同じく、votedSongs.json を signal の正本より優先する
                foreach (var pair in VotedSongs.Read(_votedSongs))
                {
                    votes[pair.Key] = pair.Value;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"[lamp] votedSongs.json could not be read: {ex.Message}");
            }
            var policy = new TrialPolicy(_signals.Liked(), votes, plays, days, settings.KeepMinPlays, settings.TrialExpiryLaunchDays);
            return TrialLampState.For(map, policy, settings.TrialExpiryLaunchDays);
        }

        private void Toggle(LampButton lamp)
        {
            if (lamp.Hash == null || !lamp.State.CanToggle)
            {
                return;
            }
            var keep = lamp.State.KeepAfterToggle;
            if (!_ledger.SetKeepRequested(lamp.Hash, keep))
            {
                Plugin.Log.Warn($"[lamp] could not record keep={keep} for {lamp.Hash} (not a trial, or the ledger is unreadable)");
            }
            else
            {
                Plugin.Log.Info($"[lamp] {lamp.Hash}: keep={keep} (from the {lamp.Name})");
            }
            Refresh();
        }

        // 浮かぶ板の曲の行に、選んでいる曲のランプを作る（RecommendationTab が板を作るとき）。parent の右端に、行の高さいっぱいの押せる範囲として置く
        public void CreatePanelLamp(UnityEngine.GameObject parent)
        {
            if (!_detail.ShouldTry())
            {
                return;
            }
            var rect = _detail.Create(parent, PanelLampSize, PanelLampWidth);
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = new UnityEngine.Vector2(1f, 0f);
            rect.anchorMax = new UnityEngine.Vector2(1f, 1f);
            rect.pivot = new UnityEngine.Vector2(1f, 0.5f);
            rect.anchoredPosition = UnityEngine.Vector2.zero;
            _detail.Hash = SelectedHash();
            Refresh();
            Plugin.Log.Debug("[lamp] added to the floating panel");
        }

        // 結果画面のボタンは、初めて遊び終えて戻ったときに作る
        private void EnsureResultsButton()
        {
            if (!_results.ShouldTry())
            {
                return;
            }
            var results = UnityEngine.Resources.FindObjectsOfTypeAll<ResultsViewController>().FirstOrDefault();
            var bar = results != null ? results.GetComponentsInChildren<LevelBar>(true).FirstOrDefault() : null;
            if (bar == null)
            {
                Plugin.Log.Warn($"[lamp] the song row of the results was not found (results={results != null})");
                return;
            }
            var rect = _results.Create(bar.gameObject);
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = new UnityEngine.Vector2(1f, 0.5f);
            rect.anchorMax = new UnityEngine.Vector2(1f, 0.5f);
            rect.pivot = new UnityEngine.Vector2(0f, 0.5f);
            rect.anchoredPosition = new UnityEngine.Vector2(ResultsGap, 0f);
            Plugin.Log.Debug($"[lamp] added to the right of {bar.name} ({((UnityEngine.RectTransform)bar.transform).rect.size})");
        }
    }

    // ランプ 1 つ。押せる絵（clickable-image）。お試しは輪の付いたカレンダー（橙）に残りの起動日の数、自分で残したものは丸にチェック（緑）。
    // 色だけでなく形でも分ける（2026-11-09）。ボタンにしないのは、結果画面で色がゲームの白に戻され、枠と下線も意味がなく見えたため（2026-11-07）。
    // 押されたら持ち主に知らせる
    internal sealed class LampButton
    {
        private static readonly UnityEngine.Vector2 Size = new UnityEngine.Vector2(7f, 7f);
        private static readonly UnityEngine.Color TrialColor = new UnityEngine.Color(1f, 0.72f, 0.25f, 1f);
        private static readonly UnityEngine.Color KeepColor = new UnityEngine.Color(0.5f, 1f, 0.6f, 1f);

        // 絵の PNG は、四角の真ん中 0.76 に描いてある（Icons/draw.py の LampScale）。リングを除いた枠の内側（512 四方で y 124〜436）の真ん中は、絵の真ん中より絵の高さの 0.047 下。
        // 数字は、数字の高さ（cap height）の真ん中をそこに合わせる（文字を枠に入れるときのふつうの合わせ方）。下へ寄せると低く見えた。
        // 数字は思い切って大きく、中いっぱいに出す（2026-11-10）
        private const float IconShare = 0.76f;
        private const float DaysDrop = 0.047f;
        private const float DaysFontShare = 0.72f;

        private UnityEngine.Sprite _trialSprite;
        private UnityEngine.Sprite _keptSprite;

        private readonly Action<LampButton> _click;
        private bool _tried;

        [UIComponent("lamp")]
        private BeatSaberMarkupLanguage.Components.ClickableImage _button;

        // カレンダーの中の、残りの起動日の数
        [UIComponent("lamp-days")]
        private TMPro.TextMeshProUGUI _days;

        public LampButton(string name, Action<LampButton> click)
        {
            Name = name;
            _click = click;
        }

        public string Name { get; }

        // 出している曲（null ならお試しの曲ではない）
        public string Hash { get; set; }

        public TrialLampState State { get; private set; } = TrialLampState.Hidden;

        // まだ作っておらず、作ろうとしたこともない
        public bool ShouldTry()
        {
            if (_button != null || _tried)
            {
                return false;
            }
            _tried = true;
            return true;
        }

        // parent に重ねて作り、置き場所を決めるための RectTransform を返す（作れなければ null）
        public UnityEngine.RectTransform Create(UnityEngine.GameObject parent) => Create(parent, Size, Size.x);

        // size は押せる範囲。side は絵の四角の一辺（押せる範囲の短い辺）
        public UnityEngine.RectTransform Create(UnityEngine.GameObject parent, UnityEngine.Vector2 size, float side)
        {
            BeatSaberMarkupLanguage.BSMLParser.Instance.Parse("<clickable-image id='lamp' src='OctantPath.Icons.trial.png' preserve-aspect='true' on-click='click' hover-hint='-'/>", parent, this);
            if (_button == null)
            {
                return null;
            }
            _trialSprite = Icons.Find(Icons.Trial);
            _keptSprite = Icons.Find(Icons.Kept);
            var layout = _button.GetComponent<UnityEngine.UI.LayoutElement>() ?? _button.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            layout.ignoreLayout = true;
            var rect = (UnityEngine.RectTransform)_button.transform;
            rect.sizeDelta = size;
            // 数は絵の子にして、絵と一緒に出し入れする。押すのは絵（数は押せる範囲をふさがない）
            BeatSaberMarkupLanguage.BSMLParser.Instance.Parse("<text id='lamp-days' text='' align='Center' word-wrapping='false' bold='true'/>", _button.gameObject, this);
            if (_days != null)
            {
                var icon = side * IconShare;
                var daysLayout = _days.GetComponent<UnityEngine.UI.LayoutElement>() ?? _days.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                daysLayout.ignoreLayout = true;
                var daysRect = _days.rectTransform;
                daysRect.anchorMin = daysRect.anchorMax = daysRect.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                daysRect.sizeDelta = new UnityEngine.Vector2(icon, icon * 0.7f);
                daysRect.anchoredPosition = new UnityEngine.Vector2(0f, -icon * DaysDrop);
                _days.fontSize = icon * DaysFontShare;
                _days.raycastTarget = false;
                // 行の高さ（上下の余白込み）ではなく、数字の高さで上下の真ん中を合わせる。行で合わせると数字が上に寄った（2026-11-10）
                _days.alignment = TMPro.TextAlignmentOptions.Capline;
            }
            return rect;
        }

        [UIAction("click")]
        private void Click()
        {
            _click(this);
            ShowHintAgain();
        }

        // 押した直後もポインタはランプの上にあるので、出ている説明を新しい文で出し直す（出し直さないと、外して当て直すまで古い文のまま）
        private void ShowHintAgain()
        {
            try
            {
                var hint = _button != null ? _button.GetComponent<HMUI.HoverHint>() : null;
                var controller = UnityEngine.Resources.FindObjectsOfTypeAll<HMUI.HoverHintController>().FirstOrDefault();
                if (hint != null && controller != null)
                {
                    controller.ShowHint(hint);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[lamp] could not refresh the hint: {ex.Message}");
            }
        }

        public void Draw(TrialLampState state)
        {
            State = state;
            if (_button == null)
            {
                return;
            }
            // 押して変えられないときは出さない（押せる範囲ごと消す）
            _button.gameObject.SetActive(state.Kind != TrialLampKind.None);
            var trial = state.Kind == TrialLampKind.Trial;
            var sprite = trial ? _trialSprite : _keptSprite;
            if (sprite != null)
            {
                _button.sprite = sprite;
            }
            // 指を離したあとに戻る色（DefaultColor）も変える。color だけだと、ポインタが外れたときに白に戻る
            _button.DefaultColor = trial ? TrialColor : KeepColor;
            _button.HighlightColor = UnityEngine.Color.Lerp(_button.DefaultColor, UnityEngine.Color.white, 0.4f);
            _button.color = _button.DefaultColor;
            if (_days != null)
            {
                // 消えるまでに残っている起動日（今日を含む）。正確な日数は手を当てたときの説明に出す
                _days.text = trial ? Math.Max(1, state.ExpiryLaunchDays - state.LaunchDays + 1).ToString() : "";
                _days.color = TrialColor;
            }
            var hint = _button.GetComponent<HMUI.HoverHint>();
            if (hint != null)
            {
                hint.text = Hint(state);
            }
        }

        private static string Hint(TrialLampState state) =>
            state.Kind == TrialLampKind.Trial
                ? Lang.T("lamp.trialHint", new { days = state.LaunchDays, expiry = state.ExpiryLaunchDays })
                : Lang.T("lamp.keepUserHint");
    }
}
