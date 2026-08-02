# 譜面エディタ UI 改修設計 第4弾（実機フィードバック、2026-08-02 rev.1）

`memory/editor-ui-rework-r3.md` の §1〜§8 を実装したビルド（コミット `e97e70a` / `dd14956`）を
ユーザーが実機確認した結果の指摘 **11 件**への設計。r3 の指摘（S1/S2/S4/S6・P1〜P4）は
**すべて問題なしと確認済み**で、今回は新規の指摘のみ。

**このドキュメントは実装計画。ユーザーの確認後に実装へ入る。**

- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（2706行）、
  `ChartEditorApp.UI.cs`（1721行）、`PreviewSystem.cs`（441行）、
  `unity/Assets/UI/ChartEditor/ChartEditorRoot.uxml`（81行）/ `.uss`（454行）。
- 今回は**エディタ外へ波及する項目が3つある**: §3（`Notes/NoteGeometry.cs`・`NoteView.cs`）、
  §8（`ChartEditorApp/PreviewSystem.cs` ＋ `Stage/StageController.cs` の前提）、
  §10（`Chart/SongMeta.cs` の `SongAddr` ＝ **譜面ファイル形式の意味論**）。
- Unity 6000.5.6f1。参照元は `memory/reference/MikuMikuWorld-master/`（C++/ImGui/OpenGL）。

---

## 0. 指摘一覧と対応

| # | 指摘 | 節 | 影響範囲 | 原因の特定 |
|---|---|---|---|---|
| 1 | Slide 置きかけの UI がおかしい（始点が映らない／終点まで一直線に描画されない） | §1 | 描画のみ | **特定済**（バウンディングボックスを塗っている） |
| 2 | タイムライン上のノーツが上部メニューにはみ出る | §2 | USS 1行 | **特定済**（`overflow` 未指定） |
| 3 | プレビューの小節線がタイムラインと合っていない | §3 | ゲーム側へ波及 | **特定済**（`cfg.bpm` 固定・4/4 決め打ち） |
| 4 | Slide の点も端ドラッグで幅変更したい | §4 | 入力 | 機能追加（r-mmw §5.2 で前提が解消済み） |
| 5 | イベント3種を高さレーンと同じ要領で非表示にしたい | §5 | レイアウト | 機能追加 |
| 6 | イベントが常に入力中なのが違和感。ノーツ追加と同じ入力モードにしたい | §6 | 入力 | 機能追加（**参照元は最初からモード方式**） |
| 7 | テキスト入力が見えない（値は反映されるが文字とカーソルが映らない） | §7 | USS | **仮説あり**（実機で1分で切り分け可） |
| 8 | プレビュー切り替え時、ステージが一瞬動く（広がる） | §8 | プレビュー | **特定済**（`cam.aspect` が画面アスペクトへ戻る） |
| 9 | 音源ファイルをクリックで Finder を開けないか | §9 | 新規UI | 方式の選択が要る（**要確認**） |
| 10 | アウフタクト用に常に 0 小節目から | §10 | **データ意味論** | 参照元と同じ 0 始まりへ |
| 11 | ステータスバーの tick 表示を4桁固定に | §11 | 表示のみ | **特定済**（桁数変動でシークバーが動く） |

**§10 だけが譜面ファイルの意味（`bar` の基点）に触る**ので、r3 §5 と同じく単独コミットにする（§12）。

---

## 1. Slide 置きかけのプレビュー（指摘1）

### 1.1 現状と原因

`DrawPlacementGhost` の Slide ブロック（`ChartEditorApp.cs:1276-1301`）で、始点と
カーソルを繋ぐ帯を**軸並行のバウンディングボックス1枚**として塗っている。

```csharp
// ChartEditorApp.cs:1292-1296
float y0 = L.TickToY(wp0.tick), y1 = L.TickToY(tick);
float x0 = L.NoteX(wp0.layerF, wp0.cellF + wp0.width * 0.5f, previewForceSky);
float x1 = L.NoteX(layerF, cellF + defaultWidthCells * 0.5f, previewForceSky);
FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1) - 2, Mathf.Min(y0, y1),
                            Mathf.Max(x0, x1) + 2, Mathf.Max(y0, y1)), lineCol);
```

変数名が `lineCol` であるとおり**線を引くつもりの式**だが、`Rect.MinMaxRect(minX, minY, maxX, maxY)`
は始点と終点を対角とする**矩形**になる。始点と終点の cellF が違うと `|x1 - x0|` がそのまま矩形の
幅になり、添付スクリーンショットの「巨大な緑の塗り」になる。x0 == x1（真下）のときだけ
幅 4px の縦線に見えるので、これまで気づかれなかった。

**「始点が映らない」**は別の原因。置きかけの始点は

```csharp
// ChartEditorApp.cs:1123-1129
float px = L.NoteX(wp0.layerF, wp0.cellF, forceSky: false);
FillRectOutline(p, new Rect(px - 5, py - 5, 10, 10), Color.white);
```

と **10×10px の白い枠**でしか描かれない。しかも
- 基準が `cellF`（帯の左端）なのに対し、上の帯は `cellF + width/2`（中心）を使っており**位置がずれる**
  （スクリーンショットで白い四角が緑の矩形の左外に飛び出しているのはこれ）
- 描画順が `DrawPlacementGhost` より**前**（`:1123` → `:1131`）なので、帯に上書きされる

の2点で「ノーツを置いた」ようには読めない。

### 1.2 参照元

`drawDummyHold`（`TimelineNotes.cpp:217-229`）:

```cpp
if (insertingHold) {
    drawHoldCurve(dummyStart, dummyEnd, EaseType::None, renderer, noteTint);
    drawNote(dummyStart, renderer, noteTint);
    drawNote(dummyEnd, renderer, noteTint);
} else {
    drawNote(dummyStart, renderer, hoverTint);
}
```

**本物の hold と同じ `drawHoldCurve`（帯）を描き、始点・終点は `drawNote`（＝通常ノーツと同じ描画関数）
で描く**。ユーザーの言う「始点が映らない／一直線に描画されない」は、参照元では
そもそもこの形になっている。

（なお参照元の hold 入力は**ドラッグ**方式＝押した位置が始点・離した位置が終点。
muses は2クリック方式。ここは変更しない — 2クリックのほうがタッチ環境で扱いやすく、
`pendingSlideStart` を中心に既存実装が組み上がっているため。）

### 1.3 方針

`DrawPlacementGhost` の Slide ブロックを、確定後の描画（`GenerateNotesSheet` の
Slide 分岐、`:1038-1075`）と同じ語彙で組み直す。

| 要素 | 現状 | 変更後 |
|---|---|---|
| 始点 | 10×10px の白い枠（`cellF` 基準・帯より前に描画） | **`DrawGhostPoint` で幅いっぱいの帯**（`cellF`〜`cellF+width`、`DrawEndpointGlyph` と同じ求め方） |
| 始点→終点 | バウンディングボックス | **`FillQuad` で四隅を結ぶ帯**（左端の2点・右端の2点） |
| 終点 | `DrawGhostPoint`（現状のまま） | 変更なし |
| 描画順 | 始点 → 帯 → 終点 | **帯 → 始点 → 終点**（端点が帯の上に出る、参照元と同じ） |

- 帯の四隅は `NoteX(layerF, cellF, forceSky)` と `NoteX(layerF, cellF + width, forceSky)` で求める。
  始点・終点とも同じ `width`（＝`defaultWidthCells`）なので `FillQuad`（`:849`）1枚で足りる。
  easing は未確定（配置時は必ず `Linear`、`NewWaypoint:2640`）なので分割は不要。
- **マウスがシート外にいるときも始点は出し続ける**という現在の意図（`:1121-1122` のコメント）は維持する。
  `DrawPlacementGhost` は `sheetHoverPos` が無いと早期 return するので、
  「始点だけを描くブロック」は今までどおり別に残し、**呼び出し位置を `DrawPlacementGhost` の後ろへ移す**。
- `previewForceSky`（始点とホバー位置で層が違えば Sky ペインのみ）の判定は現状どおり維持。

### 1.4 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1121-1131` | 置きかけ始点ブロックを `DrawPlacementGhost` の後ろへ移し、帯グリフ描画に差し替え |
| `ChartEditorApp.cs:1286-1298` | バウンディングボックス → `FillQuad` による帯 |

---

## 2. ノーツ・小節番号が上部バンドへはみ出る（指摘2）

### 2.1 原因

`notesSheet` は `overflow` を指定していない（`ChartEditorApp.UI.cs:369-371`、`.uss` にも規則なし）。
UI Toolkit の `overflow` 既定値は `visible` で、**`generateVisualContent` が描いたメッシュも、
`position:absolute` の子 Label も要素の矩形からはみ出して描かれる**。

はみ出す量は描画側の culling 余裕そのままで、

- ノーツ・帯・端点: `rect.y - 8` まで描く（`:1016-1017`, `:881`, `:1087`）
- 小節番号ラベル: `y - 7f` に置き、`y >= rect.y - 4f` まで許容 → 上端から最大 11px + 行高
- イベントチップ: `top = y - 8f`、`y >= rect.y - 8f` まで許容 → 上端から最大 16px + 行高

一方 `notesSheet` の親（`.canvas-host` → `.tabs-host` → `.main-row` → `.root`）にも
`overflow` 規則が無いため、**どこでもクリップされずタブ見出し・ツールバー・メニューバーの上に
重なる**。バンド類は UXML 上で `main-row` より前にあり先に描かれるので、後から描かれる
ノーツシートの内容が上に乗る。

参照元は OpenGL のフレームバッファへ描いてから 1 枚の画像として貼る
（`ScoreEditor.cpp:1017-1020` の `framebuffer` → `drawList->AddImage`）ため、
構造的にはみ出しようがない。

### 2.2 方針

`notesSheet` に `overflow: hidden` を付ける。**1行で済み、他に影響しない。**

- `previewSurface` にも予防的に付ける（現状 `backgroundImage` しか無いのではみ出さないが、
  §3/§8 で Painter2D の重ね描きが増えるため）。
- USS 側（`.uss` に `.notes-sheet { overflow: hidden; }` を足してクラスを付与）でも
  C# 側（`notesSheet.style.overflow = Overflow.Hidden`）でもよいが、
  **見た目に関する指定は USS に寄せる**という既存方針（`.uss` 冒頭のコメント）に従い USS にする。

### 2.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorRoot.uss` | `.notes-sheet` / `.preview-surface` クラスを追加（`overflow: hidden` ＋ `flex-grow: 1`） |
| `ChartEditorApp.UI.cs:369-393` | 生成時に `AddToClassList` し、`style.flexGrow` の直書きを USS へ移す |

---

## 3. プレビューの小節線がタイムラインと合わない（指摘3）

### 3.1 原因

プレビューのビートライン（3D の横線）は `NoteGeometry.Build` の末尾で生成している。

```csharp
// Notes/NoteGeometry.cs:148-164
var beatTimeline = TimelineFor(0);
float b = 60f / cfg.bpm;                       // ← StageConfig.bpm（固定値）
float last = ...;
for (float t = 0; t < last + 4f; t += b * 4f)  // ← 4拍 = 1小節 決め打ち
```

**譜面の BPM（`song.bpmEvents`）でも拍子（`song.meters`）でもなく、`StageConfig.bpm` から
「4拍ごと」に引いている。**一方エディタのタイムラインは `SongAddr.ToAddr(song.meters, t)` で
tick から小節頭を判定している（`ChartEditorApp.cs:953-954`）。両者は別の情報源なので、
以下のいずれかがあれば必ずずれる。

| ずれる条件 | 具体例 |
|---|---|
| 譜面の BPM ≠ `cfg.bpm` | `StageConfig.Default().bpm = 150`（`StageConfig.cs:139`、`memory/settings.json` 由来）。**「新規」直後は `song.bpmEvents` が空**で、`BuildTickToSeconds` の既定 120 BPM が使われる（`ChartFormat.cs:51-52`）→ **1.25 倍ずれる** |
| 拍子が 4/4 でない | `b * 4f` 決め打ちなので 3/4・6/8 では合わない |
| BPM 変化がある | 定間隔なので変化後は累積的にずれる |

「新規譜面 → プレビュー」で必ず 1.25 倍ずれるので、ユーザーが最初に気づくのはここのはず。

### 3.2 方針

**エディタのタイムラインとまったく同じ情報源（`song.meters` ＋ `chart.bpmEvents`）から
小節線の時刻を作り、`NoteGeometry` へ渡す。**同じ情報源にする以上、原理的にずれ得なくなる。

```csharp
// 呼び出し側（PreviewSystem.Rebuild）で組む
var tickToSec = ChartFormat.BuildTickToSeconds(chart.bpmEvents);
var barTimes = new List<float>();
for (int bar = FirstBar; ; bar++) {
    int tick = SongAddr.ToTick(song.meters, bar, 1, 0);
    float t = tickToSec(tick);
    if (t > chartEnd + 4f) break;
    barTimes.Add(t);
}
```

- `NoteView.Build` / `NoteGeometry.Build` に `List<float> barTimes = null` を足し、
  **null なら従来どおり `cfg.bpm` の4拍間隔**にフォールバックする（`GameController` 側は
  デモ譜面が単一 BPM・4/4 なので回帰が出ない。将来ゲーム本体が `song.muses` を読むように
  なったら同じ値を渡す）。
- 描くのは**小節線のみ**（現状も実質そう）。拍線も出すとなると
  `beatMesh` に色/濃さの頂点属性が要る（現在 `beatPositions`/`beatNear`/`beatLayerF` のみで
  色属性が無い）ので、**今回は小節線だけに絞る**。
- グループ 0 の `XAt` に乗せる簡略化（ソフラン非対応）は現状維持。

### 3.3 付随して見つかった問題（今回は直さない）

**`song.offsetSec`（音源先頭 → 譜面 tick0 のズレ）がプレビューで一切適用されていない。**
`ChartSerializer` で読み書きし、右パネルで編集もできる（`ChartEditorApp.UI.cs:616`）が、
`PreviewClock.SongTime` は `AudioSource.time` をそのまま返す（`PreviewClock.cs:34-38`）だけで、
`offsetSec` を参照している箇所がプロジェクト全体に存在しない
（`grep offsetSec` のヒットはシリアライザとUIのみ）。

つまり**音源とノーツの頭出しが常に「音源0秒 = tick 0」に固定**されている。
§10（アウフタクト）と目的が近い項目なので、**次の増分で扱うか、§10 と同時にやるかを確認したい**（§13 Q6）。

### 3.4 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `Notes/NoteGeometry.cs:42, 148-164` | 引数に `List<float> barTimes` を追加。null なら従来動作 |
| `Notes/NoteView.cs:74` | 同上を素通し |
| `ChartEditorApp/PreviewSystem.cs:147-174` | `song.meters` ＋ `chart.bpmEvents` から `barTimes` を組んで渡す |

---

## 4. Slide の点も端ドラッグで幅変更（指摘4）

### 4.1 現状と、制約が消えていること

端ドラッグでの幅変更（editor-ui-redesign.md §7.4-D）は**単発ノーツ限定**になっている。

```csharp
// ChartEditorApp.cs:1859-1861
private static int EdgeGrabSign(SheetLayout L, Note note, Vector2 pos)
{
    if (note.points.Count != 1) return 0;   // ← Slide を弾く
```

当時の理由は editor-ui-redesign.md §7.4 の実装ログにあるとおり
「Slide は `width` が中継点ごとの値で、**帯のどの区間を掴んだかで対象の中継点が変わる**ため、
素直な『左右端をつかむ』操作に落ちない」だった。

**この前提は editor-ui-rework-mmw.md §5.2（選択の点単位化）で既に消えている。**
現在ヒットテストは `HitTestPoint`（点のみ、`:2589-2607`）で、掴んだ点が `NoteRef` として
一意に決まる。帯はもう選択・ドラッグの対象ではない。したがって
**「掴んだ点の左右端 ±4px」が曖昧になる余地は無い。**

### 4.2 参照元

`updateNote`（`TimelineNotes.cpp:14-157`）は hold の **start / end / mid すべてに対して**
同じ関数で L（左端リサイズ）/ M（移動）/ R（右端リサイズ）の3ボタンを置く
（`ScoreEditor.cpp:892-913` が start・end・各 mid に `updateNote` を呼ぶ）。
つまり参照元では中継点の幅変更が最初から可能。

さらに参照元の L/R は **`for (int id : selectedNotes)` と選択中の全ノーツに同じ差分を適用**する
（`TimelineNotes.cpp:47-58`）。muses の移動ドラッグも既に `foreach (var r in selection)` で
全選択点へ同じ差分を適用している（`ChartEditorApp.cs:1973-1981`）ので、
**幅変更だけ「掴んだ1点のみ」なのは非対称**。→ §13 Q4 で確認したい。

### 4.3 方針

幅変更の状態を「ノーツ」から「点」へ下げる。移動ドラッグを `NoteRef` 化したのと同じ変更。

| 現在 | 変更後 |
|---|---|
| `Note resizingNote` | `NoteRef? resizingRef` |
| `Waypoint resizeOriginWp` | `Dictionary<NoteRef, Waypoint> resizeOriginByRef`（全選択点へ適用する場合） |
| `EdgeGrabSign(L, Note, pos)` | `EdgeGrabSign(L, NoteRef, pos)`（その点の矩形と `forceSky` で判定） |
| `resizingNote.points[0] = wp` | `resizingRef.note.points[resizingRef.index] = wp` |

- `OnSheetPointerDown` の Select ケース（`:1556`）から `hn.note.points.Count == 1` の条件を外す。
- **セルのスナップ刻みを揃える**: 現在は常に `SnapCellTo(rawCellR, 1f)`（`:1923`）だが、
  Slide の点は移動ドラッグと同じ 0.5 刻み（`:1960` の `cellStep`）にする。
- 最小幅 0.1 セルのクランプ（`:1927`, `:1932`）と、`cellF ∈ [0, Cells]` のクランプは維持。
- 端ドラッグと easing 巡回の衝突: easing 巡回は Slide ツールでのみ（`:1508`）、
  端ドラッグは Select ツールでのみ（`:1557`）なので**排他。現状のまま衝突しない**。

### 4.4 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:272-276` | 幅変更の状態を `NoteRef` ベースへ |
| `ChartEditorApp.cs:1556-1567` | 単発ノーツ限定の条件を外す |
| `ChartEditorApp.cs:1859-1870` | `EdgeGrabSign` を点単位へ |
| `ChartEditorApp.cs:1920-1940` | 書き戻し先とスナップ刻み |
| `ChartEditorApp.cs:174-199, 324` | `resizingNote` を参照している選択クリア・ドラッグ判定を追随 |

---

## 5. イベントレーンの表示トグル（指摘5）

### 5.1 現状

右余白（`sheetMarginRight = 104f`、`ChartEditorApp.cs:106`）は**常に確保**され、
`EventColumns`（`:784-791`）が BPM / 拍子 / ソフランの3列に3等分している。
高さレーンは既に折りたたみを持っている（`showHeightLane` / `HeightLaneW`、`:293-295`）。

### 5.2 方針

高さレーンと**まったく同じ形**にする。

```csharp
private bool showEventLane = true;                       // 既定は表示（現状維持）
private float EventLaneW => showEventLane ? sheetMarginRight : 0f;
```

- `CurrentSheetLayout()`（`:771-776`）が `sheetMarginRight` の代わりに `EventLaneW` を渡す。
  `SheetLayout` は `rightMargin.width == 0` でも破綻しない（`EventColumns` の各列が幅0になり、
  `rightMargin.Contains(pos)` が常に false になるだけ）。
- トグルの置き場も高さレーンと揃える: **「表示」メニュー**（`ChartEditorApp.UI.cs:205-222`）と
  **右パネル「表示設定」**（`:621-632`）の両方。
- `UpdateEventChips`（`UI.cs:479`）は幅0のとき早期 return する（チップ Label が幅0で残らないように）。
- 畳んだぶん（104px）は Ground/Sky ペインへ回る。セル幅がウィンドウ幅1290pxで
  約 46.5px → 51px に広がる（editor-ui-redesign.md §7.2 の試算と同じ計算）。

**種別ごとに3つのトグルにするか、まとめて1つか**は §13 Q2 で確認したい。
1つを推す理由: 高さレーンと形が揃う／3列は等分割なので個別に消すと列位置が動いて
「どの列が何か」がクリックのたびに変わる（列＝種別という §7.3 の前提が崩れる）。

### 5.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:102-106, 771-776` | `showEventLane` / `EventLaneW` の追加と `SheetLayout` への反映 |
| `ChartEditorApp.UI.cs:205-222, 621-632` | メニュー項目と表示設定トグル |
| `ChartEditorApp.UI.cs:479-482` | 幅0なら早期 return |

---

## 6. イベントの入力モード化（指摘6）

### 6.1 現状と問題

イベントレーンのクリックは**現在のツールに関わらず**常に「追加」として扱われる。

```csharp
// ChartEditorApp.cs:1406-1413（OnSheetPointerDown）
if (L.rightMargin.Contains(pos)) { HandleEventLaneClick(L, pos, tick); ... }
```

ゴースト（`DrawEventGhost`）も同じくツール非依存で出る（`:1252-1256`、
「イベントレーンは現在のツールに関わらず常時クリックで追加できるので」というコメント付き）。

これは §7.3 実装時の意図的な設計だったが、**選択ツールで作業中に右端へカーソルを持っていくだけで
ゴーストが出て、クリックすると意図せずイベントが増える**。ユーザー指摘のとおり、
ノーツ側が「配置ツールを選んでいるときだけ置ける」のと非対称。

### 6.2 参照元

参照元は**最初からモード方式**。`TimelineMode`（`TimelineMode.h:5-16`）に
`InsertBPM` / `InsertTimeSign` が Tap / Hold / Flick と**同じ列挙**として並び、
ツールボックスも同じボタン列として生成される（`EditorWindows.cpp:23-44`、
キーボード 1〜8 で切替: `Application.cpp:188`）。

ゴーストも挿入もそのモードのときだけ走る:

```cpp
// ScoreEditor.cpp:952-966（ゴースト）
else if (currentMode == TimelineMode::InsertBPM)   { /* 横線 + "BPM" を hoverTick に描く */ }
else if (currentMode == TimelineMode::InsertTimeSign) { /* 横線 + "4/4" */ }
// ScoreEditor.cpp:986-993（クリック）
else if (currentMode == TimelineMode::InsertBPM)   insertTempo();
else if (currentMode == TimelineMode::InsertTimeSign) insertTimeSignature();
```

つまり muses が §7.3 で採った「レーン常時入力」は参照元に無い独自仕様で、
ユーザー指摘は**参照元の設計へ戻す**方向。

### 6.3 方針

`EditorTool` に**イベント配置用のツールを追加**し、追加操作をそのツール限定にする。

**現状のツール**（`ChartEditorApp.cs:39`）:
`Select, Tap, ExTap, Slide, Flick, AddWaypoint, Delete`

案は2つ（§13 Q1 で確認したい）:

| | 案A: `Event` 1つ | 案B: `Bpm` / `Meter` / `Scroll` の3つ |
|---|---|---|
| 種別の決め方 | **クリックした列**（§7.3 の「列＝種別」を維持） | ツールが種別を決める |
| ツールバー | +1 個（計8個） | +3 個（計10個） |
| 参照元との一致 | 部分的 | **一致**（`InsertBPM`/`InsertTimeSign`） |
| 3列レイアウト | 必要（種別選択を兼ねる） | 冗長になりうるが、**同時刻に別種別のイベントが並ぶ**ため表示上は3列が要る |
| ゴースト | 列で色が変わる（現状の `DrawEventGhost` をそのまま流用） | ツールで色が決まる |

**推奨は案A**。理由: 3列は「表示」としても必要（同じ tick に BPM と拍子が両方あると
1列では重なる）ので列は残る。列が残るなら列が種別を決めるほうが、ツールバーを増やさずに済む。

どちらでも共通して入れる規則:

- **`HandleEventLaneClick` はイベントツールのときだけ呼ぶ。**それ以外のツールでは、
  イベントレーンの空白クリックは「イベント選択の解除」だけを行う（何も追加しない）。
- **既存チップのクリックによる選択はツール非依存のまま**。チップは `Label` 要素自身が
  `PointerDownEvent` を拾って `StopPropagation` する（`UI.cs:526-531`）ので、この経路は元々
  `OnSheetPointerDown` に届いておらず、変更不要。
  → r3 §3 で高さレーンに入れた「**ドラッグ/選択はツール非依存、属性の巡回だけツール限定**」と同じ切り分け。
- **ゴースト（`DrawEventGhost`）もイベントツールのときだけ**出す（`:1252-1256`）。
- **§5 との相互作用**: イベントレーンを畳んだ状態でイベントツールを選んだら、
  **自動で表示に戻す**（畳んだままだと何もできず、原因が分からない）。

### 6.4 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:39` | `EditorTool` に追加 |
| `ChartEditorApp.cs:1406-1413` | イベントツールのときだけ `HandleEventLaneClick` |
| `ChartEditorApp.cs:1252-1256` | ゴーストをツール限定に |
| `ChartEditorApp.UI.cs:27-36` | `ToolButtons` に追加（色は `--event-*` を使う新 modifier クラス） |
| `ChartEditorRoot.uss:140-144` | `.tool-btn--event` を追加 |

---

## 7. テキスト入力が見えない（指摘7）

### 7.1 症状の切り分け

ユーザー報告「**反映はされている**が、入力値およびカーソルが映らない」。
つまりフィールドは機能しており、**描画だけが見えない**。ラベル・ボタン・ドロップダウンの
文字は見えているので、フォント自体の問題ではない。

### 7.2 最有力の仮説: 入力欄のテキスト色

`PanelSettings.asset` のテーマは `UnityDefaultRuntimeTheme.tss` ＝ `@import url("unity-theme://default")`、
**Unity 標準のランタイムテーマ（明色系）**。muses 側の USS は入力欄の**背景だけ**を暗くしている。

```css
/* ChartEditorRoot.uss:279-282 */
.prop-row .unity-base-field__input {
    background-color: var(--bg-control);   /* rgb(58,58,66) = 暗い */
    border-color: var(--border);
}
```

`color` は指定していない。USS の `color` は継承プロパティなので `.root` の
`color: var(--text)`（明色、`:36`）が降りてくる**はず**だが、標準テーマ側が
入力欄のテキストへ明示的に暗い色を当てていれば、継承より明示指定が勝つ。
その場合「暗い背景に暗い文字」で完全に読めなくなる。カーソル色も
`--unity-cursor-color`（既定は暗色）なので同時に見えなくなり、**症状の両方を説明できる**。

Unity 6 では `TextInputBaseField.cursorColor` / `selectionColor` の C# API が非推奨化され、
**USS プロパティ `--unity-cursor-color` / `--unity-selection-color` へ移行**している
（`UnityEngine.UIElementsModule.dll` の非推奨メッセージで確認済み）。

### 7.3 対抗仮説（潰しておく）

- **行高による切り詰め**: `.prop-row { height: 20px }`（`:259-264`）が固定高で、
  入力欄の中の `TextElement` が縦に潰れている可能性。ただしこれだと枠の見た目も崩れるはずで、
  ユーザーのスクリーンショットからは枠は正常に見える。優先度低。
- **動的アトラス**: フォントアトラスの問題ならラベルも消えるので該当しない。

### 7.4 方針

**1分で確定できる切り分けを先にやる。** `.prop-row .unity-base-field__input` に
`color: red;` を1行足してビルドし、赤い文字が見えれば §7.2 で確定。

確定後の修正（切り分けの結果に関わらず害が無いので、確定と同時に入れてよい）:

```css
.prop-row .unity-base-field__input,
.status-bar .unity-base-field__input,
.toolbar .unity-base-field__input {
    color: var(--text);
    --unity-cursor-color: var(--text);
    --unity-selection-color: rgba(74, 158, 255, 0.45);   /* --accent の半透明 */
}
```

- 対象は**右パネルだけでなくツールバーの「幅」欄・ステータスバー・モーダルのファイル名欄も**同じ
  （どれも同じ標準テーマの上に乗っている）。セレクタを `.root` 直下の子孫指定に一本化するほうが
  漏れが無い。
- **`--unity-*` 系のカスタムプロパティが USS 変数として通るか**は Unity のバージョンに依存するため、
  実機で赤文字テストと合わせて確認する（TabView / IMGUIContainer で2度踏んだ
  「UI Toolkit の前提は実機で確認するまで信用しない」の教訓どおり）。

### 7.5 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorRoot.uss:279-282` | 入力欄のテキスト色・カーソル色・選択色を明示 |

---

## 8. プレビュー切り替え時にステージが一瞬広がる（指摘8）

### 8.1 原因

`StageController` は**カメラのアスペクト比が変わるたびにステージ形状を作り直す**。

```csharp
// Stage/StageController.cs:62-72
public void EnsureBuilt() {
    float aspect = cam.aspect;
    if (dirty || !Mathf.Approximately(aspect, lastAspect)) { Rebuild(aspect); ... }
}
```

`Rebuild(aspect)` → `StageDerive.Derive(cfg, aspect)` で `laneK = cfg.U * aspect * tan(phi/2)`
（`StageDerive.cs:178`）などレーン幅が直接アスペクトに比例する。

一方 `cam.aspect` は **`cam.targetTexture` から自動導出**される。ところが

```csharp
// PreviewSystem.cs:406-410
public void DetachTexture() { RenderEnabled = false; if (cam != null) cam.targetTexture = null; }
// PreviewSystem.cs:349-360（targetTexture を割り当てるのはここだけ）
private void MaybeRender() { ... cam.targetTexture = rt; cam.Render(); ... }
```

**タイムラインタブへ移った瞬間に `targetTexture = null` になり、`cam.aspect` が
アプリのウィンドウ全体のアスペクト（例 1290/800 ≒ 1.61）へ戻る。**
`StageController` は `[ExecuteAlways]` の MonoBehaviour なのでタブに関係なく毎フレーム
`Update()` → `EnsureBuilt()` が走り、**その画面アスペクトでステージを作り直してしまう。**

プレビュータブへ戻ると `MaybeRender()` が `targetTexture = rt` を入れ直すが、
`StageController.Update()` と `ChartEditorApp.Update()`（→ `preview.Tick()` → `MaybeRender()`）の
**実行順は Unity が保証しない**ため、少なくとも1フレームは
「画面アスペクトで作られたステージ」が RenderTexture へ描かれる。
プレビュー領域（右パネル 300px を除いた横長でない領域）より画面のほうが横長なので、
**一瞬だけレーンが広がって見える** — ユーザー報告と一致する。

### 8.2 方針

**`cam.aspect` を明示的に固定し、`targetTexture` の有無から切り離す。**
`Camera.aspect` は代入するとその値が固定され（`ResetAspect()` を呼ぶまで自動導出に戻らない）、
UI 側の都合でステージ形状が動くことが原理的になくなる。

```csharp
// PreviewSystem.EnsureRenderTexture の中
rt = new RenderTexture(w, h, 16) { name = "ChartEditorPreview" };
rtW = w; rtH = h;
cam.aspect = (float)w / h;      // ← 追加。DetachTexture でも解除しない
```

あわせて **`MaybeRender()` で `cam.Render()` を呼ぶ直前に `stageController.EnsureBuilt()` を呼ぶ**
（実行順に依存せず、そのフレームのアスペクトで確実に組まれた状態から描く）。
`EnsureBuilt` は「dirty かアスペクト変化があるときだけ再構築」なので毎フレーム呼んでも安い
（コメントにも「何度呼んでも安全」とある）。

- `DetachTexture()` は `targetTexture = null` を続けてよい（アスペクトは固定済みなので影響しない）。
- ゲーム本体側（`SampleScene` の `StageController`）は無変更。あちらは実カメラなので
  画面アスペクトに追従するのが正しい。

### 8.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `PreviewSystem.cs:392-403` | `EnsureRenderTexture` で `cam.aspect` を明示設定 |
| `PreviewSystem.cs:349-360` | `MaybeRender` の `cam.Render()` 前に `stageController.EnsureBuilt()` |

---

## 9. 音源ファイルの選択 UI（指摘9）

### 9.1 現状

音源ファイル名は右パネル「音源」セクションのただのテキスト欄（`ChartEditorApp.UI.cs:615`）。
`song.audio` に入るのは**ファイル名だけ**で、実際の読み込みは
`Path.Combine(audioDir, song.audio)`（`PreviewSystem.cs:179`、`audioDir` は `song.muses` のあるフォルダ）。
つまり**音源は譜面と同じフォルダに置く前提**。

譜面ファイル用には自前の簡易ファイルブラウザ（`ShowFileModal`、`UI.cs:1547-1643`）が既にあり、
`Directory.GetFiles(browseDir, "*.muses")` で拡張子を決め打ちしている。

### 9.2 選択肢

**スタンドアロンビルドには OS ネイティブのファイル選択 API が無い**
（`EditorUtility.OpenFilePanel` は Editor 専用）。したがって:

| 案 | 内容 | 長所 | 短所 |
|---|---|---|---|
| **(a)** 自前ブラウザの音源版 | `ShowFileModal` を拡張子フィルタ引数付きに一般化し、「音源ファイル」行に `…` ボタンを足す | 確実に動く。「開く/別名で保存」と操作感が揃う。**追加コスト小** | Finder ではない |
| (b) Finder を開くだけ | `Application.OpenURL("file://" + dir)` | 数行 | 選択はできず、結局ファイル名を手入力 |
| (c) ネイティブダイアログ | macOS `NSOpenPanel` を Objective-C プラグイン、またはサードパーティ（StandaloneFileBrowser 等） | 本物の Finder | **editor-ui-redesign.md §0 でメニューバーの NSMenu プラグインを不採用と決めた前例**と方針が矛盾する。プラットフォーム別のビルド設定・保守が増える |

**推奨は (a)、必要なら (b) を「フォルダを開く」ボタンとして併設**。
ユーザーの表現は「finder を開く」だが、意図は「パスを手入力せずに選びたい」だと解釈している。
→ §13 Q3 で確認したい。

(a) で足す仕様:
- `ShowFileModal(bool saveMode, string pattern, Action<string> onPick)` へ一般化。
  音源用は `pattern = "*.ogg"`（`UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS)`
  で **OGG 決め打ち**なので、他形式を選べても読めない。→ フィルタも OGG に揃える）。
- 選んだファイルが `song.muses` と別フォルダなら、**そのままでは読めない**ことを
  `statusMessage` で警告する（相対パス解決の前提が壊れるため）。
- ジャケット画像（`song.jacket`）も同じ仕組みに乗るが、**画像の表示自体が未実装**なので今回は触らない。

### 9.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.UI.cs:1547-1643` | `ShowFileModal` を拡張子フィルタ＋コールバック方式へ一般化 |
| `ChartEditorApp.UI.cs:613-619` | 「音源ファイル」行に `…` ボタン（＋任意で「フォルダを開く」） |

---

## 10. 小節番号を 0 始まりにする（指摘10・アウフタクト）

### 10.1 現状

`SongAddr` は **bar / beat とも 1 始まり、tick のみ 0 始まり**（`Chart/SongMeta.cs:33-37` の
クラスコメントに明記）。既定の拍子も `bar = 1` から始まる。

```csharp
// SongMeta.cs:50-51
if (sorted.Count == 0 || sorted[0].bar != 1)
    sorted.Insert(0, new MeterEvent { bar = 1, numerator = 4, denominator = 4 });
```

そのため tick 0 は必ず「1 小節目の1拍目」になり、**アウフタクト（弱起）を置く場所が無い**。
負の tick は全経路で `Mathf.Max(0, ...)` により禁止されている。

### 10.2 参照元

参照元は **0 始まり**。

```cpp
// Score.cpp:19（Score のコンストラクタ）
timeSignatures.insert(std::pair<int, TimeSignature>(0, TimeSignature{ 0, 4, 4 }));
```

- `gotoMeasure` の下限も `measure < 0` で弾くだけ（`ScoreEditor.cpp:411`）＝ 0 は有効。
- 小節番号の表示も `"#" + std::to_string(measure)` で 0 から出る（`ScoreEditor.cpp:541`）。
- **0 小節目の拍子だけは削除できない**という保護がある（`EditorWindows.cpp:705`: `if (it.second.measure != 0)`）。

ユーザー要望は参照元と完全に一致する。

### 10.3 方針

**bar を 0 始まりにする（beat は 1 始まりのまま）。**

「表示だけ -1 する」案は採らない。理由:
- `bar` は `.muses` のテキスト（`[METER]` 行と `bar:beat:tick` アドレス）に**そのまま出る値**なので、
  表示と保存でずれると「ファイルを見た人が混乱する」ほうの害が大きい。
- 内部を 0 始まりにしても `ToTick`/`ToAddr` は**すべて相対計算**（`segStartBar` からの差分）なので、
  数式そのものは1行も変わらない。

具体的な変更:

| 箇所 | 現在 | 変更後 |
|---|---|---|
| `SongAddr.Normalize`（`SongMeta.cs:50-51`） | 先頭が `bar != 1` なら `bar = 1` の 4/4 を挿入 | `bar != 0` なら `bar = 0` の 4/4 |
| `SongAddr` のクラスコメント（`:36`） | 「bar/beat は1始まり」 | 「bar は0始まり、beat は1始まり」 |
| 拍子インスペクタの下限（`UI.cs:1041`） | `Mathf.Max(1, v)` | `Mathf.Max(0, v)` |
| 拍子イベントの削除（`ChartEditorApp.cs:2527-2532`） | 無条件で削除可 | **0 小節目の拍子は削除不可**（参照元に合わせる。消しても `Normalize` が既定 4/4 を補うため、ユーザーの設定だけが黙って消える） |
| `GotoMeasure`（`:616-622`） | `measure < 0` で弾く（変更不要） | — |
| `ChartSerializer` の `[METER]`（`:74-80, 114-118`） | コード変更不要（値の意味だけが 1 ずれる） | — |

**アウフタクトの書き方**（この変更で可能になるもの）:
- 0 小節目を短い拍子（例 1/4）にし、1 小節目から本来の 4/4 にする
  → `[METER] 0 1/4` と `[METER] 1 4/4` の2行。`MeterEvent` は既にこれを表現できる。
- 0 小節目も 4/4 のまま「頭に1小節の空白を置く」だけでもよい。

**データ移行**: リポジトリ内にも `Application.persistentDataPath`（`~/Library/Application Support/DefaultCompany/muses/`）
にも `.muses` ファイルは**1つも存在しないことを確認済み**（r3 §10 Q1 の回答と同じ状況）。
`ChartBuilder` のデモ譜面は `song.meters` を使っていない（`SongMeta` を持たない）ので影響なし。
**移行スクリプトは不要。**

### 10.4 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `Chart/SongMeta.cs:33-37, 50-51` | コメントと `Normalize` の基点 |
| `ChartEditorApp.cs:2527-2532` | 0 小節目の拍子を削除不可に |
| `ChartEditorApp.UI.cs:1038-1046` | 小節番号入力の下限 |
| `memory/editor-spec.md` §1.4 | bar の基点を明記（rev.3） |

---

## 11. ステータスバーの tick 桁数を固定（指摘11）

### 11.1 現状と原因

ステータスバー右側は `BuildChartInfoText()`（`UI.cs:1431-1441`）が
`SongAddr.FormatAddr(addr)` を使って `"1:1:0"` `"12:3:1260"` のように出す。
`FormatAddr` は**ゼロ埋めしない**（`SongMeta.cs:115-116`）。

UXML 上の並びは

```
status-scrub（flex-grow: 1） → status-time → sep → status-chartinfo → sep → status-validation
```

（`ChartEditorRoot.uxml:63-76`、`.status-scrub { flex-grow: 1 }` は `.uss:383-388`）。
`status-chartinfo` は幅を持たないので**中身の文字数がそのまま幅になり、
残りを埋める `status-scrub` が押されて動く** — ユーザー指摘のとおり。

`ChartData.TicksPerBeat = 5040` なので **1拍内の tick は 0〜5039 の最大4桁**。
4桁ゼロ埋めで必要十分。

### 11.2 方針

1. **表示専用のゼロ埋め書式を足す。**
   `FormatAddr` は `ChartSerializer` が `.muses` の書き出しにも使っている
   （`ChartSerializer.cs:126, 273, 300, 311`）ため、**そのまま変えるとファイルの見た目も変わる**。
   ステータスバー用に `FormatAddrPadded`（`$"{bar}:{beat}:{tick:0000}"`）を別に用意し、
   `BuildChartInfoText` からだけ呼ぶ。
   （インスペクタの「位置」欄・`.muses` も揃えるかは §13 Q5 で確認したい。
   `ParseAddr` は `int.Parse` なのでゼロ埋めを読めるため、揃えても壊れない。）

2. **ラベル幅も固定する。**桁数を固定しても
   - 小節番号は 0 → 10 → 100 と伸びる
   - `.mono` クラス（`.uss:70-76`）は `-unity-font-style: bold` だけで**実際には等幅フォントではない**

   ため、tick だけ揃えてもシークバーは動きうる。`status-chartinfo` に
   `min-width`（例 190px）＋ `-unity-text-align: middle-right` を与えて、
   **中身に関わらず幅が変わらないようにする**のが確実。`status-time` も同様（`00:00.00` 固定長だが念のため）。

### 11.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `Chart/SongMeta.cs:115-116` | `FormatAddrPadded` を追加（`FormatAddr` は据え置き） |
| `ChartEditorApp.UI.cs:1431-1441` | `BuildChartInfoText` を padded 版へ |
| `ChartEditorRoot.uss:70-76` | `.mono` に `min-width` / 右揃えを追加（または専用クラス） |

---

## 12. 音源オフセットの適用（§3.3 の付随問題、今回対応と決定）

### 12.1 現状

`SongMeta.offsetSec`（「音源先頭 → 譜面tick0のズレ(秒)」、`SongMeta.cs:27-28`）は
**プロジェクト全体でどこからも読まれていない**。`grep offsetSec` のヒットは

- `ChartSerializer.cs:56, 112` — `@OFFSET` 行の読み書き
- `ChartEditorApp.UI.cs:616, 1320` — 右パネルの入力欄

の2箇所だけで、再生系（`PreviewClock` / `PreviewSystem` / `ChartFormat`）には一切現れない。
`PreviewClock.SongTime` は `AudioSource.time` をそのまま返す（`PreviewClock.cs:34-38`）ので、
**頭出しは常に「音源0秒 = 譜面tick0」に固定**されている。

アウフタクト（§10）と「曲頭と譜面頭の関係を決める」という目的が同じなので、まとめて対応する。

### 12.2 方針

**変換を `PreviewClock` に閉じ込める。**`PreviewClock` は AudioSource を包む唯一の層で、
外（`PreviewSystem` / `ChartEditorApp`）は既にすべて**譜面時間（tick0 = 0秒）**でやり取りしている
（`Seek(TickToSeconds(cursorTick))`、`scrollTick` 同期、スクラブバー等）。ここで吸収すれば
呼び出し側は1行も変わらない。

```
audioTime = songTime + offsetSec        （offsetSec > 0 = 譜面tick0が音源のoffsetSec地点）
songTime  = audioTime - offsetSec
```

| メソッド | 変更 |
|---|---|
| `SongTime` の getter | `source.time - offset` |
| `Play()` | `source.time = clamp(pausedAt + offset, 0, clip.length - 0.001f)` |
| `Seek(t)` | `t` は譜面時間のまま。再生中なら `source.time = t + offset` |
| 無音フォールバック（clip 無し） | **無変更**。音源が無ければオフセットの意味が無い |

- **`offsetSec` の反映経路**: `PreviewSystem.Rebuild` で `clock.Offset = song.offsetSec` を入れる
  （右パネルでの編集は `MarkPreviewDirty()` → `Rebuild` を呼ぶので自動的に追従する）。
- **負のオフセット（譜面tick0が音源より前）**: `source.time` を 0 でクランプする。
  この場合、先頭 `|offset|` 秒は音が鳴らないまま譜面時間だけ進むのが本来の挙動だが、
  `AudioSource.PlayDelayed` を挟む必要があり複雑化する。**今回はクランプ止まりとし、
  その区間だけ音と譜面がずれることを既知の制限として残す**（正のオフセットが通常の用途）。
- **符号の向きは実機で確認する**。`@OFFSET` にどちら向きの値を書くのが自然かは
  実際に音源を当てて聴かないと確定しない（コメントの文言からは上記の向きだが、
  逆だった場合の修正は符号1つ）。
- `ChartValidator` の V10（譜面長 vs 音源長、`AudioLengthSec` 使用）は、
  厳密にはオフセットぶんを見込むべきだが**今回は触らない**（警告の閾値が数秒動くだけ）。

### 12.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp/PreviewClock.cs:19-60` | `Offset` プロパティと `SongTime` / `Play` / `Seek` の換算 |
| `ChartEditorApp/PreviewSystem.cs:147-174` | `Rebuild` で `clock.Offset = song.offsetSec` |

---

## 13. 実装順

独立性と「壊れたときの切り分けやすさ」で並べる。

1. **§2** はみ出し（USS 1行・完全に独立）
2. **§7** テキスト入力の可視化（**先に赤文字テストで確定**してから修正、独立）
3. **§11** tick 桁数とラベル幅（表示のみ、独立）
4. **§1** Slide 置きかけのプレビュー（描画のみ、独立）
5. **§8** プレビューのアスペクト固定（`PreviewSystem` 内で閉じる）
6. **§3** プレビューの小節線（`NoteGeometry`/`NoteView` のシグネチャ変更を伴う）
7. **§4** Slide の点の幅変更（入力、`NoteRef` 化）
8. **§5** イベントレーンの表示トグル（レイアウト）
9. **§6** イベント入力モード（§5 と相互作用するので後）
10. **§9** 音源ファイル選択
11. **§12** 音源オフセットの適用（`PreviewClock` に閉じる）
12. **§10** 小節番号 0 始まり（**単独コミット**。譜面ファイルの意味に触るため切り戻せるようにする）

---

## 14. ユーザー確定事項（2026-08-02、着手前に確認済み）

| # | 節 | 決定 |
|---|---|---|
| Q1 | §6 | イベント入力モードは **「イベント」1つ**。クリックした列が種別を決める（現行の「列＝種別」を維持） |
| Q2 | §5 | 非表示トグルは **3種まとめて1つ**（高さレーンと同じ形） |
| Q3 | §9 | 音源選択は **自前ブラウザの音源版**（`ShowFileModal` を拡張子フィルタ付きに一般化）。ネイティブダイアログは不採用 |
| Q4 | §4 | 幅変更は **選択中の全点に同じ差分を適用**（参照元・muses の移動ドラッグと揃える） |
| Q5 | §11 | 4桁ゼロ埋めは **ステータスバーのみ**。インスペクタ「位置」欄と `.muses` は従来どおり |
| Q6 | §12 | `song.offsetSec` の未適用も **今回まとめて直す** |

**実装後の確認では特に次を見てほしい**（実装時点では検証できない点）:

- §7: 入力欄の文字とカーソルが見えること（見えなければ §7.3 の対抗仮説へ進む）
- §3: プレビューの小節線がタイムラインの小節線と一致すること（BPM 変化・4/4 以外の拍子でも）
- §8: タブを何度往復してもステージの形が変わらないこと
- §1: Slide 配置中に始点がノーツとして見え、カーソルまで帯が伸びること
- §10: 0 小節目に置いたノーツが保存・再読込で往復すること
- §12: `@OFFSET` に正の値を入れたとき、**音が遅れて始まる向きで合っているか**（符号の向き）

---

## 実装ログ（2026-08-02、同セッション内）

§13の実装順どおり§2→§7→§11→§1→§8→§3→§4→§5→§6→§9→§12→§10の順で全項目実装した。
**`dotnet build Assembly-CSharp.csproj` でコンパイル成功を確認済み**（警告12件はすべて
既存分・今回の変更と無関係）。Unity Editor上でのPlay確認は次回。

- **§2**: `notes-sheet`/`preview-surface` USSクラスを追加し `overflow: hidden` を設定。
  スタイルの直書き(`style.flexGrow`)もクラス経由へ寄せた。
- **§7**: `.prop-row`/`.status-bar`/`.toolbar`/`.modal` 配下の `unity-base-field__input` に
  `color`/`--unity-cursor-color`/`--unity-selection-color` を明示（重複していた旧`.status-bar`
  専用ルールは統合して削除）。実機での確定は次回。
- **§11**: `SongAddr.FormatAddrPadded`（tick 4桁ゼロ埋め、ステータスバー専用）を追加し
  `BuildChartInfoText`だけをこちらへ切り替え。`.muses`/インスペクタは`FormatAddr`のまま。
  `#status-time`/`#status-chartinfo`に`min-width`+右揃えを追加。
- **§1**: `pendingSlideStart`の描画を`DrawGhostPoint`（帯グリフ）に統一し、`DrawPlacementGhost`の
  後ろへ移動。Slideの帯ゴーストは`Rect.MinMaxRect`の矩形塗りから`FillQuad`（四隅を結ぶ四角形）へ
  変更し、始点も帯として描くようにした。
- **§8**: `PreviewSystem.EnsureRenderTexture`で`cam.aspect`をRenderTextureのアスペクトへ
  明示固定（`DetachTexture`で`targetTexture=null`にしても画面アスペクトへ戻らなくなる）。
  `MaybeRender`の`cam.Render()`直前に`stageController.EnsureBuilt()`を追加し、実行順への
  依存も無くした。
- **§3**: `NoteGeometry.Build`/`NoteView.Build`に`List<float> barTimes`引数を追加（nullなら
  従来どおり`cfg.bpm`から4拍間隔、GameControllerは無変更で回帰なし）。
  `PreviewSystem.BuildBarTimes`が`song.meters`＋`chart.bpmEvents`から実際の小節頭の時刻を
  組み立てて渡す。
- **§4**: `resizingNote`(単発ノーツのみ)を`resizingActive`+`resizeOriginByRef`
  (`Dictionary<NoteRef,Waypoint>`)へ再構成。`EdgeGrabSign`を`NoteRef`ベースに変更し
  単発ノーツ限定の条件を撤廃。ドラッグ中は選択中の全点へ同じセルデルタ(スナップ済み)を適用する
  （移動ドラッグと同じ`TryPaneAt`によるガター越え対策も流用）。
- **§5**: `showEventLane`/`EventLaneW`を高さレーンと同じ形で追加。メニュー「表示」・右パネル
  「表示設定」の両方にトグルを追加。畳んだ間は`UpdateEventChips`/`UpdateSheetLabels`の該当行を
  早期returnで省く。
- **§6**: `EditorTool.Event`を追加。イベントレーンのクリック追加・ゴーストの両方を
  `currentTool == EditorTool.Event`限定にした（それ以外のツールでの空白クリックは選択解除のみ）。
  `SelectTool`でEventツールを選んだ際、イベントレーンが畳まれていたら自動的に開く。
  ツールバーに「イベント」ボタン（`--event-bpm`色）を追加。
- **§9**: `ShowFilePickerModal`（拡張子フィルタ＋コールバックのみを受け取る汎用モーダル）を
  `ShowFileModal`とは別に新設。音源ファイル行に「…」ボタンを追加し`PickAudioFile`から呼ぶ。
  選んだファイルが`song.muses`と別フォルダなら`statusMessage`で警告する
  （相対パス解決の前提が壊れるため）。拡張子は`*.ogg`に固定
  （`UnityWebRequestMultimedia.GetAudioClip`がOGG決め打ちのため）。
- **§12**: `PreviewClock`の内部状態(`pausedAt`/`source.time`/`silentT0`)の意味を
  「音源上の再生位置(audioTime)」に統一し、`Offset`プロパティを追加。外部公開の`SongTime`は
  `AudioTime - Offset`、`Seek(songTime)`は内部で`songTime + Offset`へ変換する。
  呼び出し側(`PreviewSystem`/`ChartEditorApp`)は譜面時間でやり取りするままで変更不要。
  `PreviewSystem.Rebuild`で`clock.Offset = song.offsetSec`を設定。
  **符号の向き（`@OFFSET`に正の値を入れたとき音が遅れる向きで合っているか）は未検証**
  （実際に音源を当てて聴く必要がある、§13末尾参照）。
- **§10**: `SongAddr.Normalize`の既定挿入を`bar=1`→`bar=0`に変更（コメントも0始まりへ更新）。
  拍子インスペクタの下限を`Mathf.Max(1,v)`→`Mathf.Max(0,v)`。`DeleteSelectedEvent`のMeterケースに
  「0小節目は削除不可」のガードを追加（`statusMessage`で理由を表示）。`memory/editor-spec.md`
  §1.4を更新（bar基点の明記、`[METER]`のサンプル値も0始まりへ）。
  **リポジトリ内・`Application.persistentDataPath`とも`.muses`ファイルは1つも存在しないことを
  確認済みのため、データ移行は不要だった**（r3 §5と同じ状況）。

**次回セッション最優先事項**: Unity Editorでの実機確認。特に
§7（入力欄の文字とカーソルが実際に見えるか）、§3（プレビューの小節線がタイムラインと一致するか、
BPM変化・非4/4拍子でも）、§8（タブ往復でステージの形が変わらないか）、
§1（Slide配置中に始点が見え、カーソルまで帯が正しく伸びるか）、
§12（`@OFFSET`の符号が意図どおりか）、§10（0小節目のノーツが保存・再読込で往復するか）を
重点的に見る。

## 関連

- `memory/editor-ui-rework-r3.md` — 前段。§1〜§8 は実機で問題なしと確認済み。
- `memory/editor-ui-rework-r2.md` — その前段。§6 の「場所でeasingの軸を分ける」規則は §6 の切り分けの下敷き。
- `memory/editor-ui-rework-mmw.md` — §5.2 の選択の点単位化が、本書 §4 の前提を解いた。
- `memory/editor-ui-redesign.md` — §7.2 の帯構成（本書 §5）、§7.4-D の幅変更（本書 §4）、§0 のプラグイン不採用方針（本書 §9）。
- `memory/editor-spec.md` — Phase 4 機能仕様 rev.2。**§10 の決定により §1.4 に bar の基点を明記して rev.3 にする。**
- `memory/note-spec.md` — ノーツ仕様 rev.6（`cellF` は左端基準）。本書では変更しない。
- `memory/reference/MikuMikuWorld-master/` — 参照元ソース。本書 §1（`TimelineNotes.cpp:217-229`）、
  §4（`TimelineNotes.cpp:14-157`）、§6（`TimelineMode.h:5-16`, `ScoreEditor.cpp:952-993`）、
  §10（`Score.cpp:19`, `EditorWindows.cpp:705`）の根拠。
