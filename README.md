# OctantPath

> **To use BeatOctant** (install, known issues): [Getting Started](https://github.com/BeatOctant/OctantSpecs/blob/main/GettingStarted_en.md) · [はじめかた（日本語）](https://github.com/BeatOctant/OctantSpecs/blob/main/GettingStarted_ja.md)
>
> BeatOctant is written mostly with AI coding agents (Anthropic's Claude). The maintainer decides what to build and reviews every change. It does not generate maps; it recommends existing maps from BeatSaver. ([作り方（AI の使い方）](https://github.com/BeatOctant/OctantSpecs/blob/main/README.md#作り方ai-の使い方))
>
> OctantPath is a Beat Saber mod for PC (Steam) that recommends BeatSaver maps from your own play history. For Beat Saber on Quest, see OctantPath for Quest ([OctantPathAndroid](https://github.com/BeatOctant/OctantPathAndroid)).
>
> BeatOctant is an unofficial fan project for Beat Saber. It is not affiliated with, endorsed by, or sponsored by Beat Games or Meta. Beat Saber and Meta Quest are trademarks of their respective owners. Beat Games・Meta とは関係のない非公式の MOD・ツールです。
>
> 報告と案は Discord の BeatOctant サーバー（バグは `#bug-reports`、案は `#feedback-ideas`）で受けます。外からの pull request は受けていません。

Beat Saber（PC）の MOD と、ゲームを起動せずに推薦を見る OctantPath Desktop です。ゲームの外で動く [OctantCompass](https://github.com/BeatOctant/OctantCompass) に推薦を頼み、推薦されたマップを BeatSaver から取って入れ、自分が入れたマップだけを片付けます。BeatOctant 全体の中での位置は [OctantSpecs の概要](https://github.com/BeatOctant/OctantSpecs#概要)にあります。

## 入れ方

MOD の入れ方、OctantCompass とのペアリング、既知の問題は [はじめかた](https://github.com/BeatOctant/OctantSpecs/blob/main/GettingStarted_ja.md)（[English](https://github.com/BeatOctant/OctantSpecs/blob/main/GettingStarted_en.md)）にあります。

Beat Saber（PC）1.40.8 用です。BeatOctant の部品（OctantPath・OctantCompass・配布 DB など）は、どれも最新の版どうしで使ってください。古い版の組み合わせはサポートしていません。

OctantPath Desktop は [Releases](https://github.com/BeatOctant/OctantPath/releases) の実行ファイル（Linux・Windows）を使います。.NET は入っているので要りません。

- Linux: OS の WebKitGTK 4.1 と libnotify が要ります（Debian・Ubuntu は `libwebkit2gtk-4.1-0` と `libnotify4`）
- Windows: Windows 10 と 11 に入っている WebView2 を使います

## OctantPath Desktop

ゲームを起動せずに、MOD と同じ手札で推薦を受け取り、下見して取得し、プレイリストにできます。

- 手札（「ミックス」の種類と、曲を起点にしないカード）を押して推薦を受け取ります。起点の曲は、左の所有マップから選ぶか、BeatSaver のキー・URL・hash で選びます
- 下見で曲を選ぶと、試聴、推薦の理由、譜面のプレビュー（ArcViewer）が出ます。⇔ で入れ替え、↓ で取得、「プレイリストを作る」で並んでいる曲を取ってプレイリストにします。「保存…」で、作ったプレイリストを好きなフォルダに写せます
- 「所有しているマップ」で、ゲームに入っている曲と Desktop で取った曲をまとめて検索・再生・プレビューできます。ゲームに入っている曲は下見で「導入済み」と出し、取り直しません
- 「設定」のタブで、ペアリング、ゲームのフォルダ、下見の曲数、マップの取得元、ペアリングの名前を変えられます。上の帯で言語とテーマを選べます

ペアリングは「設定」のタブで、つなぐ先（空なら同じ PC の `127.0.0.1:23287`）と、OctantCompass の窓に出る合言葉を入れます。同じ PC でも合言葉が要ります。Desktop は OctantCompass を起動しないので、先に OctantCompass を動かしておきます。

譜面のプレビューに使う ArcViewer（GPLv3）は、初回の起動で決まった版（約 80MB）をデータの場所の `arcviewer` に取ります。取れなかったときは譜面のプレビューだけが出ません。取り直すときは `arcviewer` を消して起動します。

Desktop は signal を送りません（遊んだ記録がないため）。ゲームのフォルダには書きません。

### 起動のオプション

| オプション | 中身 |
|---|---|
| `--no-window` | 窓を出さずにサーバーだけ動かし、ページは http://127.0.0.1:5791 をブラウザで開く。フォルダを選ぶ窓とプレイリストの保存先の選択は使えない |
| `--data <フォルダ>` | Desktop のデータを全部このフォルダに置く |
| `--game <フォルダ>` | ゲームのフォルダ（入っている曲を読むだけ）。省くと「設定」のタブで選んだもの |
| `--userdata <フォルダ>` | ゲームの `UserData/OctantPath` の設定とトークンを読んで、MOD と同じ環境として話す（そのフォルダには書かない） |

### Desktop のデータの場所

使っている場所は「設定」のタブに出ます。

| 中身 | Linux | Windows・macOS |
|---|---|---|
| 設定（`settings.json`・`desktop.json`） | `~/.config/OctantPath/Desktop` | `%LOCALAPPDATA%\OctantPath\Desktop`（macOS は `~/Library/Application Support/OctantPath/Desktop`）の 1 か所 |
| トークン（`connection.json`） | `~/.local/state/OctantPath/Desktop` | 同上 |
| 取得したマップ（`Levels`）・プレイリスト（`Playlists`） | `~/.local/share/OctantPath/Desktop` | 同上 |
| 所有マップの覚え書き（`library-cache.json`）・ArcViewer | `~/.cache/OctantPath/Desktop` | 同上 |

Linux はそれぞれ `$XDG_CONFIG_HOME`・`$XDG_STATE_HOME`・`$XDG_DATA_HOME`・`$XDG_CACHE_HOME` があればその下です。実行ファイルの隣に `OctantPath.Desktop-data` フォルダがあれば、全部をそこに置きます（持ち歩き用）。

## 設定

MOD の設定は `UserData/OctantPath/settings.json` です。ほとんどは MOD の設定画面（Mod Settings の OctantPath）で変えられ、変えた項目だけが書かれます。項目を消すと既定値に戻ります。既定値は [`src/OctantPath.Core/Settings/defaults.json`](src/OctantPath.Core/Settings/defaults.json) にあり、そこにある待ち時間や上限などの内部の値も、settings.json に書けば変えられます。

| 項目 | 中身 |
|---|---|
| `language` | 画面の言語（`auto`・`en`・`ja`）。下の「言語」 |
| `autoDownload` | 推薦された曲を自動で取るか |
| `deleteDryRun` | 切なら、お試しの曲を起動直後に自動で消す（設定画面の「お試しの曲を自動で消す」）。既定は切 |
| `keepMinPlays` | お試しの曲を「残す」にする遊んだ回数 |
| `trialExpiryLaunchDays`・`trialMaxMegabytes` | お試しの期限（起動した日の数）と、お試しの合計の上限（MB）。どちらかを超えたら起動直後に消す。他のプレイリストやお気に入りに入っている曲は消さない |
| `trialFrames`・`trialSlotCount` | お試しの枠（どの手札で何件取るか）。既定ではどの枠も 0 件 |
| `mixDraftCount`・`mixPrefetchCount` | ミックスの下見に並べる曲数と、先に取る曲数 |
| `mixAutoAdvance` | 遊び終えたら次の曲を自動で選ぶか（曲選択の上の板の「自動で次へ」） |
| `mixListOnPanel` | 板にミックスの一覧を出すか |
| `difficultyCeiling` | 難しさの上限を使うか（板で切り替える） |
| `recommendationPanelScale`・`panelOpacity` | 推薦の板の大きさと、背景の不透明度 |
| `zipSource` | マップの取得元（`BeatSaver` か手元のフォルダ） |
| `environmentName` | ペアリングで OctantCompass に伝える名前（空なら OctantPath が作る） |

settings.json が壊れて読めないときや、片付けの値がおかしいときは、お試しの曲を何も消しません。

OctantCompass とは通信だけでつなぎ、OctantCompass のファイルを読まず、起動もしません。つなぐ先とトークンは `UserData/OctantPath/connection.json` にあります。ペアリングは MOD の「OctantCompass との接続」の画面で、つなぐ先（同じ PC なら空のまま）と OctantCompass の画面に出る合言葉を入れて「ペアリング」を押します。Proton でも Linux の OctantCompass に 127.0.0.1 でつながります（推測: Wine は Linux のネットワークをそのまま使う）。

## MOD が書くファイル

どれも今使っているもので、消すと記録や設定がなくなります。

| 場所 | 中身 |
|---|---|
| `UserData/OctantPath/settings.json` | 設定（人が書く。MOD は設定画面で変えた項目だけを書く） |
| `UserData/OctantPath/connection.json` | つなぐ先とトークン |
| `UserData/OctantPath/signals.jsonl` | プレイ・いいねなどの記録（追記だけ） |
| `UserData/OctantPath/recommendations.jsonl` | 推薦 1 回ごとの要求の中身と、返った曲と理由（追記だけ。推薦のまとめに使い、外へは送らない） |
| `UserData/OctantPath/summaries/` | 設定画面の「推薦のまとめを書き出す」で書いたまとめ |
| `UserData/OctantPath/ledger.json` | OctantPath が入れたマップの所有の記録 |
| `UserData/OctantPath/placed.json` | 置いたが、SongCore の読み込みをまだ確かめていないマップ（なければ `[]`） |
| `UserData/OctantPath/state.json` | 導入前の記録を取り込んだ日と、起動した日 |
| `UserData/OctantPath/ui.json` | 動かした板の位置 |
| `UserData/OctantPath/Levels/` | 取得したマップ |
| `UserData/OctantPath/Work/` | 取得中の作業場所（終われば空） |
| `UserData/OctantPath/Deleting/` | 消している途中のマップ（消し終われば空） |
| `Playlists/OctantPath/` | お試しの枠ごとのプレイリスト、ミックスの一時的なプレイリスト、保存したミックス |

`UserData/OctantPath/lang/` は人が置くもので、MOD は書きません。

推薦は 1 回ごとに、ゲームの Logs の OctantPath のログに `[recommend]` で書きます（種類・起点・選択肢と、返った曲ごとの BeatSaver のキー・曲名・譜面・理由）。

## 言語

英語と日本語を同梱しています。MOD の設定画面の「言語」で選び、OK を押すと使います。`auto` なら、ゲームの言語（英語以外のとき）、OS の言語、英語の順に選びます。Desktop は上の帯で選び、`auto` は OS の言語です。

`UserData/OctantPath/lang/<言語>.json`（キー → 文）を置くと、同梱の文より優先します。同梱にない言語もファイルを置くだけで選べ、足りないキーは英語で出ます。同梱の文は `src/OctantPath.Core/Languages/` と `src/OctantPath.Desktop/Languages/` にあります。手札の名前は配布 DB から来るので、ここでは変わりません。

## 開発

仕様はすべて [OctantSpecs](https://github.com/BeatOctant/OctantSpecs) にあり、ここには書き写しません。入口は [OctantPath 法](https://github.com/BeatOctant/OctantSpecs/blob/main/octantpath/law.md)と[実装構想](https://github.com/BeatOctant/OctantSpecs/blob/main/plans/octantpath-implementation.md)です。

### ビルド

ゲームのフォルダをリポジトリ直下の `BeatSaber` というシンボリックリンクで指すか、`-p:BeatSaberDir=<ゲームのフォルダ>` で渡します。`-p:CopyToGame=true` を付けると、できた DLL をゲームの `Plugins` に置きます。

```bash
dotnet build src/OctantPath -c Release
dotnet run --project src/OctantPath.Desktop
```

リポジトリの中で Desktop を動かすと、データはリポジトリの `.desktop`（git に入れない）に置きます。

### テスト

Unity に依存しない部品（`src/OctantPath.Core`）を .NET 8 で試します。契約3 の例は OctantSpecs から `OCTANTSPECS_REF` の版で取ってきます。

```bash
scripts/fetch-octantspecs.sh
dotnet test tests/OctantPath.Core.Tests
```

動いている OctantCompass とつなぐテストは、`OCTANTPATH_TEST_PAIR_CODE` に合言葉、`OCTANTPATH_TEST_ADDRESS` につなぐ先を入れると動きます（つなぐ先を省くと失敗します）。ペアリングのたびに環境が 1 つ増えるので、テスト用のデータフォルダと別のポートで起動した OctantCompass につなぎます。

```bash
octantcompass serve --data-dir /tmp/octantcompass-test --port 23288 --db <普段の DB のフォルダ> &
OCTANTPATH_TEST_PAIR_CODE=$(octantcompass pair code --data-dir /tmp/octantcompass-test) \
OCTANTPATH_TEST_ADDRESS=127.0.0.1:23288 dotnet test tests/OctantPath.Core.Tests
```

終わったら止めて、`/tmp/octantcompass-test` を消します。

### 配布用の実行ファイル

Desktop を .NET ごと 1 つの実行ファイルにします。Linux の上で Windows 向けも作れます。MOD の DLL はゲームの DLL が要るので、ゲームのある PC で作ります。

```bash
scripts/publish-desktop.sh linux-x64   # dist/linux-x64/OctantPath.Desktop
scripts/publish-desktop.sh win-x64     # dist/win-x64/OctantPath.Desktop.exe
```

同梱したもののライセンスは `dist/<RID>/THIRD-PARTY-NOTICES.txt` に書かれます。手で書く分は [`src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt`](src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt) で、パッケージを足したらここにも足します（テストが名前と版を確かめます）。

### CI

pull request ごとに GitHub Actions が OctantPath.Core のテストと Desktop のビルドを動かします（`.github/workflows/test.yml`）。MOD 本体はゲームの DLL が要るので CI ではビルドしません。文書と `probes/` だけの変更では動きません。

`v*` のタグを push すると、`.github/workflows/release.yml` が Linux と Windows の Desktop を作り、Linux の分が起動して画面を返すことを確かめ、下書きの GitHub Release に置きます。手動の実行では Windows の分の起動も確かめ、成果物を 1 日だけ置きます。

## ライセンス

[MIT](LICENSE)。OctantPath Desktop が初回に取ってくる ArcViewer（GPLv3）は同梱していません。
