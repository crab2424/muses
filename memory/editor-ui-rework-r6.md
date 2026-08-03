# 譜面エディタ 幅ショートカット・カーソル中央追従・音源/SE（第6弾、2026-08-03 rev.1）

`memory/editor-ui-rework-r5.md` の §1〜§9 が実機確認済み（コミット `5d249cf`）になり、
続けてユーザーから出た **5件の実装項目 ＋ 2件の質問** を扱う回。

**このドキュメントは実装計画。ユーザーの確認後に実装へ入る。**

- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（2917行）、
  `ChartEditorApp.UI.cs`（2248行）、`ChartEditorApp.Commands.cs`（175行）、
  `EditorSettings.cs`（243行）、`PreviewSystem.cs`（481行）、`PreviewClock.cs`（133行）。
- Unity 6000.5.6f1。参照元は `memory/reference/MikuMikuWorld-master/`（C++ / Dear ImGui / OpenGL）。
- **今回は §4（音源が鳴らない）に原因を特定済みの明確な答えがある**。先に §0 を読むこと。

---

## 0. 着手前に共有すべき調査結果

### 0.1 【確定】`source/output.ogg` が鳴らないのは **Opus** だから

ファイルの先頭を実際に読んだ結果:

```
00000010: 82b4 0000 0000 cc1c ec33 0113 4f70 7573  .........3..Opus
00000020: 4865 6164 0102 3801 80bb 0000 0000 004f  Head..8........O
...
00000050: 6167 730d 0000 004c 6176 6636 322e 3132  ags....Lavf62.12
```

- `ffprobe` の結果も `codec_name=opus` / 48000Hz / 2ch / 153.0秒。
- 拡張子は `.ogg` だが、中身は **Ogg コンテナに入った Opus**。`Lavf62`/`Lavc62` とあるので
  ffmpeg で作られたもので、**近年の ffmpeg は `output.ogg` に対して既定で libopus を選ぶ**
  （昔は libvorbis だった）。ここが落とし穴。
- **Unity は Opus をデコードできない。**`PreviewSystem.LoadAudioCoroutine`（`:224`）は
  `AudioType.OGGVORBIS` 決め打ちで `UnityWebRequestMultimedia.GetAudioClip` を呼ぶが、
  そもそも Unity のサポート形式は MP3 / Ogg **Vorbis** / WAV / AIFF などで Opus は含まれない。

**対処（ユーザー側）**: Vorbis で再エンコードする。

```bash
ffmpeg -i source/output.ogg -c:a libvorbis -q:a 6 source/output_vorbis.ogg
```

**対処（実装側）**: §4.2 の診断表示で「Opus は非対応」と名指しで出す（ヘッダ先頭を読めば
`OpusHead` / `\x01vorbis` で判別できる）。同じ罠を二度踏まないために必要。

### 0.2 音源が鳴らない原因は他にも3つ重なりうる（すべて無言で失敗する）

| # | 条件 | 現在の挙動 | 出典 |
|---|---|---|---|
| 1 | 音源は **`song.muses` と同じフォルダ**に無いと絶対に読まれない | `Path.Combine(audioDir, song.audio)`、`audioDir` は `Path.GetDirectoryName(songPath)` | `PreviewSystem.cs:209`、`ChartEditorApp.cs:450` |
| 2 | `@AUDIO`（`song.audio`）が空だと **即 return**（何も起きない） | `if (string.IsNullOrEmpty(song.audio) ...) return;` | `PreviewSystem.cs:208` |
| 3 | 読み込み失敗は `Debug.LogWarning` のみ。**UI には一切出ない** | `Debug.LogWarning($"...音源の読み込みに失敗...")` | `PreviewSystem.cs:230` |

**現状 `source/` には `output.ogg` しか無く `song.muses` が無い**（実測）。仮に Vorbis に
変換しても、譜面をそのフォルダに置いていなければ #1 で読まれない。

### 0.3 【バグ】失敗しても `lastLoadedAudioPath` をキャッシュするので二度と再試行されない

```csharp
if (path == lastLoadedAudioPath) return;
lastLoadedAudioPath = path;          // ← 成否に関わらずここで確定してしまう
if (!File.Exists(path)) { musicSource.clip = null; return; }
```
（`PreviewSystem.cs:210-216`）

- ファイルが無い状態で1回試すと、**同じパスに後からファイルを置いても読み込まれない**。
- デコード失敗（＝今回の Opus）も同様に1回きりで、以降 `Rebuild` が何度走っても再試行しない。
- つまりユーザーが「ファイルを正しい場所に置き直した」あとにエディタを再起動していなければ、
  直っているのに鳴らないままになる。**実バグなので §4.1 で直す。**

### 0.4 SE は「無い」のではなく「実行時合成のクリック音」が全種共通で鳴っている

`seClip = BuildClickClip(1200f)`（`PreviewSystem.cs:126`、実体は `:465-478`）。
`PlayNoteSe`（`:341-355`）は**ノーツ種別を一切見ずに**この1音を鳴らしている
（先頭点=音量0.6、Slide中継点=0.25、メトロノーム=0.4）。
→ 「SEを導入したい」は **音源ファイルを差し込む口が無い** という話であって、
再生機構（`PlayScheduled` のプール、先読み `AudioLookAheadSec=0.1`）は既にある。§5 はその口を作る。

### 0.5 参照元の音量・SE の作り（§4.3 / §5 の下敷き）

| 調べたこと | 参照元の実態 | 出典 |
|---|---|---|
| 音量 | **master / BGM / SE の3段**。`ma_engine_set_volume` / `bgmGroup` / `seGroup` の3レイヤ。既定 0.8 / 1.0 / 1.0 | `AudioManager.cpp:195-208`、`ScoreEditor.cpp:37-48` |
| SE の置き場 | **実行ファイルの隣** `res/sound/*.mp3`（アプリ内に埋め込まない） | `AudioManager.cpp:61`、`:264` |
| SE の種類 | perfect / great / good / flick / connect / tick / critical_tap / critical_flick / critical_connect / critical_tick の10種 | `AudioManager.cpp:65-74` |
| ロングノーツ | `pushAudioEvent(SE_CONNECT, start, end, loop=true)` で**区間ループ再生** | `ScoreEditor.cpp:454`、`:481` |

muses の Slide 中継点は `comboTimes` で離散的に鳴らしている（ループではない）ので、
そこは参照元と設計が違う。**今回はループ再生は導入しない**（現行方式のまま素材だけ差し替える）。

### 0.6 幅変更のショートカットは参照元に無い（muses 独自）

`Application.cpp:145-203` のキー処理に幅の増減は無い。参照元の既定幅は
`Note Width` プロパティ（`EditorWindows.cpp:47`、1〜12 の IntProperty）＝ muses の
ツールバー「幅」欄と同じ位置づけで、キーからは触れない。**§1 は全面的に muses 独自設計。**

一方、**カーソル中央追従は参照元の既定挙動**で、実装も1行:

```cpp
int ScoreEditor::laneFromCenterPos(int lane, int width)
{
    return std::clamp(lane - (width / 2), MIN_LANE, MAX_LANE - width + 1);
}
```
（`ScoreEditor.cpp:229-232`、呼び出しは `:1028`・`:1044` のゴースト生成）
→ §2 はこれの移植。muses は `cellF` が float なので整数除算ではなくスナップで丸める。

---

## 1. ノーツ幅の拡大・縮小ショートカット

### 1.1 現状

- 既定幅は `defaultWidthCells`（`ChartEditorApp.cs:119`、初期値 1.0）。ツールバーの「幅」
  FloatField からのみ変更できる（`ChartEditorApp.UI.cs:317-323`）。
- 既存ノーツの幅はマウスの**端ドラッグ**（r5 までの §7.4-D、`ChartEditorApp.cs:2148-2168`）と
  インスペクタの数値入力でしか変えられない。キーからは触れない。
- 端ドラッグの下限は `0.1f`、上限は `Cells - cellF`。

### 1.2 有効になる対象と優先順位

ユーザー指定は「ノーツ入力モード」と「選択中の点」の2つ。両方が成立しうるので順序を決める:

```
1. selection.Count > 0            → 選択中の全点の width を変える（§1.4）
2. currentTool が配置ツール        → defaultWidthCells を変える（§1.3）
   (Tap / ExTap / Slide / Flick)
3. それ以外（選択ツールで未選択等） → 何もしない
```

**選択が優先**。配置ツール中でも点を選んでいれば選択側が動く（見えているものが動くほうが自然）。
選択を解除すればゴーストの幅を調整できる。

### 1.3 配置ツール中（既定幅の変更）

- `defaultWidthCells` を **±step** する。`step` は現行のセル刻みの規則に合わせる:
  - Slide ツール → **0.5**（`SnapCellTo(rawCell, 0.5f)`、`ChartEditorApp.cs:1464` 等）
  - Tap / ExTap / Flick → **1.0**（同 `:1456`）
- クランプは `[minWidth, Cells]`。**`minWidth` は step と同値**（Slide 0.5 / 単発 1.0、§9 Q1 で確定）。
  ユーザー指定の「0を除く」＝「幅0にはしない」＝「1段階は必ず残す」。
- ツールバーの「幅」FloatField を `SetValueWithoutNotify` で追従させる（表示が食い違わないように）。
- **Undo は積まない**（`defaultWidthCells` は譜面データではなくエディタの状態。既存の
  「幅」欄の編集も Undo 対象外）。

### 1.4 選択中（幅変更、中継点を含む）

`selection` は `PointRef { note, index }` のリストなので、**Slide の中継点も自然に対象になる**
（ユーザー指定の「中継点含む」はこれで満たされる）。

**「初期の選択位置が中央になるように拡大する」の実装**:

素直に「毎回いまの中心から広げる」と、スナップの丸めが毎回入って **押すたびに中心がずれていく**。
そこで **選択したときの中心を記憶しておき、常にそこを基準に左端を決める**:

```csharp
// 選択が変わった時点で1回だけ作る（resizeOriginByRef と同じパターン）
// key: PointRef, value: そのときの中心 = cellF + width/2
private Dictionary<NoteRef, float> widthAnchorCenter;

// 1ステップぶんの適用（各点ごと）
float newWidth = Mathf.Clamp(w.width + delta, minWidth, Cells);
float newCellF = SnapCellTo(anchorCenter - newWidth * 0.5f, step);
newCellF = Mathf.Clamp(newCellF, 0f, Cells - newWidth);   // 枠からはみ出さない
```

- **アンカーの破棄タイミング**: 選択の変更（`SetSingleSelection` / `SetMultiSelection` /
  `ClearSelection`）、ドラッグ移動・端ドラッグ・Undo/Redo・貼り付け。
  → `SyncSelectedNoteFromSelection()` に破棄を1行足すのが最も漏れが少ない。
- **クランプは点ごと**。移動（`ResolveCellDelta`）は「点群全体で1回だけクランプ」しているが、
  あちらは**形を保つ**のが目的。幅は点ごとに独立した値なので、片方が壁に当たっても
  他方は伸ばせるほうが期待に合う（端ドラッグ `:2160` も点ごとクランプ）。
- **step の決め方**は移動・端ドラッグと同じ規則を流用:
  `selection.Exists(r => r.note.kind == NoteKind.Slide) ? 0.5f : 1f`（`:2146`）。
  選択に Slide が1つでも混ざれば 0.5 刻み。
- **Undo**: `PushUndo(coalesce: true, "幅を変更")`。連打を1つにまとめる（`UndoCoalesceSec` 内は
  同じスナップショットを使い回す既存挙動、`:614-626`）。
- `dirty = true` でプレビューへ反映。

**中心が半端になる例（意図どおりの挙動）**: 単発ノーツ（step=1）を幅1→2にすると
中心 `cellF+0.5` に対して左端は `SnapCellTo(center-1, 1) = cellF-1` または `cellF`。
`Mathf.Round` の丸め（`SnapCellTo` は `Mathf.Round(x/step)*step`、`:2757`）で片側に寄る。
参照元の整数除算（`lane - width/2`）も同じく片寄るので、これは仕様として許容する。

### 1.5 コマンド・既定キー

`ChartEditorApp.Commands.cs` のコマンドテーブルに **新カテゴリ「ノーツ」** を足す
（既存カテゴリは ファイル / 編集 / ツール / 再生 / カーソル・表示）。

| id | label | 既定キー |
|---|---|---|
| `note.widthGrow` | 幅を広げる | `←` |
| `note.widthShrink` | 幅を狭める | `→` |

- ユーザー指定どおり **拡大＝←、縮小＝→**。`KeyCode.LeftArrow` / `RightArrow` は
  `EditorSettings.DefaultKeyBindings()`（`:152-193`）で**未使用**なので競合しない。
- `enabled` は `() => selection.Count > 0 || IsPlacementTool(currentTool)`。
  無効時はメニューでもグレーアウトされる（コマンドテーブルの既存の仕組み、`Commands.cs:154`）。
- **入力欄での誤爆は自動的に防がれる**: `OnGlobalKeyDown` は修飾キー無しの単キーを
  テキスト入力中は奪わない（`Commands.cs:147-149`）。←→ はカーソル移動としてそのまま効く。
- **実機で確認が要る点**: UI Toolkit は ←→ を `NavigationMoveEvent` としてフォーカス移動に
  使う。r2 §5 で `notesSheet` 上の `NavigationMoveEvent` は潰してあるが、r5 で
  ディスパッチを `uiRoot` へ移したので、**右パネルにフォーカスがある状態で ←→ を押すと
  フォーカスが飛ぶ可能性**がある。`OnGlobalKeyDown` で `evt.PreventDefault()` は既に
  呼んでいる（`Commands.cs:158`）が、Navigation 系は別イベントなので効かないかもしれない。
  効かなければ `uiRoot` 側でも `NavigationMoveEvent` を潰す。

---

## 2. ノーツのカーソル追従を中央にする

### 2.1 何を変えて、何を変えないか（混同注意）

ユーザーの但し書き「譜面データやノーツプレビューの仕様と混同しないよう注意」への回答:

| 層 | 現在 | 変更するか |
|---|---|---|
| **譜面データ** `Waypoint.cellF` | ノーツの**左端**のセル座標（`@NOTE` の書式もこれ） | **変えない** |
| **タイムラインの描画** `SheetLayout.NoteX(layerF, cellF)` / `NoteX(..., cellF + width)` | 左端〜右端 | **変えない** |
| **3Dプレビュー / ゲーム本体** `NoteGeometry` | `cellF` を左端として頂点を作る | **変えない** |
| **マウス位置 → 配置位置の写像** `SnapCellTo(rawCell, step)` | 結果を**左端**として使う | **ここだけ変える** |

つまり「カーソルの下にノーツの**中心**が来る」ようにするだけで、データ形式・シリアライザ・
描画・判定には一切触らない。

### 2.2 実装

参照元 `laneFromCenterPos`（`ScoreEditor.cpp:229-232`）の float 版ヘルパーを1つ足し、
**配置系の `SnapCellTo(rawCell, step)` を全部これに置き換える**。

```csharp
/// <summary>カーソル位置(rawCell)に幅widthのノーツの中心が来るような左端cellFを返す。
/// 参照元 ScoreEditor::laneFromCenterPos(ScoreEditor.cpp:229) の float 版。</summary>
private static float CellFFromCenter(float rawCell, float width, float step) =>
    Mathf.Clamp(SnapCellTo(rawCell - width * 0.5f, step), 0f, Cells - width);
```

置き換える箇所（すべて「ホバー位置から新しく置く点を決める」もの）:

| ファイル:行 | 文脈 | 使う width |
|---|---|---|
| `ChartEditorApp.cs:1456` | ゴースト（Tap/ExTap/Flick） | `defaultWidthCells` |
| `:1464` | ゴースト（Slide の1点目） | `defaultWidthCells` |
| `:1485`, `:1492` | ゴースト（Slide の2点目・帯） | `defaultWidthCells` |
| `:1504` | ゴースト（中継点追加） | `InterpAtTick(...).width` |
| `:1655` | 配置（Tap/ExTap/Flick） | `defaultWidthCells` |
| `:1673` | 配置（中継点追加） | 補間値 |
| `:1729`, `:1738`, `:1743` | 配置（Slide の1点目・2点目） | `defaultWidthCells` |
| `:2047-2048` | `InsertWaypointInto`（右クリックメニューからの中継点挿入） | 補間値 |

**置き換えない箇所**（すべて「差分」を扱っており、中心の概念が無い）:
- ドラッグ移動 `:2147`・`:2189` 付近（`ResolveCellDelta` 経由のデルタ）
- 端ドラッグ `:2146-2168`（掴んだ端を基準にする操作なので中央化すると壊れる）
- 貼り付け `ComputePasteTransform`（`:2534-2555`。アンカーからのデルタ）
- 高さレーンのドラッグ（`layerF` のみを触る）

### 2.3 副作用（改善方向なので許容）

現状は左端をスナップしているため、**セルの中央でノーツが1セルぶん飛ぶ**
（rawCell=5.4→左端5、5.6→左端6）。中央基準にすると幅1のときは
`SnapCellTo(rawCell-0.5, 1)` なので **セル5の上のどこにいても左端は5** になり、
「ホバーしているセルにそのまま置かれる」挙動に変わる。これは改善。

幅が偶数（step=1 で width=2 等）のときは境界がセル中央に来る。これは中央基準である以上
避けられない（参照元も同様）。

### 2.4 設定にするか

**しない**（無条件で中央にする）。参照元も切り替え不可。左端基準に戻したいという話が
出たら `EditorSettings` に1行足せば済む形にはなっている（ヘルパー1関数に閉じているため）。

---

## 3. ショートカットキーの重複を知らせてから選択させる

### 3.1 現状

`BeginCapture`（`ChartEditorApp.UI.cs:1962-1991`）は **無言の後勝ち**:

```csharp
foreach (var b in keyBindings)
{
    if (b.commandId == commandId) continue;
    b.chords.RemoveAll(c => ChordEquals(c, chord));   // ← 黙って外す
}
```

r5 §11.2 Q7 で「後勝ち」と決めた部分。実際に使うと「いつの間にか別のキーが外れている」
ことに気づけない、というのがユーザー指摘。

### 3.2 方針: 確認モーダルを挟む

キャプチャしたキーが**他のコマンドに既にある場合のみ**、適用前に確認を出す。

```
┌ キーの重複 ──────────────────────────────┐
│ ⌘S は既に次のコマンドに割り当てられています:              │
│   ・ファイル > 保存                                      │
│                                                          │
│ 「元に戻す」に割り当てると、上のコマンドからは外されます。 │
│                                                          │
│                        [ 割り当てる ]  [ やめる ]        │
└──────────────────────────────────────────┘
```

- **選択肢は2つだけ**にする（§9 Q3 で確定）。「両方に残す」は**提供しない**:
  `OnGlobalKeyDown`（`Commands.cs:128-160`）は `keyBindings` を線形探索して
  **最初に一致したコマンドだけを実行して return** する。二重登録を許すと
  「押しても片方しか動かない、しかも順番は設定ファイルの並び順」という説明不能な状態になる。
  → どうしても要るなら §9 Q3 で確認する。
- 複数のコマンドが同じ chord を持っている場合（旧設定ファイルの持ち込み等）は全部列挙する。
- 同じコマンド内の重複は今までどおり黙って no-op（`:1982` の `Exists` チェック）。

### 3.3 実装上の注意

1. **モーダルの入れ子**: 設定モーダルは `overlayLayer` の子（`ShowModal`、`UI.cs:1712-1724`）。
   確認モーダルを同じ `overlayLayer` に足せば後から追加したほうが上に来るので、
   既存基盤をそのまま使える。閉じたら `SelectSettingsTab(settingsTabIndex)` で再描画。
2. **【既存の穴】モーダル表示中でもショートカットが素通りする**:
   `OnGlobalKeyDown` はモーダルの有無を見ていない。今も**ファイル参照モーダルを開いた
   状態で Space を押すと再生が始まり、`2` を押すとツールが切り替わる**。
   確認モーダルは Enter/Esc で操作したいので、ここで一緒に直す:

   ```csharp
   // キャプチャ中(capturingCommandId != null)は従来どおり素通りさせる。
   // それ以外でモーダルが出ている間は、譜面側のコマンドを一切発火させない。
   if (capturingCommandId == null && overlayLayer.childCount > 0) return;
   ```
   メニューのポップアップも `overlayLayer` に入る（`UI.cs:371`・`:402`）が、
   Escape でメニューを閉じる処理はこの判定より**手前**にあるので影響しない（`Commands.cs:118-123`）。
3. **静的な重複表示（おまけ）**: ショートカットタブの chip 描画（`BuildShortcutRow`、
   `UI.cs:1926-1937`）で、他コマンドと重複している chord の chip に警告色を付ける。
   重複は本来起きなくなるが、**手で編集した `editor-settings.json` を読み込んだ場合**には
   起こりうるので、気づける口を残す価値がある。低コスト（`keyBindings` の二重ループ1つ）。

---

## 4. 音源（ogg）が鳴らない ＋ 音量バーの追加

### 4.1 「鳴らない」への対処（§0.1〜§0.3 の実装側）

| # | 内容 | 変更箇所 |
|---|---|---|
| a | **失敗をキャッシュしない**。`lastLoadedAudioPath` の更新は**読み込み成功後**に移す。ファイルが無い／デコードに失敗したパスは記録せず、次の `Rebuild` で再試行する | `PreviewSystem.cs:210-217`, `:228-244` |
| b | **読み込み状態を持つ**。`AudioLoadState { None, Loading, Ok, NotFound, DecodeFailed, Unsupported }` ＋ `AudioLoadMessage`（探したフルパス・エラー文）を公開プロパティにする | `PreviewSystem.cs` |
| c | **Opus を名指しで弾く**。読み込み前に先頭16バイトを読み、`OggS`＋`OpusHead` なら `Unsupported` にして「Opus形式は再生できません。Vorbisに変換してください（ffmpeg -c:a libvorbis）」を出す。`GetAudioClip` に投げてから曖昧なエラーを受け取るより確実 | `PreviewSystem.TryLoadAudio` |
| d | **UI に出す**。右パネル「音源」セクションに状態ラベルを1行追加。§0.2 の3つの無言失敗が全部ここに出る | `ChartEditorApp.UI.cs:790` 付近 |
| e | **「再読み込み」ボタン**を音源セクションに置く。`lastLoadedAudioPath = null` にして `Rebuild` を強制する。外でファイルを差し替えたときにエディタを再起動せずに済む | 同上 |
| f | **オフセットの健全性チェック**（§9 Q4 で入れると確定）。`song.offsetSec` が音源長を超えていると `PreviewClock.Seek` が終端へクランプされて無音になる（`PreviewClock.cs:97`）。`ChartValidator` に警告を1件足す | `Chart/ChartValidator.cs` |

**(a) が最重要**。ユーザーが Vorbis に変換してファイルを置き直しても、(a) が無いと
エディタを再起動するまで鳴らないままになり、「変換しても直らない」と誤診する。

**音源セクションの表示例**:

```
▼ 音源
  音源ファイル   [ output.ogg          ] [ … ]  [ 再読み込み ]
  状態           ⚠ Opus形式は再生できません（Vorbisに変換してください）
  オフセット(秒) [ 0.000 ]
  全体音量       ══════●═══  80%
  BGM音量        ═════════●  100%
  SE音量         ═════════●  100%
```

### 4.2 「音源ファイルは song.muses と同じフォルダ」の再確認

これは r4 §9 で決めた仕様で、`PickAudioFile`（`UI.cs:1698-1708`）は別フォルダを選ぶと
警告を出す。**仕様自体は変えない**（譜面フォルダ1つを配れば動く、という配布の都合）。
ただし §4.1(d) の状態表示で「探したフルパス」を必ず出すので、置き場所の間違いは
一目で分かるようになる。

→ **`source/output.ogg` を使うなら、`source/` に `song.muses` と譜面ファイルを置く**
のが正しい形になる。

### 4.3 音量バー（全体 / BGM / SE）

参照元の3段構成（§0.5）をそのまま採る。

**実装方式: AudioMixer は使わず、素の AudioSource の volume を掛ける。**

| バー | 実装 | 対象 |
|---|---|---|
| 全体 | `AudioListener.volume = master` | アプリの全音（グローバル） |
| BGM | `musicSource.volume = bgm` | 曲 |
| SE | 各再生時に `se * 個別の重み` | ノーツSE・メトロノーム |

- **AudioMixer を使わない理由**: グループ分けのためだけに `.mixer` アセットを新設し、
  `SetFloat` の dB 変換（`20*log10`）を挟むことになる。音源が2系統しかない今の構成では
  釣り合わない。`AudioListener.volume` はグローバルだが、**エディタはスタンドアロンの
  単独アプリ**なので副作用が無い。
- **SE の個別の重み**は現行の値をそのまま相対値として残す:
  ノーツ先頭 0.6（`PreviewSystem.cs:352`）/ Slide中継点 0.25（`:354`）/ メトロノーム 0.4（`:374`）。
  → `src.volume = seVolume * 0.6f` のように掛ける。
- **既定値は参照元に合わせて 0.8 / 1.0 / 1.0**（`ScoreEditor.cpp:37-39`）。
- **永続化先は `EditorSettings`**（`masterVolume` / `bgmVolume` / `seVolume` を追加）。
  **`song.muses` には書かない** — これは譜面の属性ではなくエディタの設定。
  音量を変えただけで譜面ファイルが dirty になるのは間違い。
- **置き場所**: ユーザーの言う「音源のタブ」＝ 右パネルの **「音源」Foldout**（タブではなく
  折りたたみセクション。`UI.cs:790` 付近）。設定モーダルの一般タブではなく**こちら**に置く。
  再生しながら耳で合わせるものなので、設定を開き直さずに触れる場所にあるべき。

---

## 5. SE（効果音）の導入

### 5.1 現状の整理（§0.4）

再生の仕組みは既にある。足りないのは **(1) 音源ファイルをどこから読むか** と
**(2) ノーツ種別ごとに鳴らし分ける仕組み** の2つ。

### 5.2 どこに置くか（3案、推奨は案A）

| | **案A: SerializeField で AudioClip 参照（推奨）** | 案B: StreamingAssets | 案C: 実行ファイルの隣（参照元方式） |
|---|---|---|---|
| 置き場 | `Assets/Audio/SE/*.wav` | `Assets/StreamingAssets/se/*.ogg` | `muses.app` の隣の `se/` |
| 読み方 | シーンの参照をそのまま使う（同期・即時） | `UnityWebRequestMultimedia`（非同期） | 同左 |
| ビルド無しの差し替え | **不可** | 可（.app の中身を開く必要あり） | **可（一番簡単）** |
| ゲーム本体（iOS/Android）でも同じ素材を使えるか | **そのまま使える** | 使えるが Android は APK 内なので別経路 | **使えない**（モバイルに「隣」が無い） |
| 追加コード | ほぼゼロ（既存のShader注入と同じパターン） | ロード用コルーチン＋パス解決 | 同左＋アプリディレクトリ解決 |

**案A に確定（§9 Q5）**。理由:
1. **ノーツSEはゲーム本体でも必要**（判定音）。エディタだけ外部フォルダ方式にすると
   同じ素材を2箇所で管理することになる。muses はタブレット（iOS/Android）が本命プラットフォーム
   （[[muses-platform-decisions]]）なので、「実行ファイルの隣」は本体では成立しない。
2. **既存の配線パターンと同じ**。`PreviewSystem` は既にシェーダ3種を
   `ChartEditorApp` から SerializeField 経由で受け取っている（`PreviewSystem.cs:66-72`）。
   `AudioClip` も同じ形で渡せる（`ChartEditor.unity` の `ChartEditorApp` に欄が増えるだけ）。
3. SEの差し替え頻度は「作業中に何度も」ではなく「一度決めたら固定」なので、
   ビルドが要ることのコストが低い。

**案Aで作っておけば、後から「同名ファイルが外部フォルダにあれば上書き」（案C 相当）を足すのは
容易**（読み込み経路が `AudioClip` 1個に収束しているため）。ビルド無しの差し替えが要ると
分かった時点で追加する。

### 5.3 SE の種類

参照元は10種（§0.5）だが、muses のノーツ種別と現行のプレビュー用途に絞る:

| 用途 | 現状 | 追加するクリップ | フォールバック |
|---|---|---|---|
| Tap | 合成クリック 0.6 | `seTap` | 合成クリック |
| Ex Tap | 同上 | `seExTap` | `seTap` → 合成クリック |
| Slide 始点 | 同上 | `seSlide` | `seTap` → 合成クリック |
| Flick | 同上 | `seFlick` | `seTap` → 合成クリック |
| Slide 中継点（`comboTimes`） | 合成クリック 0.25 | `seTick` | 合成クリック |
| メトロノーム | 合成クリック 0.4 | `seMetronome` | 合成クリック |

- **フォールバックは必須**。未設定のクリップがあっても音が消えないようにする
  （素材が揃う前でも今までどおり動く）。
- `PlayNoteSe`（`PreviewSystem.cs:341-355`）は現在ノーツ種別を見ていないので、
  `note.kind` で clip を選ぶよう拡張する。Slide の中継点は `seTick`。
- **Unity のインポート設定**: 短いSEなので `Load Type = Decompress On Load`、
  `Preload Audio Data = ON`、`Compression Format = PCM`（または ADPCM）。
  ここを既定のままにすると**初回再生時に解凍が走って先頭が欠ける**ことがある。
- 素材のフォーマットは **.wav（16bit / 44.1kHz または 48kHz）** を推奨。
  Unity が確実に読め、短い音でループ点の心配が無い。**素材の用意はユーザー側の作業**。

### 5.4 ゲーム本体側

`Muses.Audio.SongClock` にも同じ合成クリック（`BuildClickClip`）がある。
**今回はエディタのみを対象にする**（§9 Q6 で確定。ゲーム側の調整は別途行う予定のため）。
§5.2 案A なら `Assets/Audio/SE/` の同じアセットをゲーム側からも参照するだけで済むので、
ゲーム側に着手するときの手戻りは無い。

---

## 6. 【質問への回答】自動保存ファイルに上限はあるか

**上限は不要。そもそもファイルが増えない設計。**

| 対象 | 保存先 | 個数 | 出典 |
|---|---|---|---|
| 保存済みの譜面 | `<譜面ファイルのパス>.autosave` | **譜面ごとに1つ、毎回上書き** | `ChartEditorApp.cs:674`, `:677` |
| 未保存の新規譜面 | `persistentDataPath/untitled.muses.autosave` | **固定1つ、毎回上書き** | `:667` |

- **世代管理（`.1` `.2` `.3` …）は無い**。したがって放置しても無限に増えることはない。
  ディスクを圧迫するのは「編集した譜面の数だけ `.autosave` が残る」程度で、
  1ファイルは譜面本体と同じサイズ（テキスト、数十KB）。
- 設定側の上限は **「自動保存の間隔(分)」が 1〜60 にクランプ**されているだけ
  （`ChartEditorApp.UI.cs:1824`）。個数の設定項目は存在しない。
- **`.autosave` は自動削除されない**。正常に保存しても残り続ける。次に同じ譜面を開いたとき、
  `.autosave` のほうが新しければ復元を提案する（`CheckAutosaveRestore`、`:688-696`）。
  **この挙動は今回変更しない**（§9 Q7 で確定。「保存後に消す」対応は見送り、現状維持）。

**世代管理が欲しい場合の案**（今回の実装項目ではない）: `.autosave.1`〜`.autosave.N` を
ローテーションし、N を設定項目にする（既定3程度）。「1つ前ではなく3つ前に戻したい」
という要求が出たときに検討する。

---

## 7. 【質問への回答】プレビューの素材変更はゲーム本体に反映されるか

**結論: 「素材」は反映される。「設定値」は反映されない。情報源が別だから。**

| 変えるもの | 実体 | エディタのプレビュー | ゲーム本体 | 反映されるか |
|---|---|---|---|---|
| ステージのジオメトリ | `Muses.Stage.StageGeometry` / `StageDerive` | 同じクラス | 同じクラス | **される** |
| ステージの色 | `StageColors` の定数 → `ColorFromHex`（`StageGeometry.cs:117`, `:142`） | 同じ | 同じ | **される** |
| ノーツの形・色 | `Muses.Notes.NoteGeometry.Build`（色は `:94-99` にハードコード） | 同じ | 同じ | **される** |
| シェーダ | `Assets/Shaders/*.shader` | シーンの SerializeField 経由（`PreviewSystem.cs:66-72`） | シーンの SerializeField 経由 | **同じ .shader を指す限りされる**（配線は2箇所） |
| **StageConfig の数値**（画角・判定線位置・hiSpeed・farFrac 等） | `StageConfig` | **`StageConfig.Default()` をハードコード**（`PreviewSystem.cs:32`） | **`SampleScene.unity` に保存された別インスタンス**（`StageController.cs:15` の SerializeField） | **されない** |

### 7.1 いま危ないのは StageConfig だけ

- 実測すると `SampleScene.unity:493-509` の値（`phiDeg: 77` / `thetaDeg: 38` / `cells: 12` /
  `readAheadSec: 1.2` / `hiSpeed: 1`）は `StageConfig.Default()` と**現時点では一致している**。
  つまり今は問題が出ていない。
- しかし **Inspector で StageController の cfg を1つでも触ると、その瞬間からプレビューと
  ゲームの見た目がずれる**（プレビューは `Default()` しか見ない）。逆に `Default()` を
  コードで変えてもシーン側の serialized 値は上書きされない。
- **素材やステージ見た目の調整を始める前に、ここを1本化しておくべき**。案:
  - (a) `StageConfig` を **ScriptableObject** にして `Assets/Settings/StageConfig.asset` に置き、
    プレビューとゲームの両方がそれを参照する。**推奨**（Inspector で触れる利点を保ったまま一本化できる）。
  - (b) プレビューもゲームも `StageConfig.Default()` を正とし、シーンの SerializeField をやめる。
    簡単だが Inspector で調整できなくなる。

### 7.2 これから素材（画像・テクスチャ）を入れる場合

現在プロジェクトに画像アセットは1枚も無く、ノーツもステージも**単色ポリゴン**
（`editor-ui-redesign.md` §4.3）。テクスチャを入れると新しく「どこに置くか」の判断が要る:

- `Assets/` 内のアセットを **SerializeField か Resources で両方から参照** → 反映される。
- シーンごとに別のアセットを割り当てる → 反映されない。
- → **§5.2 案A（SerializeField）と同じ判断**。素材は1箇所に置いて両方から参照する。

**この項目は実装対象ではない**（ユーザー明示）。§7.1 の一本化も **今回は触らない**（§9 Q8 で確定）。
ただし **ステージ／ノーツの素材づくりを始める回の冒頭で必ず先に片付ける**。
Inspector で `StageController` の cfg を1つでも触った瞬間にプレビューとゲームがずれ、
「プレビューで合わせたのに本番で違う」という切り分けの難しい状態になるため。

---

## 8. 実装順

独立性と「壊れたときの切り分けやすさ」で並べる。

1. **§4.1** 音源の読み込み修正＋診断表示（(a) の再試行バグが最優先。ユーザーが今詰まっている）
2. **§4.3** 音量バー3種（§4.1 と同じ「音源」セクションを触るのでまとめる）
3. **§2** カーソル中央追従（ヘルパー1関数＋置換9箇所。完全に独立）
4. **§1** 幅ショートカット（§2 の中央基準ヘルパーを使うので §2 の後）
5. **§3** 重複通知（§3.3-2 のモーダル中ディスパッチ停止を含む）
6. **§5** SE（素材が揃ってから。フォールバックがあるので素材ゼロでもコードは先に入れられる）

**順序上の依存**:
- **§1 は §2 の後**。幅を変えたときの中心の扱いが §2 のヘルパーと同じ規則になるので、
  先に §2 を入れておくと §1 が短くなる（逆順だと同じ計算を2回書くことになる）。
- **§5 のコード部分は §4.3 の後**。SE音量の乗算先が §4.3 で決まるため。

**§6・§7 は質問への回答**なので実装項目には含まれない（§7.1 の一本化だけ Q8 で扱う）。

---

## 9. 確認事項

### 9.1 確定済み（2026-08-03、着手前にユーザー確認）

| # | 節 | 決定 |
|---|---|---|
| Q1 | §1.3 | 「（0を除く）」は **幅を0にはしない**の意味。最小幅は step と同値（単発ノーツ=1セル / Slide中継点=0.5セル） |
| Q2 | §1.2 | 配置ツール中に選択がある場合は **選択側を優先** |
| Q3 | §3.2 | 重複時の選択肢は **「割り当てる／やめる」の2つ**。「両方に残す」は提供しない（先に登録されたほうしか動かず説明不能になるため） |
| Q4 | §4.1(f) | `offsetSec` が音源長を超えている場合の警告も **今回入れる**（検証の警告1件） |
| Q5 | §5.2 | SE の置き場は **案A（`Assets/Audio/SE/` に同梱、SerializeField 参照）**。差し替えには再ビルドが必要 |
| Q6 | §5.4 | ゲーム本体（`SongClock` の合成クリック）は **今回は対象外**。ゲーム側の調整は別途行う予定のため、エディタのみ差し替える |
| Q7 | §6 | 正常保存後も `.autosave` は **削除せず残す**（現状維持）。世代管理を追加する話ではなく、単に「保存後に消す」対応を見送るだけ |
| Q8 | §7.1 | `StageConfig` の一本化は **今回やらない**。ただし**ステージ／ノーツの素材づくりを始める前には必ず片付ける**（Inspector を触った瞬間にプレビューとゲームがずれるため） |

### 9.1 実装後の実機確認で特に見てほしい点

- **§4**: Vorbis に変換した ogg を `song.muses` と同じフォルダに置いて再生し、**音が鳴ること**。
  わざと存在しないファイル名を入れて「見つかりません＋探したフルパス」が出ること。
  Opus のままのファイルを指定して「Opusは非対応」と出ること。
  **ファイルを置き直したあと「再読み込み」だけで鳴ること**（エディタの再起動が要らないこと）。
- **§4.3**: 3本のスライダーがそれぞれ独立して効くこと（全体を0にすると SE も無音になること）。
  エディタを再起動しても音量が保持されていること。
- **§2**: 配置ツールのゴーストが**カーソルの下に中心**で出ること。ゴーストの位置と
  クリック後に実際に置かれる位置が**完全に一致**すること（ここがずれるのが最も起きやすい回帰）。
  盤面の左端・右端でゴーストが枠からはみ出さないこと。
- **§1**: 選択して ← を連打したとき、**中心が横に流れていかない**こと。
  端に寄せたノーツで ← を押しても枠外へ出ないこと。
  右パネルの入力欄を編集中に ←→ を押して、**幅ではなくテキストのカーソルが動く**こと。
  右パネルにフォーカスがある状態で ←→ を押してフォーカスが飛ばないこと（§1.5 の懸念）。
- **§3**: 既に使われているキーを登録しようとして確認が出ること。「やめる」を選ぶと
  **元の割り当てが両方とも変わっていない**こと。
  ファイル参照モーダルを開いた状態で Space / 数字キーを押しても譜面側が反応しないこと（§3.3-2）。

---

## 関連

- `memory/editor-ui-rework-r5.md` — 前段（設定画面・コマンドテーブル・自前メニュー）。実機確認済み。
  本書 §1・§3 はそこで作った `EditorCommand` / `KeyBinding` / 設定モーダルの上に乗る。
- `memory/editor-ui-rework-r4.md` — §9「音源は song.muses と同じフォルダ」、
  §12「`@OFFSET` の符号」が本書 §4 の前提。**§12 は音源が用意できていないため検証延期中だったが、
  §4 で音が鳴るようになれば検証できる**（r6 完了後の宿題）。
- `memory/editor-ui-redesign.md` — 差分カタログ。§2.5 の「音源セクション: 音量3種はそもそも
  機能自体が未実装」が本書 §4.3 の出発点。§4.3「アイコン素材」が本書 §7.2 の背景。
- `memory/editor-spec.md` — Phase 4 機能仕様。§5.1 が `PreviewClock`。
- `memory/note-spec.md` — `cellF` が左端であること（本書 §2.1 の「変えない」列の根拠）。
- `memory/reference/MikuMikuWorld-master/` — 参照元。本書 §0 の表に出典行を全て記載。
- [[muses-platform-decisions]] — タブレット本命という判断が本書 §5.2 案A の根拠。
- [[feedback-editor-ui-polish-deferred]] — 見た目の指摘を溜めてまとめて対応する方針。
