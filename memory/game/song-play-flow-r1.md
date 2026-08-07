# 曲の読み込み・再生フローとゲーム外周の設計 (r1)

対象: 「自作した譜面を実機でテストできる状態」にするために必要な、
譜面/音源の読み込み・音源同期・画面遷移（タイトル→ロード→プレイ→終了）・最小限の設定。

前提: [[muses-unity-port-progress]] の Phase 0〜4 完了・Phase 5（ゲーム側演出）完了時点。
関連: `memory/editor/editor-spec.md` §1（ファイル形式）、`memory/game/ipad-build-issues-r1.md`
（dspTime量子化の対処）、`memory/editor/editor-ui-rework-r6.md` §4（エディタ側の音源読み込み）。

---

## §0 このドキュメントの立ち位置

ロードマップ上は Phase 5 の「16. スコアリング、クリア条件、ライフ、リザルト、曲選択、画面遷移」に
あたる。ただし**全部はやらない**。目的は「ゲームとして完成させる」ことではなく
**「作った譜面を実機で鳴らして確かめられる」最短経路を、後から作り込める形で敷くこと**。

したがって設計の判断基準は一貫して次の2つ:

- **(A) 今テストするのに要るか** — 要らないものは作らない。
- **(B) 後で作り込むときに、今作ったものを捨てずに済むか** — 捨てることになる構造は選ばない。

---

## §1 現状の棚卸し

### 1.1 既にあるもの（流用できる）

| 機能 | 所在 | ゲームから使えるか |
|---|---|---|
| 譜面/曲メタのパース | `Chart/ChartSerializer.cs` | **そのまま使える**。UnityEngine非依存の純粋C#で、クラスのdocコメントに「ゲーム本体もこれで譜面を読むためEditor専用にしない」と明記済み |
| tick→秒の解決 | `ChartFormat.ResolveTimes` / `ResolveSlideComboPoints` / `BuildScrollTimelines` | そのまま使える |
| 音源の読み込み | `ChartEditorApp/PreviewSystem.cs:310-410` | **ロジックは流用、置き場所は移す**（§3.2） |
| 音源同期の時計 | `ChartEditorApp/PreviewClock.cs` | **概念だけ流用、実装は流用しない**（§4） |
| SEプール(PlayScheduled) | `PreviewSystem.cs:189-200` | 流用可。ただしゲームは「予約」ではなく「判定時に即時」（§7） |
| 音量 | `PreviewSystem` の Master/Bgm/Se | 流用可 |
| 設定のJSON永続化 | `ChartEditorApp/EditorSettings.cs` の `EditorSettingsStore` | **同じパターンをゲーム側に複製**（§6.3） |
| オフセットの永続化 | `Stage/OffsetSettings.cs` | あるが PlayerPrefs。§6.3 で置き換え |
| スコア/判定内訳 | `Gameplay/Score.cs` | そのまま使える。`ComputeScore(totalComboPoints)` まで実装済み |
| 一時停止/再開/シーク/リトライ | `Game/GameController.cs:69-113` | ロジックは完成している。**呼び出す口（UI）が無いだけ** |

### 1.2 無いもの（今回作る）

- ゲーム側から譜面ファイルを読む経路（`GameController.cs:131` は `ChartBuilder.BuildDemoChart` 決め打ち）
- **楽曲の再生そのもの**。`GameController` の `AudioSource` は `metronomeSource` の1本だけで、
  `SongClock` は音源を持たない dspTime のフリーランクロック。今のゲームは**曲が鳴っていない**。
- 画面という概念（シーンは `SampleScene`（ゲーム）と `ChartEditor`（エディタ）の2つ。
  `EditorBuildSettings` には `SampleScene` のみ）
- 実機から触れる操作系一切（一時停止/リトライは**キーボード専用**。iPadにキーボードは無い）
- ヒットSE（`Assets/Audio/SE/` は README のみで素材ゼロ）
- 譜面の終わりという概念

---

## §2 全体構成: 単一シーン + アプリ状態機械

### 2.1 結論: シーンは増やさない

`SampleScene` 1つのまま、`AppState` の状態機械にする。画面は UI Toolkit の
オーバーレイとして重ね、プレイ中以外はステージ側の更新を止める。

v1では**曲選択画面を作らない**（§5.2）ので、STARTが選ぶ曲は設定で指定した1曲になる。
状態そのものは将来 `SongSelect` を差し込める並びにしておく。

```
Title ──START──> Loading ──完了──> Playing ──┬─ Pause ──┬──> Playing
  ^                  ^                        │          ├──> Loading (はじめから)
  │                  │                        │          └──> Title
  │                  └────────────────────────┴─ Result ─┴──> Title
  └───────────────────────────────────────────────────────────┘

         ※ 将来: Title ──START──> SongSelect ──決定──> Loading ...
            戻り先が Title か SongSelect かだけの差になるよう、
            「1つ前の画面」を AppController が保持する形にしておく。
```

### 2.2 なぜシーン分割にしないか

1. **Inspector配線を各シーンで再現する必要が出る**。`StageController` / `NoteView` /
   `TouchInputManager` / `StageOverlay` はシーン上で配線されており、特に
   `StageOverlay.panelSettingsAsset` は**実行時生成では実機で描画できないことが実証済み**
   （`ipad-build-issues-r1.md` ①）。シーンを増やすたびに同じ地雷を踏む面積が増える。
2. **シーン遷移のコストが、遷移の意味に見合わない**。タイトル↔プレイの往復は頻繁に起きる
   （リトライ）のに、シーンロードはステージ/ノーツのGameObject一式を毎回捨てて作り直す。
   一方、状態機械なら**ノーツメッシュの再生成（約8万頂点）だけ**で済み、これは
   どのみちリトライで必要になる処理ですらない（同じ譜面ならメッシュは使い回せる、§8.2）。
3. **状態機械は後からシーン分割へ移せるが、逆は苦しい**。判断基準(B)。

### 2.3 実装の形

`GameController` は既に「Stage/Notes/Input/Judge/Clock を束ねる統括役」なので、
そこへ状態を足すのではなく、**上に `AppController` を1枚被せる**。

- `AppController` (新規, MonoBehaviour): `AppState` の保持、画面（UI Toolkit）の出し分け、
  `SongLoader` の駆動、`GameController` への指示。
- `GameController`: 「1曲をプレイする」責務に限定。`Start()` での自動 `StartGame()` を
  やめ、`AppController` から `LoadChart(chart, song) → StartGame()` と呼ばれる形に変える。
- 画面UIは `StageOverlay` とは**別の `UIDocument`**にする（`StageOverlay` は
  `pickingMode = Ignore` の非入力オーバーレイであり、ボタンを持たせると設計が濁る）。
  `PanelSettings` は `Assets/UI/Game/GameOverlayPanelSettings.asset` を流用してよい。

---

## §3 譜面・音源の読み込み

### 3.1 曲データの置き場所

エディタは `EditorSettings.songsRoot`（既定 `~/Documents/muses/songs/`）以下に
`<song-id>/` フォルダ単位で `song.museproj` + `<difficulty>.muses` + 音源 を置く
（`editor-ui-rework-r9.md` §2〜§4）。ゲームも**この構造をそのまま読む**。

置き場所の解決は**探索パスの列**にして、プラットフォームで分岐させない:

```
1. Application.persistentDataPath/songs/     ← iOSはここが唯一の書き込み可能な実体
2. <ユーザー設定の songsRoot>/               ← デスクトップのみ。エディタと同じ既定値
3. Application.streamingAssetsPath/songs/    ← ビルドに焼いた同梱曲（読み取り専用）
```

最初に見つかったものを使うのではなく、**全部を走査して曲リストを合成する**
（同じ song-id があれば 1 が優先）。こうしておくと「同梱曲＋持ち込み曲」が
後から自然に同居でき、判断基準(B)を満たす。

**iPadへの持ち込み手段**は Info.plist の2キーで解決する:

- `UIFileSharingEnabled = YES`
- `LSSupportsOpeningDocumentsInPlace = YES`

これで iOS の Files アプリに muses のフォルダが現れ、Mac の Finder / AirDrop /
iCloud Drive から曲フォルダを丸ごと放り込める。Unity iOS の
`Application.persistentDataPath` はアプリコンテナの `Documents/` を指すため、
上記1のパスがそのまま共有先になる。**ビルドし直さずに譜面を差し替えられる**のが
この方式の要点で、テストの反復速度に直結する。

> Xcodeプロジェクトを毎回手で触らずに済ませるため、`Assets/Editor/` に
> `IPostprocessBuildWithReport` を1つ足して Info.plist へ自動追記する。
> signing の手作業を減らした前例（[[muses-unity-port-progress]] の未解決事項2）と同じ発想。

### 3.2 読み込みパイプライン

`Muses.Game.SongLoader`（新規）に、`PreviewSystem` の音源読み込みロジックを移植する。
**`PreviewSystem` から切り出して共有する**のが本筋だが、`PreviewSystem` は
エディタのrig（Camera/RenderTexture/Judge）と強く結合しているため、切り出すのは
音源読み込みの部分だけに留める:

```
Muses.Audio.AudioFileLoader  (新規・共有)
  ├─ LooksLikeOpus(path)                 ← PreviewSystem.cs:363 から移動（既にpublic static）
  ├─ AudioTypeFromExtension(path)        ← PreviewSystem.cs:346 から移動
  └─ IEnumerator Load(path, cb)          ← UnityWebRequestMultimedia、PreviewSystem.cs:379 相当
```

`PreviewSystem` は移動先を呼ぶだけに変える（挙動は不変。既存の実機実績を壊さない）。

ゲーム側のロード手順:

```
1. song.museproj を読む            ChartSerializer.ReadSongMeta       同期・数ms
2. <difficulty>.muses を読む       ChartSerializer.ReadChart          同期・数ms
3. chart.bpmEvents = song.bpmEvents のコピー                          ※PreviewSystem.Rebuild:217 と同じ
4. 時刻解決                        ChartFormat.ResolveTimes 等        同期・数ms
5. 音源をデコード                  AudioFileLoader.Load               ★非同期・数百ms〜数秒
6. ノーツメッシュ生成              NoteView.Build                     ★同期・数十〜数百ms（約8万頂点）
7. Judge.Prepare / Reset
```

### 3.3 ロード画面は「あった方がいい」ではなく「必要」

ステップ5は本質的に非同期（`UnityWebRequest` のコルーチン）で、4分のoggを
iPadでデコードすると無視できない時間がかかる。ステップ6は同期でメインスレッドを
止める。**この2つを隠す画面が無いと、決定した瞬間に数秒フリーズして
いきなり曲が始まる**という体験になり、しかも「固まった」のか「読んでいる」のかが
判別できない。ユーザーの言う「決定→ロード→実行の流れを今のうちに確立させておきたい」は
正しく、ここは飾りではない。

進捗は「5が非同期・6が同期」なので**正確な％は出せない**。段階ラベル
（「譜面を読み込み中」→「音源を展開中」→「準備中」）+ 不定形インジケータで十分。

---

## §4 時計と音源同期 ← **今回いちばん重要な設計判断**

### 4.1 2つの時計が既にあり、どちらもそのままでは使えない

| | `Audio/SongClock.cs`（ゲーム） | `ChartEditorApp/PreviewClock.cs`（エディタ） |
|---|---|---|
| 時刻の正 | `AudioSettings.dspTime - t0` | `AudioSource.time`（範囲外のみ仮想クロック） |
| 音源 | **持たない**（無音のフリーラン） | 持つ。`PlayScheduled` で開始揺らぎを排除 |
| 曲オフセット | 無し | `Offset`（= `SongMeta.offsetSec`）を吸収 |
| 再生レート | 固定1.0 | 0.25x〜2.0x（`source.pitch`） |
| **フレーム補間** | **有り**（`Advance()`） | **無し** |

`SongClock.Advance()` は、**iPad実機で dspTime が23〜25Hzでしか更新されない**という
実測（`ipad-build-issues-r1.md` ②-B）に対して、v1の「クランプ方式」が周期的な
スタッタを生んだ末に v2 の「毎フレーム必ず前進＋ズレは加算補正」へ辿り着いた、
**苦労して得た資産**である。これを捨ててはいけない。

一方 `PreviewClock` は `AudioSource.time` を正としており、これも
**同じDSPバッファ単位でしか更新されない**。エディタでは露見しにくかっただけで、
そのままゲームに持ってくれば ②-B が再発する。

### 4.2 方針: dspTime を唯一の正とし、音源はそこへ「貼り付ける」

**`AudioSource.time` は読まない。** `SongClock` の
「`songTime = dspTime - t0` を `smoothed` で補間する」構造を**一切変えず**、
再生開始時に音源を dspTime 基準で予約するだけにする。

```csharp
// Start / Resume / Seek で共通に呼ぶ
void ScheduleMusic(double songTime)
{
    double audioTime = songTime + song.offsetSec;   // 音源上の位置
    double startDsp  = AudioSettings.dspTime + ScheduleLeadSec;  // 0.05s（PreviewClockと同値）

    if (audioTime >= 0 && audioTime < clipLen) {
        music.time = (float)audioTime;
        music.PlayScheduled(startDsp);
        t0 = startDsp - songTime;                    // ← dspTime基準の原点をここで確定
    } else if (audioTime < 0) {
        // 前奏区間（tick0が音源より前）: 音源は0秒から、開始を先送りするだけ
        music.time = 0f;
        music.PlayScheduled(startDsp - audioTime);
        t0 = startDsp - songTime;
    } else {
        // 音源の終端より後: 鳴らす音が無い。時計だけ進める
        t0 = startDsp - songTime;
    }
}
```

`t0` を `startDsp` 基準で置くことで、**「音が実際に鳴り始める瞬間」と
「songTimeがその値になる瞬間」が dspTime 上で厳密に一致する**。以降 `Advance()` は
今までどおり `dspTime - t0` を正としてフレーム補間するだけでよく、
**iPadの ②-B 対処はそのまま生きる**。

前奏区間・終端区間の扱いは `PreviewClock` の r8 §1 と同じ考え方をそのまま移す
（あちらで一度解いた問題なので、解き直さない）。

**この方式が成立する前提**: `AudioSource` の再生進行と `AudioSettings.dspTime` が
同じオーディオデバイスクロックで駆動されており、両者の間に相対ドリフトが無いこと。
Unityの実装上これは成り立つはずだが、**曲が3〜4分と長いので実機で1回確認する**
（ドリフト検証は §9 の実装順序に入れてある）。もし無視できないドリフトがあれば、
`Advance()` の drift 補正の入力を `dspTime - t0` から `music.time - offsetSec` へ
差し替えるだけで対処できる（構造は変えずに済む）。

### 4.3 3種類のオフセットの整理

混同しやすいので、責務と持ち主を明示しておく。

| 名前 | 意味 | 持ち主 | 単位 | 曲ごと/全体 |
|---|---|---|---|---|
| `SongMeta.offsetSec` | 音源の先頭 → 譜面tick0 のズレ | **譜面ファイル**（`song.museproj` の `@OFFSET`） | 秒 | 曲ごと |
| `StageConfig.judgeOffsetMs` | 端末の入力＋出力レイテンシ。判定時刻だけをずらす | **プレイヤー設定** | ms | 全体 |
| `StageConfig.visualOffsetMs` | 音と描画位置のズレ。描画時刻だけをずらす | **プレイヤー設定** | ms | 全体 |

適用箇所も分離したまま維持する（既に `GameController.JudgeTime()` /
`VisualTime()` が正しく分けている）。`offsetSec` だけが**時計の内部**で吸収され、
残り2つは**時計の外**で足される、という非対称が設計の要点。

---

## §5 画面の中身

### 5.1 タイトル

ユーザー指定どおり **STARTボタン1つ**。ただしこの画面には後で
「設定」「終了」が必ず生えるので、**縦並びのボタンリストとして作る**（1個でも）。

### 5.2 曲選択 — **v1では作らない**（ユーザー確定、2026-08-07）

STARTで始まる曲は**設定画面で選ぶ**。設定に「曲」の項目を1つ置き、
§3.1 の探索で得た曲＋難易度のリストからドロップダウンで選んで
`PlayerSettings` に保存する（`songId` / `difficulty` の2つの文字列）。

こうしておくと:

- **曲選択画面を作るときの実装が「探索結果をリストで見せる」だけになる**。
  探索（`SongLoader.Enumerate()`）とメタの読み出しは v1 で必ず要るので、
  一覧UIを後から被せるだけで済む。判断基準(B)。
- テスト中に曲を替える頻度は低い（1つの譜面を繰り返し詰めるのがテストの実態）ので、
  設定の奥にあっても実害が無い。

ジャケット（`@JACKET`）とプレビュー再生（`@PREVIEW`）は当然やらない
（フォーマットには既にあるので後付け可）。

**曲が1つも見つからないときの案内文は v1 でも必須。** 「`~/Documents/muses/songs`
（iPadなら Files アプリの muses フォルダ）に曲フォルダを置いてください」＋
**実際に探した全パスをそのまま列挙する**。エディタの音源セクションで
「探したフルパスをそのまま出す」（`editor-ui-rework-r6.md` §4.1）が効いた前例に倣う。
曲が無ければ START は無効化し、そこに理由を出す。

### 5.3 プレイ中の操作系（実機で必須）

**キーボードが無い実機で、現状ゲームを止める手段が一切無い。** 最低限:

- 画面隅（左上）の**ポーズボタン**。ノーツの通り道と干渉しない位置に置く。
- ポーズ中のメニュー: 「再開」「はじめから」「設定」「曲選択へ戻る」
- **設定はポーズメニューから開ける**こと。これが §6 の設計を大きく単純にする（後述）。

`GameController` の `Pause()` / `Resume()` / `Retry()` はもう動くので、
繋ぐだけでよい。既存のキーボードショートカット（`HandleDevInput`）は
Unity Editor での確認に有用なので残す。

### 5.4 リザルト

**最小のものを作ることを推奨する。** 理由:

- `Score` に `perfectPlus/perfect/good/miss/maxCombo/ComputeScore()` が**既に全部ある**。
  表示するだけなので実装コストがほぼゼロ。
- 「曲が終わったらタイトルへ戻す」にすると、**テスト中に結果が見えない**。
  自作譜面のテストとは「どこで落としたか」を見ることなので、判定内訳が消えるのは痛い。
- 「終わったら何が起きるか」が未定義なままだと、§8 の終了条件を決める動機も曖昧になる。

中身: スコア / 最大コンボ / P+・P・G・M の内訳 / 「リトライ」「曲選択へ」の2ボタン。
グレード・ランク・クリア判定・ライフは**やらない**（判断基準A）。

---

## §6 設定

### 6.1 何をライブ調整可にできるか（技術的な線引き）

これは好みではなく**実装上はっきり決まる**ので、基準として書いておく:

- **毎フレーム渡す値 / シェーダのuniform → その場で反映できる（軽い）**
  - `hiSpeed` … `NoteView.UpdateScroll(t, hiSpeed)` に毎フレーム渡している
  - `thicknessFrac` / `thicknessMinFrac` / `skyThicknessMul` … `NoteView` のプロパティ
    setter が `ApplyThicknessUniforms()` を呼ぶ構造に既になっている
  - 音量 … `AudioSource.volume` / `AudioListener.volume`
  - `judgeOffsetMs` / `visualOffsetMs` … 毎フレームの `JudgeTime()`/`VisualTime()` で加算
- **`NoteView.Build` 時に頂点へ焼き込む値 → 変更にはメッシュ再構築が要る（重い）**
  - **`thetaDeg`（ステージ角度）がここに入る**。`NoteGeometry.Build` は
    `d.skyHeight` / `d.zJudge` を頂点位置に直接使っており、これらは `thetaDeg` から
    導出される。プレイ中にスライダーで動かすと、毎フレーム約8万頂点の再生成になる。
  - 同様に `cells` / `U` / `farFrac` / `readAheadSec` / アスペクト比。
    アスペクト比については `editor-ui-rework-r13.md` §7.2 で
    「0.15秒デバウンス後に作り直す」対処を既に入れており、**同じ落とし穴**である。

### 6.2 v1に入れる項目

| 項目 | v1 | 理由 |
|---|---|---|
| **曲・難易度の選択** | **入れる** | §5.2 のとおり、v1ではここが曲選択の役割を兼ねる |
| `judgeOffsetMs` | **入れる** | これが無いと実機テストが成立しない。最優先 |
| `visualOffsetMs` | **入れる** | 同上 |
| マスター音量 / BGM音量 / SE音量 | **入れる** | ユーザー指定。実装は `AudioSource.volume` だけ |
| ハイスピード | **入れる** | 音ゲーとして事実上必須。かつ実装コストがほぼゼロ（6.1） |
| ノーツの厚み | **入れる** | 同じくuniformなのでコストがゼロ。`note-visual-r1.md` の調整の続きが実機でできる |
| ステージ角度 | **入れない**（ユーザー確定） | 6.1 のとおり頂点に焼き込まれるためライブ調整できず、他の項目と扱いが揃わない。従来どおり `SampleScene` の Inspector で調整する。設定に出すのは、値をシーンから切り離す（§10-8 の ScriptableObject 化）目処が立ってから |
| メトロノーム | 入れる（トグル1つ） | 既存の `StageConfig.metronome` を繋ぐだけ。オフセット合わせの実作業で効く |
| 表示fps / デバッグ表示群 | **入れない** | `StageConfig` の `showBand` 等はステージ調整用。設定画面ではなく開発時のInspectorで足りる |
| キーコンフィグ・解像度・言語 | **入れない** | 判断基準(A) |

### 6.3 オフセット合わせの方法（ユーザー確定、2026-08-07）

専用キャリブレーション画面（メトロノームに合わせてタップして統計を取る）は**作らない**。
代わりに「**ポーズメニューから設定を開き、スライダーを動かして再開する**」で回す。

- 実装が軽い（§5.3 のポーズメニューに1画面足すだけ）
- **実際の譜面・実際の曲で合わせられる**ので、キャリブレーション画面より結果が直接的
- スライダー操作直後に `Resume()` すれば、数秒で効果を確認して詰められる

専用画面は、値が定まらない/毎回ぶれる、と分かってから作れば足りる。

### 6.4 永続化: PlayerPrefs をやめて JSON にする

現状 `OffsetSettings.cs` は PlayerPrefs。これを **`Muses.Game.PlayerSettings` +
`PlayerSettingsStore`（`Application.persistentDataPath/player-settings.json`）** へ
置き換える。`EditorSettingsStore`（`EditorSettings.cs:323-399`）と同じ構造を複製する。

理由はエディタで一度出した結論（`editor-ui-rework-r5.md` §1.2）とまったく同じ:

1. 項目が増えるとPlayerPrefsのキーが散らかり、既定値の扱いが各所に散る。
   `JsonUtility.FromJsonOverwrite` なら**欠けたフィールドは既定値のまま残る**ので、
   項目追加時のマイグレーションが要らない。
2. **macOS/iOSではPlayerPrefsの中身が実質見えない**。値がおかしいときに
   目視で確認・手で直す手段が無いのは、実機調整では致命的。
3. `Application.persistentDataPath` は §3.1 で Files アプリから見えるようにするので、
   **設定ファイルもiPadから直接覗ける**ようになる。

移行は「PlayerPrefs に既存キーがあれば初回だけ読んで JSON へ書き、以後は JSON」で
充分（値は2つしかない）。

---

## §7 ヒットSE

**入れることを推奨する。** ユーザーが「SE音量」を挙げている以上、鳴らす対象が要る。
かつ、オフセット合わせは**SEが鳴らないと感覚的に極めてやりにくい**（曲だけを
頼りに数msを詰めることになる）。§6.3 の方針と直結する。

方式は**エディタとは変える**:

- エディタ（`PreviewSystem`）は**オートプレイの予告として `PlayScheduled` で先読み**する。
  これは「ノーツの時刻」が既知だからできる。
- ゲームは**プレイヤーが叩いた瞬間に鳴らす**のが正しいので、`Judge` の判定成立時に
  `PlayOneShot` で即時再生する。予約は不要（というより有害）。

`Judge` は既に `noteView.SetNoteAlpha` をコールバックで受け取る形（`Judge(cfg, callback)`）に
なっているので、**同じパターンで判定成立コールバックを1本足す**のが素直。

素材は `Assets/Audio/SE/` が空（READMEのみ）。`PreviewSystem` の
「未設定ならフォールバック、最終的に実行時合成のクリック音」という既存の逃げ道が
そのまま使えるので、**素材ゼロでも実装を進められる**。

---

## §8 譜面の終了条件

### 8.1 いつ終わるか

「最後のノーツを叩いた瞬間に画面が切り替わる」のは体験として悪く、
「音源が終わるまで待つ」のは前奏だけの短い譜面で延々待つことになる。両方を見る:

```
endTime = max(最終ノーツの終端時刻, 音源の終端時刻 - offsetSec) + 余韻
```

- 最終ノーツの終端は `ChartMath.NoteEnd(n)` の最大値（`PreviewSystem.BuildBarTimes:280` で既に使用）
- 音源終端は `clip.length - offsetSec`（譜面時間へ変換）
- 余韻は 2 秒程度。判定窓（GOOD半幅100ms）＋最後の演出が消えるまでを含める

到達したら `Playing → Result` へ遷移する。

### 8.2 リトライ時にメッシュを作り直さない

同じ譜面をやり直すだけなら `NoteView.Build`（約8万頂点）は**不要**。
必要なのは `Judge.Reset()` → `Judge.Prepare()` → `noteView.FlushAlpha()` →
時計を0へ、だけ。現状の `GameController.Retry() → StartGame() → Rechart()` は
毎回 `Build` からやり直しているので、**譜面が変わらない場合は `Build` を飛ばす**分岐を
入れる（§2.2 で「状態機械ならメッシュを使い回せる」と書いた実体がこれ）。

ただしステージ角度（§6.1）やアスペクト比が変わった場合は作り直しが要る。
`lastBuiltAspect` と同じ発想で **Build時のパラメータを記録して比較する**のが確実。

---

## §9 実装順序

各段階の終わりで**必ず実機で動く**ように並べてある（途中で止めても損をしない）。

1. **`AudioFileLoader` の切り出し**（`PreviewSystem` から移動、挙動不変）
   → エディタが今までどおり動くことだけ確認。
2. **`SongClock` の音源対応**（§4.2）。曲選択もUIも無いまま、
   `GameController` の Inspector に曲フォルダのパスを直接書いて1曲だけ鳴らす。
   → **ここで「音とノーツが合っているか」を実機で見る**。設計上いちばんリスクが高いのはここ。
   → 併せて **dspTimeとAudioSourceのドリフト測定**（曲の頭と終わりで
      `music.time` と `songTime + offsetSec` の差を出す）。
3. **`SongLoader` と探索パス**（§3.1/§3.2）＋ Info.plist の後処理。
   → iPadのFilesアプリへ曲を放り込んで読めることを確認。
4. **ポーズボタンとポーズメニュー**（§5.3）。
   → **ここで初めて実機テストが実用になる**（止められる・やり直せる）。
5. **設定画面**（§6）＋ `PlayerSettings` のJSON化。
   → オフセットを実機で詰める。
6. **ヒットSE**（§7）。→ 5に戻ってオフセットを詰め直す。
7. **タイトル / ロード**（§2.1/§5.1）＋ 設定への曲・難易度の選択（§5.2）。
   → 「決定→ロード→実行」の確立。**あえて後ろに置いている**: 2〜6の方が
     テストの成立に直結し、7は無くてもInspector直指定で回せるため。
8. **終了条件とリザルト**（§8/§5.4）。

---

## §10 決定事項と、残る未決事項

### 10.1 決定済み（2026-08-07、ユーザー判断）

1. **iPadへの曲の持ち込み**: Files アプリ経由（§3.1）。Info.plist の2キーを
   ビルド後処理で自動追記する。譜面差し替えに再ビルドが不要になる。
2. **曲選択画面**: v1では**作らない**。設定の「曲・難易度」ドロップダウンで代替（§5.2）。
3. **タイトル画面**: 作る（STARTボタン1つ、§5.1）。
4. **リザルト画面**: 作る（最小構成、§5.4）。
5. **ヒットSE**: 入れる（§7）。素材ゼロでも合成クリック音で始められる。
6. **ステージ角度**: 設定に出さない（§6.2）。
7. **オフセット合わせ**: ポーズメニューのスライダー方式（§6.3）。

### 10.2 まだ決まっていないこと

1. **SE素材を用意するか、合成音で進めるか**（§7）。合成音で始めて、
   オフセット調整に支障が出たら差し替える、で問題ないはず。
   なお `Assets/Audio/SE/` はエディタと共有なので、素材を入れれば両方で鳴る。
2. **`SongMeta.offsetSec` と `visualOffsetMs` の符号規約**を、エディタとゲームで
   突き合わせて1回確認する必要がある。**現状ゲーム側は音源を鳴らしていないため、
   `visualOffsetMs` の符号が実機で検証されたことが一度も無い**（デフォルト0のまま
   誰も動かしていない）。§9-2 の段階で必ず両方向に振って確認する。
3. **エディタとゲームの `StageConfig` の二重管理**（`SampleScene.unity` の値 vs
   `StageConfig.Default()`）。設定項目が増えると再び食い違う。
   `editor-ui-rework-r13.md` §10-2 の ScriptableObject 化を**この段階で片付けるか**、
   また先送りするか。§6.2 でステージ角度を設定に出さない判断をしたことで、
   当面の食い違いリスクは下がったので**先送りでよい**と考えるが、
   「設定でノーツ厚みを変えられる」以上、`EditorSettings.thicknessFrac` と
   `PlayerSettings` 側の同名項目という**3つ目の分散**が生まれる点は認識しておく。
4. **リトライ時にメッシュを作り直さない最適化（§8.2）を v1 で入れるか**。
   入れなくても動く（今と同じ）ので、実機でリトライが体感的に重かったら入れる、
   という順でよい。
