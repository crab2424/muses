# 譜面エディタ 配置時の選択・音源コーデック・曲フォルダ（第7弾、2026-08-03 rev.1）

`memory/editor-ui-rework-r6.md`（コミット `4925444`）の続き。ユーザーから出た
**1件の実装項目（§1）＋ 2件の質問（§2・§3）** を扱う回。ただし §3 の調査中に
**新規譜面が二度と開き直せない実バグ**が見つかったため、§3 は質問回答だけでなく
実装項目としてもスコープに入る。

**このドキュメントは実装計画。ユーザーの確認後に実装へ入る。**

- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（2917行）、
  `ChartEditorApp.UI.cs`（2248行）、`ChartEditorApp.Commands.cs`、`PreviewSystem.cs`、
  `EditorSettings.cs`、`PreviewClock.cs`。
- Unity 6000.5.6f1 / macOS arm64 スタンドアロン。
- **未決事項は §4 に集約**。着手前にここへ回答をもらう。

---

## 0. 着手前に共有すべき調査結果

### 0.1 【実バグ】新規譜面を保存しても `song.muses` が作られない ＝ 二度と開けない

ユーザーの「`.muses` がまだありません」は、単に作っていないのではなく**作れない**のが実体。

```csharp
// ChartEditorApp.cs:631-638  SaveChartToPath()
ChartSerializer.WriteChart(path, header, chart, song);
if (songMetaDirty && !string.IsNullOrEmpty(songPath))   // ← songPath が空なら書かれない
{
    ChartSerializer.WriteSongMeta(song, songPath);
}
```

`songPath` に代入している箇所は**プロジェクト全体で `OpenChartFromPath()` の1箇所だけ**
（`ChartEditorApp.cs:597`、grep 実測）。つまり:

1. 「新規」→ 編集 → 「別名で保存」で `cube.muses` は書ける。
2. しかし `song.muses` は**一度も書かれない**（`songPath` が空のまま）。
3. 次に開こうとすると `OpenChartFromPath` が
   `「同じフォルダに song.muses がありません」`（`:586`）で弾く。
4. → **エディタ内で作った譜面は、保存はできるが開き直せない。**

さらに `song.muses` が無いということは `song.audio` の解決先ディレクトリ
（`Path.GetDirectoryName(songPath)`）も永久に `null` なので、
**音源も絶対に読まれない**（r6 §0.2 の #1 と同じ経路）。
r6 で音源読み込みの診断を整備したのに鳴らない、の残り半分がこれ。

**§3.1 で直す。** 対処は「保存＝曲フォルダを確定する行為」とみなし、
`songPath` が空なら譜面と同じフォルダの `song.muses` を採用・無ければ新規作成する。

### 0.2 譜面が Finder で見つからないのは `~/Library` 配下に書いているため

```csharp
// ChartEditorApp.cs:431-432
browseDir = !string.IsNullOrEmpty(settings.browseDir) && Directory.Exists(settings.browseDir)
    ? settings.browseDir : Application.persistentDataPath;
```

macOS スタンドアロンの `Application.persistentDataPath` は
`~/Library/Application Support/DefaultCompany/muses/`
（`ProjectSettings.asset` の `companyName: DefaultCompany` / `productName: muses`）。

実測でこの中に `editor-settings.json` と `untitled.muses.autosave` が存在する
＝ここがずっと既定の作業フォルダだった。**`~/Library` は Finder の既定で非表示**なので、
「Unity 内のファイルを探しても見つからない」のは当然。そもそも Unity プロジェクト
（`unity/Assets/`）の中でもない。

**§3.2 で直す。** 既定を Finder から見える場所へ移し、アプリからも開けるようにする。

### 0.3 配置直後の選択が幅ショートカットを塞ぐ経路（ユーザー指摘の裏取り）

```csharp
// ChartEditorApp.cs:213-224  ChangeWidth(int sign)
if (selection.Count > 0) { ChangeSelectedWidth(sign); return; }   // ← 選択が最優先
if (!IsPlacementTool(currentTool)) return;
defaultWidthCells = Mathf.Clamp(defaultWidthCells + sign * step, step, Cells);  // ← ゴースト幅
```

`defaultWidthCells` は `DrawPlacementGhost`（`:1526`/`:1534`/`:1562`）と
実配置（`:1725`/`:1799`/`:1808`）の両方が読む「ゴーストの幅」。
配置直後に `SetSingleSelection(note)`（`:1734`）/ `SetMultiSelection(...)`（`:1818`）で
選択が入るため、以降 ←→ は**置いたばかりのノーツをリサイズする**方に流れ、
ゴースト幅を変えられない。ユーザーの指摘どおりで、r6 §9 Q2 の「選択優先」という
決定がそのまま連続配置と噛み合っていない。

---

## 1. 配置直後にノーツを選択状態にしない

### 1.1 方針

**新規配置したときは選択を「入れない」だけでなく「消す」。**
単に `SetSingleSelection` の呼び出しを削るだけだと、**配置前に別のノーツを選んでいた場合に
その選択が残り**、←→ が結局そちらのリサイズに流れて問題が再発する。

| 経路 | 現在 | 変更後 |
|---|---|---|
| Tap/ExTap/Flick を新規配置（`:1734`） | `SetSingleSelection(note)` | `ClearSelection()` |
| Slide を2点目クリックで完成（`:1818`） | `SetMultiSelection(AllPointRefs)` | `ClearSelection()` |
| 配置ツールで既存ノーツを踏んだ（r3 §7 の横取り） | 選択に横取り | **変更なし**（暴発防止として維持） |
| Select ツールでのクリック | 選択 | **変更なし** |
| ペースト確定（`:2677` 付近） | 選択される | **変更なし**（後述） |

つまり規則は「**新規に生成したノーツだけ選択しない**」。既存ノーツを掴む操作は全て従来どおり。

### 1.2 実装

- `ClearSelection()`（`:315-325`）は `selection` / `selectedNote` / 幅アンカー / 高さドラッグを
  まとめて切るので、そのまま使える。`SetSingleSelection` が追加で切っていた
  `pendingSlideStart` / `draggingNote` / `resizingActive` / イベント選択のうち、
  配置直後に立っている可能性があるのは `pendingSlideStart`（Slide完成時に既に `null` 代入済み）
  だけなので、`ClearSelection()` の呼び出しで足りる。
- `uiNeedsPropertyRefresh = true` を立てて右パネルを空表示に更新する。
- `SetSingleSelection(Note wholeNote)` オーバーロード（`:293`）は他に呼び出し元が無ければ削除。
  → 実装時に grep して確認する（ペースト経路が使っている可能性がある）。

### 1.3 副作用の確認（ここは §4 Q1 で確認したい）

**中継点追加ツール（AddWaypoint）が効かなくなる経路がある。**

```csharp
// ChartEditorApp.cs:1739  case EditorTool.AddWaypoint:
if (selectedNote is { kind: NoteKind.Slide })   // ← 選択中の Slide にしか挿せない
```

現在は「Slide を置く → そのまま AddWaypoint ツールに切り替えて中継点を足す」が
選択が残っているおかげで成立している。§1.1 の変更でこれが途切れ、
**Slide をいったんクリックして選び直す**手間が入る。

対処の選択肢:
- (a) 何もしない。Slide を選び直してから中継点を足す（操作が1手増える）。
- (b) 配置ツール→AddWaypoint への切り替え時にだけ「直前に配置したノーツ」を選び直す
  （`lastPlacedNote` を1つ覚えておく）。←→ は配置ツール中しか使わないので幅の問題は起きない。
- (c) AddWaypoint を「選択中の Slide」ではなく「クリック位置の直下にある Slide」に挿す方式へ
  変更する（選択に依存しなくなる。参照元 MikuMikuWorld も近い挙動）。

**推奨は (c)**。選択への依存自体を消せるので §1 の変更と根本的に噛み合い、
「中継点を足したい Slide を選んでからでないと押せない」という現在の分かりにくさも消える。
ただし (c) は AddWaypoint の当たり判定（帯のヒットテスト）を新規に書く必要があり、
今回のスコープを1段広げる。**(a) で始めて後日 (c) にする**のも妥当。

もう1点、**右パネルのインスペクタが配置直後に空になる**。
「置いた直後に easing / layerF を右パネルで微調整する」運用をしているなら手戻りになるので、
§4 Q1 で確認する。

---

## 2. Opus について（質問への回答）

### 2.1 「高音質・低負荷」というメリットは、muses の構成では大半が消える

ユーザーが Opus を選んだ理由を1つずつ検証した結果:

| 主張 | 実際 |
|---|---|
| **Vorbis より高音質** | **ビットレート依存**。Opus が明確に勝つのは概ね **96kbps 以下**の帯域。128kbps 以上ではどちらも透明に近く、現在使っている `-q:a 6`（≈192kbps）では**聞き分けはまず不可能**。音ゲー BGM は容量より品質優先で 160〜192kbps を使うのが普通なので、ちょうど差が消える領域。 |
| **低負荷** | ここが最大の誤解。muses は**再生前に PCM へ全展開**している（`DownloadHandlerAudioClip.GetContent`、`streamAudio=false`）。つまり**再生中のデコード負荷は Vorbis でも Opus でも 0**。差が出るのはロード時間だけ。しかも Unity には Opus デコーダが無いので**外部ライブラリ＝ピュア C# 実装**になり、ネイティブ libvorbis より**遅くなる**（負荷は改善どころか悪化する）。 |
| ファイルサイズ | ここだけは本物。同等品質で **20〜30% 小さい**。曲数が増えたときのアプリ/配布容量には効く（直近で 115MB→75MB の容量削減をやった経緯とは噛み合う）。 |

つまり **残る実利はファイルサイズだけ**で、音質・負荷の利点は muses の再生方式では出てこない。

### 2.2 それでも導入する場合の現実的な手段

- **Concentus**（MIT、ピュア C# の libopus 移植）＋ Ogg デマルチプレクサ。
  netstandard2.0 DLL として `Assets/Plugins/` に置けば Unity で動く。
  ピュア C# なので **IL2CPP / iOS / Android arm64 でもネイティブプラグインの ABI 問題が無い**のは
  素直な利点（ネイティブ libopus を各プラットフォーム分ビルドするより遥かに楽）。
- デコード結果を `AudioClip.Create` + `SetData` で `AudioClip` にすれば、
  以降の再生・シーク経路は現行と完全に同じにできる（`PreviewClock` / `SongClock` は無改修）。
- OS 側のデコーダに逃げる案は**不可**。Android は ogg/opus をネイティブ対応するが、
  **iOS の CoreAudio は Opus 非対応**。タブレット（iPad）が主対象なので詰む。

導入コスト:

1. 2.5分の曲をピュア C# で全デコードすると**数秒**かかりうる → 非同期化と進捗表示が要る
   （エディタは許容できても、ゲーム側の「曲選択→開始」のロード時間に直撃する）。
2. `AudioType.OGGVORBIS` 一本だった読み込み経路が**2系統に分岐**する（保守コスト）。
   ヘッダ判定（`LooksLikeOpus`、r6 で実装済み）を分岐に流用できるのは救い。
3. ライセンス表記（MIT）の同梱が必要。
4. 将来 Managed Stripping を上げるときの `link.xml` 保護対象が増える
   （容量調査のときに見送った項目と干渉する）。

### 2.3 推奨

**今は入れない。** 得られるのは容量 20〜30% だけで、コスト（ロード時間・二重経路の保守・
ライセンス・非同期化）が見合わない。Vorbis `-q:a 6` で音質は十分に透明。

**再検討する条件**（＝先送りであって却下ではない）:
- 収録曲が増えてアプリ/配布容量が実際に問題になったとき。
- 低ビットレート（96kbps 以下）で配布したい要件が出たとき。

導入するかどうかは §4 Q2 で確認する。**導入する場合はエディタ側だけでは意味がない**
（譜面とゲームは同じ曲フォルダの同じ音源ファイルを読むため）。エディタ・ゲーム両方に
入れる前提で見積もること。

### 2.4 ogg と wav、再生タイミングの正確さ（±1ms 以内）で優れているのは

**結論から言うと「原理的には wav が優れているが、muses の現構成では差はほぼ出ない。
±1ms を狙うなら見るべきはファイル形式ではない」。**

**(a) 形式の差が出るポイントは3つだけ**

| # | 論点 | ogg（Vorbis/Opus） | wav |
|---|---|---|---|
| 1 | **エンコーダ遅延 / プリスキップ** | Opus は既定 **312サンプル(6.5ms@48k)** の pre-skip をヘッダに持ち、デコーダが捨てる前提。Vorbis も同様の仕組み。規格どおりなら時間軸はズレないが、**「正しく処理される」ことが実装依存**。 | 先頭サンプル = 時刻 0。議論の余地なし。 |
| 2 | **シーク精度** | 圧縮のままストリーミング再生する場合、**ページ/granule 単位**でしかシークできない。エディタのスクラブに直結。 | サンプル単位で確実。 |
| 3 | 可逆性 | 非可逆（音質の話。タイミングとは独立） | 可逆 |

**ただし muses ではこの3つとも実質無効化されている**:

- #1 は「音源ごとに一定の固定オフセット」でしかなく、`@OFFSET`（`song.muses`）で1回測れば消える。
  ゼロにはならないが「音源を差し替えるたびに測り直し」というだけ。
- #2 は muses が**全展開して `AudioClip` に載せている**ため消える。
  `AudioSource.time` の設定は PCM サンプル単位で効く。

**(b) 実際に ±1ms を壊しているのは形式ではなく以下**

1. **出力バッファ長（DSP buffer）** — Unity の `dspBufferSize` は
   Best latency=256 / Good latency=512 / Best performance=1024 サンプル。
   48kHz なら 256 でも **5.3ms** 刻み。ここが最大の粒度。
2. **`Play()` か `PlayScheduled()` か** — `AudioSource.Play()` は
   「次のミックスブロック境界」で鳴り始めるので**最大 1 バッファ分ゆらぐ**。
   `PlayScheduled(AudioSettings.dspTime + lead)` は**サンプル精度**で開始できる。
   （r6 で SE 側は `PlayScheduled` 方式に変更済み。BGM 開始側も同じにすべき — §4 Q3）
3. **端末固有の出力遅延** — iPad で 10〜40ms のオーダー。これは
   `StageConfig.judgeOffsetMs` で吸収する設計に既になっている（Phase 0 で実装済み）。

**(c) 推奨（＝現行方針がすでに最適）**

- **BGM は ogg (Vorbis) のまま**。長尺で容量が効く上、全展開後は wav との差が消える。
  wav にすると 2.5分ステレオで **約 25MB → 数十MB** に膨らむだけで、得るものが無い。
- **SE は wav のまま**（r6 の `Assets/Audio/SE/*.wav` 方針どおり）。短く、
  インポート設定を PCM/Decompress On Load にすればデコード遅延が完全に消える。

**(d) 付随して見つかった要修正点**

`memory/editor-spec.md` §1.5 に
「`@AUDIO` は ogg 固定。Unity のインポート設定は **Vorbis / Streaming** を既定とする」
とあるが、**Streaming は上表 #2（シーク精度）と再生開始時のディスク I/O のゆらぎを持ち込む**ので
リズムゲームでは避けるべき。少なくとも SE と、ビルドに同梱する BGM は
**Decompress On Load** にする。editor-spec.md を rev 更新して直す。
（曲フォルダから実行時に読む BGM はそもそも Unity のインポート経路を通らないので無関係。）

---

## 3. BGM 配置ディレクトリ（質問への回答＋実装）

### 3.1 `song.muses` を保存時に必ず作る（§0.1 のバグ修正）

`SaveChartToPath()` を変更する:

```
保存先 path が決まったら:
  songDir = Path.GetDirectoryName(path)
  songPath が空 or songDir と別フォルダを指している
      → songPath = Path.Combine(songDir, "song.muses")
  WriteChart(path, ...)
  song.muses が存在しない、または songMetaDirty
      → WriteSongMeta(song, songPath)      // 存在しなければ新規作成
```

- 「別フォルダを指している」を含めるのは、**別名保存で曲フォルダを移した場合**に
  古いフォルダの `song.muses` へ書き戻してしまうのを防ぐため。
- 保存後は `preview.Rebuild(song, chart, songDir)` を呼んで音源ディレクトリを確定させる
  （現在は `OpenChartFromPath` からしか確定しない）。これで
  **「保存した瞬間に音源が読まれるようになる」**。
- `songMetaDirty` の条件を「ファイルが存在しない場合」にも広げるのが要点。

### 3.2 曲フォルダの既定を Finder から見える場所へ

| | 現在 | 変更後 |
|---|---|---|
| 譜面・音源（ユーザーのデータ） | `~/Library/Application Support/DefaultCompany/muses/` | **`~/Documents/muses/songs/`**（無ければ起動時に自動作成） |
| `editor-settings.json` / `*.autosave`（アプリの内部状態） | 同上 | **同上のまま**（変更しない） |

- 分ける理由: 設定ファイルと自動保存はアプリの内部状態でユーザーが触るものではない。
  `persistentDataPath` はそのための場所として正しい。**ユーザーが Finder で触りたいのは
  曲フォルダだけ**なので、そこだけ出す。
- `browseDir` の初期値をこの `songsRoot` にする。`settings.browseDir` に前回値が
  入っていればそちらを優先する現在の挙動は維持（`ChartEditorApp.cs:431`）。
- **設定画面（一般タブ）に「曲フォルダ」項目を追加**して変更可能にする
  （`EditorSettings` に `songsRoot` を追加。既定は上記）。
- **「Finder で開く」ボタン**を音源セクションと設定画面に置く。
  `Application.OpenURL("file://" + dir)` で macOS の Finder が開く。
  これがあれば「どこにあるか分からない」が構造的に起きなくなる。

editor-spec.md §1.2 のフォルダ構成
（`songs/<song-id>/{song.muses, <difficulty>.muses, audio.ogg, jacket.png}`）は
そのまま踏襲する。`~/Documents/muses/songs/` がその `songs/` に相当する。

### 3.3 「インポート → 該当ディレクトリにコピー」は可能か → **可能。推奨。**

スタンドアロンビルドなので `System.IO.File.Copy` がそのまま使える
（macOS の署名なしビルドでもユーザーのホーム以下は自由に読み書きできる。
サンドボックス制約は無い）。現在の `PickAudioFile`（`ChartEditorApp.UI.cs:1729-1739`）は
**別フォルダのファイルを選ぶと「読み込めません」と警告して終わり**という中途半端な状態なので、
ここをコピー方式に置き換える。

変更後の `PickAudioFile` の流れ:

1. `songDir`（= `Path.GetDirectoryName(songPath)`）が未確定なら
   → 「先に譜面を保存してください（曲フォルダが決まっていません）」で中断。
   §3.1 の修正後は「保存する」だけで解決する状態になる。
2. ファイルブラウザで音源を選ぶ。
3. 選んだファイルが既に `songDir` の中 → そのままファイル名を `song.audio` に入れる（現行どおり）。
4. 別フォルダ → **確認モーダル**
   「`<name>` を曲フォルダ `<songDir>` にコピーしますか？」→ OK で `File.Copy`。
   - 同名ファイルが既にある場合は「上書きしますか？」を追加で確認（既存の譜面が
     参照している音源を黙って壊さないため）。r6 のショートカット重複確認モーダルと同じ作り。
   - コピー後 `song.audio = Path.GetFileName(dest)` → `songMetaDirty = true` →
     `MarkPreviewDirty()` で即読み込み。
5. コピー失敗（権限・容量）は `statusMessage` にそのまま出す。

**Opus の取り違えをここで水際で止める**: コピー前に `LooksLikeOpus()`（r6 で実装済み）を
呼び、Opus なら「この音源は Opus です。Vorbis に変換してください」＋ ffmpeg コマンド例を
出してコピー自体を中断する。今回ユーザーが踏んだ罠を、ファイル選択の時点で潰せる。

### 3.4 （提案）新規曲フォルダの作成フロー

§3.1〜§3.3 を入れても、**最初の1曲を作るとき「フォルダを自分で作って保存先を打つ」**
という段差は残る。「新規曲」メニューを足すと一直線になる:

```
[ファイル] → 新規曲…
  曲ID（フォルダ名）: my-song
  タイトル:           サンプル曲
  難易度:             CUBE ▾
→ ~/Documents/muses/songs/my-song/song.muses   を生成
   ~/Documents/muses/songs/my-song/cube.muses   を生成（空譜面）
   songPath / chartPath を確定 → そのまま編集開始 → 音源を「…」でコピー
```

既存の「新規」（`NewChart`、`ChartEditorApp.UI.cs:2320`）は
**`chart` を空にするだけで `song` / `songPath` / `chartPath` には触らない**
（＝同じ曲に別難易度を作る動作）ので、意味が被らず共存できる。
むしろ現在の「新規」は曲フォルダが無い状態から押すと §0.1 の袋小路に入る入口になっている。

やるかどうかは §4 Q4 で確認する。

---

## 4. 未決事項（着手前に回答がほしい）

**Q1（§1.3）** 配置直後に選択が消えることの副作用の扱い:
- (a) そのまま（Slide に中継点を足すときは選び直す）
- (b) AddWaypoint ツールに切り替えたときだけ「直前に置いたノーツ」を選び直す
- (c) AddWaypoint を「クリック位置の下にある Slide」に挿す方式へ変更（推奨だがスコープ +1）

あわせて、**配置直後に右パネルのインスペクタが空になる**が問題ないか
（置いた直後に easing / layerF を右パネルで直す運用があるか）。

**Q2（§2.3）** Opus 対応ライブラリ（Concentus）を導入するか。
推奨は「今は入れない・容量が問題になったら再検討」。
入れる場合はエディタとゲーム両方が対象になる。

**Q3（§2.4b）** BGM の再生開始を `AudioSource.Play()` から
`PlayScheduled(dspTime + lead)` に変えるのを今回のスコープに入れるか。
±1ms を気にするなら効果が大きい箇所だが、§1〜§3 とは独立した変更。

**Q4（§3.4）** 「新規曲…」フロー（曲フォルダ生成ウィザード）を今回作るか。
作らない場合、最初の1曲だけは Finder で手動フォルダ作成が要る。

**Q5（§3.2）** 曲フォルダの既定は `~/Documents/muses/songs/` でよいか。
（リポジトリ内の `source/` に置きたい等の希望があれば合わせる。
ただし `source/` は `.gitignore` 済みなので Git 管理はされない。）

**Q6（§3.3）** 音源のファイル選択で `*.wav` も選べるようにするか。
Unity は wav を読めるので `AudioType.WAV` への分岐を足すだけ。
編集中だけ wav・配布は ogg、という使い分けができる。

---

## 5. 実装順（回答後）

1. **§3.1**（`song.muses` を保存時に作る）— 他の全てをブロックしているので最優先。
   これが直れば `.muses` も音源読み込みも動き出す。
2. **§3.2**（曲フォルダの既定変更・Finder で開く）— §3.1 と同じ保存/パス周りなのでまとめる。
3. **§3.3**（音源のインポート＝コピー）— §3.1 で `songDir` が確定するのが前提。
4. **§1**（配置直後に選択しない）— 独立。Q1 の回答で分量が変わる。
5. **§2**（Opus / `PlayScheduled`）— Q2/Q3 が「やる」の場合のみ。
6. **editor-spec.md の更新** — §1.2 のフォルダ構成の実在パス、
   §1.5 の「Vorbis / Streaming」→ Decompress On Load（§2.4d）。

---

## 6. 実装済み（2026-08-03、`dotnet build Assembly-CSharp.csproj`でコンパイル成功確認・未コミット→コミット予定）

ユーザー回答: Q1=(c)（中継点も選択状態に依存させない）、Q2=見送り、Q3=含める、Q4=作成する、
Q5=`songs/`の中に曲プロジェクトフォルダ（既存設計どおり）、Q6=wav/mp3も対応。

- **§1（配置直後に選択しない）**: `ChartEditorApp.cs`。Tap/ExTap/Flick配置後・Slide完成後を
  `SetSingleSelection`/`SetMultiSelection`から`ClearSelection()`に変更。未使用になった
  `SetSingleSelection(Note wholeNote)`オーバーロードは削除。
  **AddWaypointツール（クリック・ゴースト両方）を`selectedNote`依存から`HitTestSlideBand(L, pos)`
  依存へ変更**（Q1(c)。既存の帯ヒットテスト——右クリックメニューの「中継点を追加」が使っていた
  もの——をそのまま流用でき、選択有無に関わらずカーソル下のSlideに挿せるようになった）。
- **§3.1（`song.muses`が保存時に作られない実バグ）**: `SaveChartToPath()`で、`songPath`が
  未設定または保存先と別フォルダを指していれば`Path.Combine(songDir, "song.muses")`へ
  再確定し、`song.muses`が存在しなければ`songMetaDirty`の値に関わらず必ず書く。保存直後に
  `preview.Rebuild(song, chart, songDir)`も呼び、音源ディレクトリをその場で確定させる。
- **§3.2（曲フォルダの既定地・設定UI）**: `EditorSettings.songsRoot`（新規）＋
  `DefaultSongsRoot()`（`~/Documents/muses/songs`）。`Awake()`で`browseDir`の既定を
  `persistentDataPath`から`songsRoot`へ変更。設定モーダル一般タブに「曲フォルダ」行
  （表示ラベル＋「変更...」＋「Finderで開く」）、音源セクションにも「曲フォルダをFinderで開く」
  ボタンを追加。フォルダ専用ブラウザ`ShowFolderPickerModal`（新規、ファイル一覧を出さない版）と
  `OpenInFinder`（macOSは`open`コマンド、他OSは`Application.OpenURL`）を追加。
- **§3.3（音源インポート＝コピー）**: `PickAudioFile`を「別フォルダなら警告して終わり」から
  「確認モーダル→`File.Copy`」に変更。同名ファイルがある場合は上書き確認を別途出す。
  コピー前に`PreviewSystem.LooksLikeOpus()`（今回`public`化）でOpusを検出し、その場で
  変換コマンド例を出してコピー自体を中断する。汎用`ShowConfirmModal`ヘルパーを新設
  （r6のキー重複確認モーダルと同じ骨格を一般化）。
  **Q6**: `ShowFilePickerModal`を単一patternから`string[] patterns`対応に変更し
  `*.ogg`/`*.wav`/`*.mp3`を1覧に統合。`PreviewSystem.TryLoadAudio`/`LoadAudioCoroutine`は
  拡張子で`AudioType`（OGGVORBIS/WAV/MPEG）を分岐（Opus判定は`.ogg`のときのみ実施）。
- **§3.4（新規曲ウィザード）**: 「ファイル」メニュー・ツールバーに「新規曲...」を追加。
  `ShowNewSongWizard()`（フォルダ名・タイトル・難易度ドロップダウン、既定CUBE）→
  `CreateNewSong()`が`songsRoot/<songId>/`を作成し`song.muses`と`<difficulty>.muses`を新規生成。
  **既存の曲フォルダに難易度を追加するケースも1関数でカバー**（`song.muses`が既にあれば
  読み込んで流用し上書きしない、difficultyファイルが既にあればエラー表示して中断）。
  既存の「新規」(`NewChart`)は`chart`のみ空にする従来動作のまま変更していない
  （役割が違う: 同じ曲に別難易度を作る操作）。
- **§2 Q3（`PlayScheduled`）**: `PreviewClock.Play()`の`source.Play()`を
  `source.PlayScheduled(AudioSettings.dspTime + 0.05)`に変更。±1ms 精度を狙うときに
  最初に効く「次のミックスブロック境界まで待つ」ゆらぎ（48kHz/256サンプルでも5.3ms）を消す。
- **Q2（Opus対応ライブラリ）**: 見送り。コード変更なし。

**未検証（次回セッションでUnity Editorでの実機確認が必要）**:
1. 配置直後に選択されないこと・幅ショートカットが連続配置中に効くこと。
2. AddWaypointツールが未選択のSlideにもゴースト・挿入できること。
3. 新規曲ウィザードで作った曲を保存→閉じる→開き直しが通ること（§0.1のバグ修正の本丸）。
4. 音源インポート（別フォルダから曲フォルダへのコピー、上書き確認、Opus検出中断）。
5. wav/mp3の音源読み込み。
6. 設定画面の「曲フォルダ」変更・Finderで開くボタン。
7. `PlayScheduled`化後もプレビュー再生・シーク・ポーズが従来どおり動くこと（回帰確認）。
