# 譜面エディタ UI 改修設計 第2弾（実機フィードバック反映、2026-08-02 rev.1）

`memory/editor-ui-rework-mmw.md` の §1〜§7・移植候補8件を実装したビルドをユーザーが実機で確認し、
そこで出た **6項目** の指摘に対する設計。前回同様、参照元
（`memory/reference/MikuMikuWorld-master/`、C++/ImGui/OpenGL）の該当箇所に根拠を紐づける。

**コンパイルエラーは無し**（ユーザー確認済み）。前回の申し送りだった「Unity Editor未検証」は解消。

**このドキュメントは実装計画。ユーザーの確認後に実装へ入る。**

- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（描画・入力、2418行）、
  `ChartEditorApp.UI.cs`（UI Toolkit の要素構築、1617行）。
- データ構造: `unity/Assets/Scripts/Chart/ChartNote.cs`、`Chart/ChartSerializer.cs`。
- Unity 6000.5.6f1。

---

## 0. ユーザー指摘とこのドキュメントの対応

| # | 指摘 | 節 | 影響範囲 |
|---|---|---|---|
| 1 | 中継点は常に存在を示す（高さレーン含む、灰色にしなくて良い） | §1 | 描画のみ |
| 2 | Sky の Slide を高さレーンからも選択できるように | §2 | 入力＋描画 |
| 3 | インスペクタは Slide でも「現在選択している点のみ」（折りたたみ不要）、複数選択時は代表1点 | §3 | UI 構築のみ |
| 4 | ペーストのゴーストが横方向に制限されていない | §4 | 入力 |
| 5 | ↑↓キー移動が不自由（5回押して1マス、長押しで断続） | §5 | **不具合**。入力 |
| 6 | easing を横と高さで独立させたい | §6 | **データ構造＋フォーマット** |
| 7 | 右クリックメニューに削除/切取/コピー/貼付/反転を常設 | §7 | 入力 |

**§6 だけがデータ構造とファイルフォーマットに触る**ので、他と切り離して最後に実装する（§8）。

**ユーザー確定事項（2026-08-02、着手前に確認済み）**:

- §6 easing の割り当ては **横 = `cellF` + `width` / 高さ = `layerF`**。
  ファイル形式は既存 `ease=` を**横用として据え置き**、高さ用に `easeh=` を新設（§6.2）。
- §2 高さレーンは **全ノーツの点を常時表示し、クリックで選択**（選択済みへの絞り込みを撤廃）。
- §7 右クリックメニューは **常に全項目を表示し、実行できないものは無効表示**（`AddDisabledItem`）。
- §4 は **スナップ＋盤面内クランプを入れ、既存のドラッグ移動にも同じクランプを適用**して挙動を揃える。
- §3 代表点の優先順は **段階的な絞り込み（タイブレーク連鎖）**（§3.2 の擬似コードのとおり）。
- §1 marker の見せ方は **形で区別し、濃さは全部同じ**（Visible=塗りつぶし / それ以外=輪郭のみ）。

---

## 1. 中継点を常に描く（marker による非表示をやめる）

### 現状

`ChartEditorApp.cs:969` と `:998` が

```csharp
foreach (var wp in note.points)
{
    if (wp.marker != WaypointMarker.Visible) continue;   // ← ここ
    ...
    FillRect(p, new Rect(x - 3, y - 3, 6, 6), Color.white);
}
```

となっており、**`marker == Visible` の中継点しか描かれない**。`NewWaypoint`（`:2349`）の既定は
`WaypointMarker.None` なので、**エディタで置いた中継点は基本的に画面に出ない**。
高さレーン（`DrawHeightCurve`、`:1136-1144`）は marker を見ずに全点を描くが、
**選択中のノーツに限る**（`drawPoints: true` は選択ノーツにしか渡らない、`:1100/1105`）。

### なぜ現状がこうなっているか

`marker` は**ゲーム側の意味**を持つ（note-spec.md §3）:

| marker | ゲーム上の意味 |
|---|---|
| `None` | 純粋な制御点。コンボにならず、ゲーム画面にも出ない |
| `Visible` | コンボ点。ゲーム画面に Tap 型のマーカーが乗る |
| `Invisible` | コンボにならない。ゲーム画面にも出ない |

前回の実装は「ゲームでの見た目」をそのままエディタに持ち込んだが、**エディタは編集対象の
存在を示す場所**であって、ゲーム画面の再現ではない。ユーザー指摘のとおり、
**すべての点は常に見えていなければ掴めない**（§2 の点単位選択と直結する）。

参照元も同じ考え方で、`drawHoldStepOutline` が真なら **`Invisible` を含む全ステップに
`drawHighlight` の枠を描く**（`TimelineNotes.cpp:286-287`）。スプライトを省くのは
`Invisible` のときだけで、**枠は必ず出る**。

### 方針

- **`marker` による描画スキップを廃止**し、全中継点を描く。濃さは3種とも同じ（灰色化しない）。
- **形で区別する**（ユーザー確定）:
  - `Visible`（コンボ点）: 6×6 の**塗りつぶし四角**（現状と同じ）。
  - `None` / `Invisible`: 6×6 の**輪郭のみの四角**（`FillRectOutline`、既存ヘルパー）。
  - この2分割にする理由は、note-spec.md §2.3 の「BPM 境界と `comboStep` 上書き点には
    Visible 中継点が必須」というルールが、**目視で確認できるようになる**こと。
    `None` と `Invisible` はどちらもコンボにならず、エディタ上で区別する実益が小さいので同形にする。
- **始点・終点は従来どおり `DrawEndpointGlyph`**（`:1009-1010`）で Tap 相当の矩形を描く。
  端点の marker は「Tap 型マーカーを重ねるか」の意味しか持たない（note-spec.md §3）ので、
  中継点の描き分けとは別扱いのままでよい。
- **高さレーンも同じ規則**にする（`DrawHeightCurve` の点描画、`:1136-1144`）。
  こちらは既に marker を見ていないので、**形の区別を追加**するだけ。
  ただし「選択中のノーツしか点を描かない」制約は §2 で撤廃する。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:967-973` | forceSky 側の中継点描画。`continue` を削除し、marker で塗り/輪郭を分岐 |
| `ChartEditorApp.cs:996-1002` | 通常側の中継点描画。同上 |
| `ChartEditorApp.cs:1136-1144` | 高さレーンの点描画。marker で塗り/輪郭を分岐 |

小さなヘルパー `DrawWaypointGlyph(Painter2D, Rect, WaypointMarker, Color)` を1つ足して
3箇所から呼ぶ形にする（3箇所に同じ分岐を書かない）。

---

## 2. 高さレーンからの選択

### 現状

`HandleHeightLanePointerDown`（`ChartEditorApp.cs:1593-1625`）は

```csharp
foreach (var note in SelectedNotesDistinct())   // ← 選択済みノーツだけを探索
```

となっており、**シート本体で先に選択していないノーツは高さレーンで一切掴めない**。
掴めなかった場合は `statusMessage = "高さレーンで編集するノーツを先に選択してください"` を出す。
描画側（`DrawHeightLane`、`:1089-1106`）も、非選択ノーツは α=0.07 の線だけで**点を描かない**。

### なぜ現状がこうなっているか、そして何が変わるか

editor-ui-redesign.md §7.5 の設計根拠は「**同時押し Slide の高さカーブが重なると
1レーンでは編集できない**」で、その解決手段として「選択によって描画対象を絞る」を採った。
この理屈自体は今も正しい。しかし実際に使うと、

- 高さを直したい Slide を、**まずシート本体で探して選択する**という往復が毎回発生する。
- §4（editor-ui-rework-mmw.md）で高さ変化のある Slide は Sky ペインのみに濃淡で描かれるので、
  **シート本体では層を跨ぐ Slide ほど掴みにくい**。高さレーンのほうが素直に見えているのに、
  そちらからは触れないという逆転が起きている。

ユーザー指摘の「sky の slide について、高さレーンからも選択できるように」はこの逆転を指している。

**絞り込みを「選択でしか描かない」から「選択を濃く描く」へ弱める**。重なり問題は
「濃淡による区別」＋「掴む優先度」で解く（下記）。

### 方針

**描画（`DrawHeightLane`）**:

- **全ノーツのカーブと点を常に描く**。
  - 非選択: カーブ α=0.28 程度、点は輪郭のみ（§1 の形の規則に加えて α で選択状態も表す）。
    現状の 0.07 は「当たりがつく程度」を狙った値だが、掴める対象になる以上そこまで薄くしない。
  - 選択中: 現状どおり `NoteColor(note.kind)` の不透明カーブ＋点。
- **選択中のノーツを後に描く**（現状も同じ順序、`:1097-1105`）ので、重なっても選択中が手前に出る。
- 単発ノーツ（`points.Count == 1`）は現状カーブを描かない（`:1099/1104` が `Count < 2` を弾く）。
  これは維持する。単発ノーツは高さレーン上では**点1つ**として描き、掴める
  （§7.5 実装時に「単発も高さレーンで掴めて 0/1 にスナップする」が既に入っている、`:1713`）。
  → 描画側だけが `Count < 2` で弾いていて**入力と食い違っている**ので、ここで揃える。

**入力（`HandleHeightLanePointerDown`）**:

- 探索対象を `chart.notes` 全体に広げる。
- **掴む優先度**は「選択中 > 非選択」。具体的には、選択中ノーツの点を先に半径内で探し、
  見つからなければ全ノーツから探す（2パス）。これで「重なっているときに選択中を掴み続けられる」
  という現状の利点を保ったまま、未選択も掴めるようになる。
- **クリック＝選択も兼ねる**。掴んだ点を `SetSingleSelection(new NoteRef(note, index))` で
  選択状態にしてからドラッグに入る。これでシート本体とインスペクタが即座に追従する。
  - Shift 併用で選択に追加（シート本体の `Shift+クリック=トグル` と揃える）。
  - **既に選択済みグループの一員を掴んだときは選択を維持する**（シート本体の
    editor-ui-redesign.md §7.4 実装ログにある規則をそのまま踏襲。これをしないと
    複数選択したまま高さを触れない）。
- 半径外をクリックしたときは**選択解除**（シート本体の空白クリックと同じ意味）。
  現状の「先に選択してください」メッセージは不要になるので消す。

**ドラッグ中の対象**は現状どおり `heightDragNote` / `heightDragPointIndex` の**1点のみ**。
複数選択した点をまとめて layerF 移動する機能はここでは入れない（シート本体のドラッグも
点単位で複数対応しているので将来揃える余地はあるが、今回の指摘の範囲外）。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1089-1106` | `DrawHeightLane`: 全ノーツ描画へ。非選択の α を上げ、単発ノーツも点を描く |
| `ChartEditorApp.cs:1108-1145` | `DrawHeightCurve`: `drawPoints` の意味を「選択中か」から「濃く描くか」へ。点は常に描く |
| `ChartEditorApp.cs:1593-1625` | `HandleHeightLanePointerDown`: 2パス探索＋選択の更新。Shift 対応 |

---

## 3. インスペクタを「選択中の1点」だけにする

### 現状

`RebuildInspector`（`ChartEditorApp.UI.cs:763-839`）は
**`note.points` を全部ループして点ごとに `Foldout` を作る**。中継点が10個ある Slide を選ぶと
Foldout が10個並び、各 Foldout の中に 位置 / layerF / cellF / width / easing / marker /
comboStep上書き / comboStep(tick) の8行が入る。ユーザー指摘のとおり圧迫される。

**この構造は §5.2（点単位選択）を入れる前の名残**。当時は「ノーツを選択する」しかなかったので
全点を出す必要があったが、いまは `selection` が `List<NoteRef>` で
**どの点を選んでいるかが確定している**（`ChartEditorApp.cs:133` の `NoteRef`）。

参照元も同じで、プロパティ表示は選択中の要素に対してのみ行われる。

### 3.1 単一選択時

- **`selection[0]` の点だけ**を出す。Foldout で包まず、インスペクタ直下にフラットに並べる。
- 見出しは `種別: Slide — 中継点 2/5（始点）` のように
  「**ノーツ種別**」「**何番目の点か / 全点数**」「**役割（始点/終点/中継点）**」を1行で示す。
  Foldout が消えることで「いまどの点を見ているか」の手掛かりが無くなるので、ここで補う。
- ノーツ単位の項目（`scrollGroup`、「このノーツを削除」）は従来どおり出す。
  ただし**点単位の項目と混ざらないよう、ノーツ単位 → 点単位の順で並べ、区切りのラベルを挟む**。
- 「この中継点を削除」ボタン（`:829-838`）は、選択中の点が中継点のときだけ出す
  （§5.2 の削除規則: 始点/終点を消すと Slide 全体が消えるので、そちらは
  「このノーツを削除」が担当する）。

**再構築の条件を見直す**: `SyncModelToUi`（`ChartEditorApp.UI.cs:1280-1296`）は
「選択が変わった / 中継点が増減したとき」に `RebuildInspector` を呼んでいる。
**同じノーツ内で選択する点だけが変わったとき**も作り直す必要があるので、
追跡キーに `NoteRef.index` を含める（現状の追跡キーが何かは実装時に確認し、
`selection` の内容（note 参照 + index の列）そのものを比較する形にする）。

### 3.2 複数選択時（代表点）

`RebuildMultiSelectInspector`（`ChartEditorApp.UI.cs:850-885`）は現在
「N件選択」＋一括削除＋（全部単発なら）種別一括変更 しか出さない。
ここに**代表1点の情報**を追加する。

**代表点の選び方（ユーザー確定: 段階的な絞り込み）**:

```
候補 = selection

// 1) 始点があれば、それだけに絞る
if 候補.Any(r => r.index == 0)                        候補 = 候補.Where(r => r.index == 0)
// 2) 無ければ終点があるか
else if 候補.Any(r => r.index == r.note.points.Count - 1)
                                                       候補 = 候補.Where(r => r.index == 最終)
// 3) 残った候補のうち cellF が最小（＝左）のもの
候補 = 候補.MinBy(r => r.note.points[r.index].cellF) の同値グループ
// 4) それでも複数なら tick が最小（＝最も早い）のもの
代表 = 候補.MinBy(r => r.note.points[r.index].tick)
```

- 単発ノーツ（`points.Count == 1`）は `index == 0` かつ `index == 最終` なので、
  **段階1で始点として拾われる**。複数の単発ノーツを矩形選択した場合は
  段階3・4（左→早い）で1点に決まる。
- 「左」は `cellF` の値で判定する。**Ground と Sky で同じ cellF は同値**になるが、
  そのときは段階4（tick）が決める。画面上の X 座標で比べる案もあったが、
  `cellF` はデータ上の値でレイアウトに依存しないぶん、挙動が読みやすい。

**代表点の情報は読み取り専用にするか、編集可能にするか**:
→ **編集可能にする**。ただし**編集は代表点1点にだけ効く**ことをラベルで明示する
（`代表点（5件選択中）— 編集はこの点のみに適用されます`）。
複数点への一括適用（例: 選択した全点の easing をまとめて変更）は参照元にはある機能
（`setEase` / `setStepType`、`EditorWindows.cpp:168-194`）だが、
**今回の指摘は「圧迫される」であって「一括編集がほしい」ではない**ので、範囲外とする。
必要になったら §7 の右クリックメニュー（一括 easing 変更）として足すのが自然。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.UI.cs:763-839` | 点ごとの Foldout ループを廃止し、選択中1点のフラット表示へ |
| `ChartEditorApp.UI.cs:850-885` | 代表点の選出＋その点の編集行を追加 |
| `ChartEditorApp.UI.cs:1280-1296` | 再構築の判定に選択中の index を含める |

点の編集行を組む部分は単一選択と代表点で同じものを使うので、
`BuildWaypointRows(VisualElement host, Note note, int index)` として切り出して2箇所から呼ぶ。

---

## 4. ペーストのゴーストを横方向に制限する

### 現状

`ConfirmPaste`（`ChartEditorApp.cs:2122-2154`）と `DrawPasteGhost`（`:2158-`）は

```csharp
var (layerF, cellF) = L.PaneAt(pos.x);
float deltaCell = cellF - pasteAnchorCell;      // ← 生の float。スナップ無し
...
wp.cellF += deltaCell;                          // ← クランプ無し
```

**問題は3つある**:

1. **スナップされていない。** ドラッグ移動（`OnSheetPointerMove:1751`）は
   `SnapCellTo(rawCell - dragOriginRawCell, cellStep)` と差分をスナップしているのに、
   ペーストだけ生の float。結果、**貼り付けたノーツが半端な cellF に着地する**。
2. **盤面外へはみ出せる。** `PaneAt` は自分の戻り値を `[0, Cells]` にクランプするが
   （`:713/715`）、`deltaCell` は差分なので、**元の cellF に足した結果は範囲外になりうる**。
   0 未満や 12 超のノーツができる（`ChartValidator` が後で警告を出すが、
   置けてしまうこと自体が問題）。
3. **ガター上でカーソルが飛ぶ。** `PaneAt` はガターに対して `(0.5f, Cells * 0.5f)` を返す
   （`:716`）。ペースト中にカーソルがガターを通ると **`deltaCell` が突然
   `6 - pasteAnchorCell` にジャンプし、layerF も 0.5 になる**。ゴーストが盤面中央へ吹っ飛ぶ。

### 方針（ユーザー確定: スナップ＋盤面内クランプ、ドラッグ移動にも同じクランプ）

**共通ヘルパーを1つ作り、ペーストとドラッグ移動の両方から呼ぶ。**

```csharp
/// 点群を cellF 方向へ delta だけ動かすときの、盤面(0〜Cells)に収まる実効 delta を返す。
/// スナップ後にクランプするので、クランプで刻みが崩れることはない（両端は盤面境界に吸着）。
private static float ResolveCellDelta(IEnumerable<Waypoint> pts, float rawDelta, float step)
{
    float d = SnapCellTo(rawDelta, step);
    float minCell = pts.Min(w => w.cellF);
    float maxEdge = pts.Max(w => w.cellF + w.width);
    d = Mathf.Max(d, -minCell);          // 左へはみ出さない
    d = Mathf.Min(d, Cells - maxEdge);   // 右へはみ出さない
    return d;
}
```

- **刻み（`step`）はドラッグ移動と同じ規則**: 対象に Slide の点が1つでもあれば 0.5、
  単発ノーツだけなら 1（`:1750` の `cellStep` と同じ判定）。
- **クランプは点群全体で1回**行う（点ごとにクランプすると**相対位置が崩れて形が壊れる**）。
  そのぶん、盤面幅を超える幅の点群は端に張り付いたまま動かなくなるが、これは正しい挙動。
- **ガター対策**: `PaneAt` がガターを返したとき（`layerF == 0.5f`）は
  **直前の有効な (layerF, cellF) を保持して使う**。ペースト用に
  `lastValidPaneLayer` / `lastValidPaneCell` を持つのではなく、`PaneAt` に
  「ガターなら null を返す」オーバーロード（`TryPaneAt`）を足し、
  呼び出し側で「null なら前回値を使う」と書くほうが、他の呼び出し箇所（配置ゴースト等）でも
  同じ問題を潰せる。**ただし今回触るのはペーストとドラッグの2箇所に限定**し、
  他は挙動を変えない（配置ツールはガターで `break` しており既に安全、`:1349`）。
- **`layerF` も同様にクランプ**。現在 `Mathf.Clamp01(wp.layerF + deltaLayer)` と
  **点ごとにクランプしている**（`:2143`）ので、層を跨ぐ Slide をペーストすると
  **端の点だけが潰れてカーブが変形する**。cellF と同じく点群全体で実効 delta を決める。

**`ConfirmPaste` と `DrawPasteGhost` は同じ計算を通す**こと（editor-ui-rework-mmw.md §1 の
「ゴーストと確定位置を一致させる」原則）。今回は計算が複雑になるので、
**`ComputePasteTransform()` が `(hoverTick, deltaCell, deltaLayer)` を返す**形に切り出し、
両者がそれを呼ぶ構造にする（現状は同じ式を2箇所に書いている）。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:2122-2154` | `ConfirmPaste`: 共通の変換計算を呼ぶ |
| `ChartEditorApp.cs:2158-` | `DrawPasteGhost`: 同上 |
| `ChartEditorApp.cs:1742-1768` | `OnSheetPointerMove` のドラッグ移動: `ResolveCellDelta` / layerF 群クランプを適用 |
| `ChartEditorApp.cs:710-717` | `PaneAt` の隣に `TryPaneAt`（ガターなら null）を追加 |

---

## 5. ↑↓キー移動の不具合

### 症状と原因

ユーザー報告: 「5回連続で押さないと上に1マス進まない。長押し時も連続で動く時と止まる時を
交互に繰り返す」。

`OnSheetKeyDown`（`ChartEditorApp.cs:1918-1926`）のロジック自体は正しい
（`SnapTickTo` した基準に ±`snapTicks` するだけ）。**原因は UI Toolkit のフォーカス移動**。

- `notesSheet` は `focusable = true` で作られている（`ChartEditorApp.UI.cs:366`）。
- UI Toolkit のランタイムパネルは、矢印キーを **`KeyDownEvent` とは別に
  `NavigationMoveEvent`** としても発行し、これが**フォーカスを次の focusable 要素へ移す**。
- `OnSheetKeyDown` で `evt.StopPropagation()` を呼んでいるが、これは `KeyDownEvent` の
  伝播を止めるだけで、**別イベントである `NavigationMoveEvent` は止まらない**。
- 結果、1回目の ↑ で `cursorTick` は進むが**同時にフォーカスが右パネルのどこかへ移る**。
  2回目以降はフォーカスが他要素にあるので `notesSheet` の `KeyDownEvent` が来ず、
  ただフォーカスが順に移動していく。**焦点が一巡して `notesSheet` に戻ると再び1マス進む**
  ——これが「5回押して1回進む」「長押しで動く時と止まる時が交互」の正体。
  移動回数がちょうど5になるのは、その時点で `notesSheet` から数えて focusable 要素が
  5つあるという意味で、レイアウトによって変わる。

### 方針

```csharp
notesSheet.RegisterCallback<NavigationMoveEvent>(evt =>
{
    evt.PreventDefault();    // 既定のフォーカス移動を止める
    evt.StopPropagation();
});
```

- `NavigationMoveEvent` は Unity 6000.5.6f1 の `UnityEngine.UIElements` に存在を確認済み
  （`UnityEngine.UIElementsModule.xml`、`Direction.Up/Down/Left/Right/Next/Previous/None`）。
  `EventBase.PreventDefault` も同 XML に存在を確認済み。
- **`notesSheet` にフォーカスがある間だけ**矢印キーを奪う形になるので、
  右パネルのテキスト欄・数値欄でのカーソル移動には影響しない
  （移植候補2を入れたときと同じ理由づけ。editor-ui-rework-mmw.md 実装ログ参照）。
- **←→ も同時に止まる**。現在 ←→ には機能を割り当てていないので、
  「押しても何も起きない」になる。**フォーカスが飛ばなくなるぶん現状より良い**。
  将来 ←→ に cellF 移動を割り当てる余地もここで確保できる。

### 併せて直す点

- **`EnsureCursorVisible`（`:594-598`）の追従が乱暴**。
  `if (cursorTick > L.TopTick || cursorTick < L.BottomTick) scrollTick = cursorTick;`
  は、画面外に出た瞬間に**カーソルを判定線位置へ持ってくる**。参照元の `centerCursor`
  （`ScoreEditor.cpp:382-407`）は**画面の中央付近に来るようスクロール**する。
  1マスずつ動かしているときに画面が丸ごと飛ぶのは読みにくいので、
  **上端/下端に達したら「1画面の 1/4 だけスクロールする」**に変える。
  （参照元の `centerCursor` は mode 引数で「中央 / 上寄せ / 下寄せ」を切り替える設計だが、
  muses は判定線位置が可変なので、シンプルに画面端からの余白を確保する方式にする。）
- **キーリピート**は OS の `KeyDownEvent` 連射に任せる（参照元も同様）。
  フォーカス移動が止まれば長押しは自然に連続する。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.UI.cs:374 付近` | `NavigationMoveEvent` のハンドラ登録を追加 |
| `ChartEditorApp.cs:594-598` | `EnsureCursorVisible` を「画面端に達したら 1/4 画面スクロール」へ |

---

## 6. easing を「横」と「高さ」で独立させる

**唯一データ構造とファイルフォーマットに触る項目。** note-spec.md §1.2 が
「別々に指定したくなったら `easing` を構造体に昇格させれば拡張できる」と**予告していた変更**にあたる。

### 6.1 データ構造

現状の `Waypoint.easing`（`Chart/ChartNote.cs:98`）は
**`layerF` / `cellF` / `width` の3つすべてに同じ easing を適用**している
（`ChartMath.At`、`:185-190`）。

**ユーザー確定の割り当て**:

| フィールド | 支配するプロパティ | 意味 |
|---|---|---|
| `easing`（既存名を維持） | `cellF` / `width` | **横方向**のカーブ |
| `easingH`（新設） | `layerF` | **高さ方向**のカーブ |

- `width` を横に含めるのは、幅の変化が画面上は横方向の動きとして見えるため。
  分けても実益が薄い（幅だけ別のカーブで変えたい場面がほぼ無い）。
- **既存名 `easing` を横用として据え置く**理由は §6.2（互換性）。

```csharp
public struct Waypoint
{
    ...
    /// <summary>この点から次の点までの cellF / width の補間種別。既定 Linear</summary>
    public Easing easing;
    /// <summary>この点から次の点までの layerF（高さ）の補間種別。既定 Linear</summary>
    public Easing easingH;
    ...
}
```

`ChartMath.At`（`ChartNote.cs:171-194`）:

```csharp
float k  = ...;
float e  = Ease(a.easing,  k);   // 横
float eh = Ease(a.easingH, k);   // 高さ
return (
    a.layerF + (b.layerF - a.layerF) * eh,
    a.cellF  + (b.cellF  - a.cellF)  * e,
    a.width  + (b.width  - a.width)  * e
);
```

### 6.2 ファイルフォーマットと互換性

`ChartSerializer` の Waypoint 行は `ease=<name>` オプションで easing を持つ
（`:321`, `:339`）。

**方針**: `ease=` を**横用として据え置き**、高さ用に **`easeh=` を新設**する。

| 状況 | 読み込み時の挙動 |
|---|---|
| `ease=smooth` のみ（既存ファイル） | `easing = Smooth`、`easingH = Smooth`（`ease` を**両方に流用**） |
| `ease=smooth easeh=linear` | `easing = Smooth`、`easingH = Linear` |
| どちらも無い | 両方 `Linear` |

- **既存譜面は完全に互換**。`easeh=` が無ければ従来どおり両軸に同じ easing がかかる。
- 書き出しは `easing != Linear` なら `ease=`、**`easingH != easing` なら `easeh=`** を出す
  （同値なら `easeh=` を省略 = 既存形式と同じ行になる。差分が最小になる）。
- `ParseEasing` / `EasingToStr` は既存のものをそのまま使う（値の集合は変わらない）。

**note-spec.md §1.2 の更新も必要**（「補間は layerF / cellF / width すべてに同じ easing を
適用する（当面）」という記述が古くなる）。rev.5 として反映する。

### 6.3 エディタ UI

- **インスペクタ**（§3 で作り直す点編集行）に `easing`（横）と `easingH`（高さ）の2行を出す。
  ラベルは `easing(横)` / `easing(高さ)` とする。
- **easing 巡回**（editor-ui-rework-mmw.md §5.3、Slide ツールで点をクリック→7種を巡回）は
  **どちらを巡回するか**が問題になる。
  → **クリックした場所で決める**: シート本体（横軸を編集する場所）でのクリックは `easing`、
  **高さレーンでのクリックは `easingH`** を巡回する。
  軸と操作場所が1対1に対応するので覚えやすく、§2 で高さレーンがクリック可能になることとも噛み合う。
  - 高さレーンでの「クリック（ドラッグせず離す）＝ `easingH` 巡回」は新規実装。
    シート本体の `easingCycleCandidate` と同じ仕組み（PointerDown 座標との距離3px で判定）を
    高さレーン側にも用意する。
- **高さレーンのカーブ描画**（`DrawHeightCurve:1120-1122`）が参照する easing を
  `easing` → `easingH` に変える。ここは**間違えると高さカーブの見た目が横の easing に
  引きずられる**ので確実に直す。

### 6.4 波及範囲（実装時に全部当たること）

| ファイル | 内容 |
|---|---|
| `Chart/ChartNote.cs` | `Waypoint.easingH` 追加、`ChartMath.At` の2軸化 |
| `Chart/ChartSerializer.cs` | `AppendWaypointOptions` / `MakeWaypoint` に `easeh` 対応 |
| `Chart/ChartBuilder.cs` | `WP()` ヘルパーに `easingH` 引数（既定は `easing` と同値）。`:89` の層跨ぎ Slide はここで挙動維持 |
| `ChartEditorApp.cs` | `NewWaypoint`（`:2349`）の初期化、高さレーン描画の easing 参照、easing 巡回の分岐 |
| `ChartEditorApp.UI.cs` | インスペクタに2行 |
| `Notes/NoteGeometry.cs` | `ChartMath.At` 経由なので**変更不要**（`:36` のコメントのみ確認） |
| `memory/note-spec.md` | §1.1 の構造体定義と §1.2 の記述を rev.5 として更新 |

`Chart/ScrollTimeline.cs` の `easing` は**ソフラン用で無関係**（`ScrollEvent.easing`）。触らない。

---

## 7. 右クリックメニューの常設項目

### 現状

`OnSheetRightClick`（`ChartEditorApp.cs:1536-1586`）は3通りに分岐する:

| 右クリック位置 | 現在のメニュー |
|---|---|
| ノーツの点の上 | 削除（＋単発ノーツなら種別変更3項目） |
| Slide の帯の上 | 「ここに中継点を追加」のみ |
| **それ以外（空白）** | **何も出ない** |
| イベントレーン / 高さレーン | 何も出ない（`:1540-1541` で即 return） |

ユーザー指摘のとおり、**空白で右クリックしても何も起きない**のが最大の問題。

### 参照元

`ScoreEditor::contextMenu()`（`EditorWindows.cpp:146-198`）は
**`BeginPopupContextWindow` でタイムライン全体に1つのメニューを張る**（＝位置で分岐しない）:

```
Delete       (Delete)         有効条件: hasSelection
---
Copy         (Ctrl + C)       有効条件: hasSelection
Paste        (Ctrl + V)       有効条件: hasClipboard()
Flip Paste   (Ctrl+Shift+V)   有効条件: hasClipboard()
Flip         (Ctrl + F)       有効条件: hasSelection
---
Ease Type  ▸  (Linear / Ease In / Ease Out)      有効条件: hasSelectionEase
Step Type  ▸  (Visible / Invisible / Ignored)    有効条件: hasSelectionStep
```

**実行できない項目は消さずグレーアウトする**（`ImGui::MenuItem(..., false, hasSelection)` の
第4引数が enabled）。ユーザーの選択（「常に全項目を表示し、不可能なものは無効表示」）と一致する。

### 方針

**位置による分岐をやめ、「常設ブロック」＋「文脈ブロック」の2段構成にする。**

```
─ 常設（位置に関わらず必ず出る。順序も固定） ────────
  削除                    Delete          有効: selection.Count > 0
  切り取り                Cmd/Ctrl+X      有効: selection.Count > 0
  コピー                  Cmd/Ctrl+C      有効: selection.Count > 0
  貼り付け                Cmd/Ctrl+V      有効: clipboard.Count > 0
  反転して貼り付け        —               有効: clipboard.Count > 0
  選択を反転              —               有効: selection.Count > 0
─ 文脈（右クリック位置で内容が変わる。無ければブロックごと省略）─
  [点の上]   Tapに変更 / Ex Tapに変更 / Flickに変更   （単一選択の単発ノーツのみ）
  [帯の上]   ここに中継点を追加
```

- **`GenericDropdownMenu.AddDisabledItem(string, bool)`** で無効項目を出す
  （Unity 6000.5.6f1 に存在を確認済み）。
- **ショートカット表記をラベルに含める**（`削除    Delete` のように）。
  参照元と同じく、メニューがショートカットの一覧を兼ねる。
  `GenericDropdownMenu` にショートカット表示欄は無いので、ラベル文字列にタブ相当の空白で埋める。
- **項目位置が常に一定**になるので、マッスルメモリで操作できる（無効表示を選んだ理由そのもの）。
- **「削除」のラベルは文脈で変える**現行の工夫（`:1553`、`選択した3件を削除` /
  `この中継点を削除`）は**維持する**。位置は固定のまま文言だけ変わる。
- **右クリック位置での選択の切り替え**（`:1548`、未選択の点を右クリックしたら単一選択にする）は
  **維持する**。参照元は選択を変えないが、muses は「右クリックした点を対象に削除したい」が
  自然なので、こちらの挙動のほうが良い。
- **空白での右クリックは選択を変えない**（貼り付け先を選ぶ用途で右クリックすることがあるため）。
- **貼り付け位置**: メニューから「貼り付け」を選んだ場合も §1 のペーストモードに入る
  （`EnterPasteMode`）。`EnterPasteMode` は `sheetHoverPos` を基準にするので、
  **メニューを閉じた後のカーソル位置**が基準になる。右クリックした位置で確定したいときは
  そのままクリックすればよい。
- **イベントレーン・高さレーンでの右クリック**は現状どおり何も出さない
  （そこにあるのはノーツではないので、常設項目の対象が無い）。
  ただし**高さレーンでは §6.3 の `easingH` 巡回**が入るので、将来
  「この点の高さ easing を選ぶ」サブメニューを足す余地はある。今回は入れない。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1536-1586` | `OnSheetRightClick` を2段構成へ全面的に書き直す |

---

## 8. 実装順

依存関係と「壊れたときの切り分けやすさ」で並べる。

1. **§5 ↑↓キーの不具合**（数行・独立・明確な不具合。最初に潰す）
2. **§1 中継点の常時描画**（描画のみ。§2/§3 で点を掴む前提が整う）
3. **§2 高さレーンからの選択**（§1 で点が見えるようになってから）
4. **§7 右クリックメニュー**（独立。既存機能を並べ替えるだけ）
5. **§4 ペーストとドラッグの横方向クランプ**（独立）
6. **§3 インスペクタの単一点化**（§6 でここに easing が2行入るので、§6 の直前に置く）
7. **§6 easing の2軸化**（データ構造＋フォーマット＋spec 更新。最後に単独で）

**§6 だけは他と混ぜずに1コミットにする。** 譜面ファイルの読み書きに触るので、
問題が出たときに切り戻しやすくしておく。

---

## 9. 実装前に決めきれていない点（実機で見てから）

1. **§2 の非選択カーブの α = 0.28** は暫定値。同時押しが多い譜面で「選択中がどれか
   分からない」なら下げる。逆に「非選択が見えなくて掴めない」なら上げる。
2. **§5 の「1/4 画面スクロール」の割合**も暫定。参照元の `centerCursor` は中央寄せなので、
   1/2 のほうが自然に感じる可能性がある。
3. **§3 で Foldout を全廃すると、`marker` や `comboStep` といった使用頻度の低い項目も
   常に見えることになる**。圧迫が気になるようなら「詳細」の Foldout を1つだけ作って
   そこへ退避する（位置 / cellF / width / easing×2 は常時、marker / comboStep は詳細）。
   実機で行数を見てから判断する。

---

## 関連

- `memory/editor-ui-rework-mmw.md` — 前段。§1〜§7・移植候補8件（実装済み・実機確認済み）。
- `memory/editor-ui-redesign.md` — その前段。§1〜§7（実装済み）。
- `memory/note-spec.md` — ノーツ仕様 rev.4。**§6 で rev.5 へ更新が必要**。
- `memory/editor-spec.md` — Phase 4 機能仕様 rev.2。§2 のレイアウト図は既に古い。
- `memory/reference/MikuMikuWorld-master/` — 参照元ソース。
