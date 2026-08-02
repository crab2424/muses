# 譜面エディタ UI 改修設計 第3弾（実機フィードバック、2026-08-02 rev.1）

`memory/editor-ui-rework-r2.md` の §1〜§7 を実装したビルド（コミット `9dda4b1`）をユーザーが実機で
確認して出た指摘への設計。**コンパイルエラーは無し**（ユーザー確認済み。r2 の申し送りだった
「Unity Editor未検証」はこれで解消）。

内訳は **r2 の積み残し4件**（S1 / S2 / S4 / S6）と **新規4件**（P1〜P4）。
r2 §7（右クリックメニュー）と §3（インスペクタ単一点化）は**問題なしと確認済み**。

**このドキュメントは実装計画。ユーザーの確認後に実装へ入る。**

- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（2612行）、
  `ChartEditorApp.UI.cs`（1671行）、`PreviewSystem.cs`（436行）。
- 今回は**エディタ外（`Notes/NoteGeometry.cs`・`Gameplay/Judge.cs`・`Chart/ChartValidator.cs`）にも
  波及する項目がある**（§5）。r2 §6 の `easingH` 追加以来の、譜面データ意味論に触る変更。
- Unity 6000.5.6f1。参照元は `memory/reference/MikuMikuWorld-master/`（C++/ImGui/OpenGL）。

---

## 0. 指摘一覧と対応

| # | 指摘 | 節 | 影響範囲 | 状態 |
|---|---|---|---|---|
| S1 | Ground/Sky 側の中継点の四角が小さい。黄枠と同じくらいに、ただし重ならないよう | §1 | 描画のみ | 方針確定 |
| S2 | 高さレーンの未選択ノーツが灰色のまま | §2 | 描画のみ | 方針確定 |
| S6 | 高さレーンだけ、通常選択ツールでも easing が切り替わる | §3 | 入力 | 方針確定 |
| S4 | 座標が飛ぶのが直っていない／Sky→Ground へ移せない | §4 | 入力 | 方針確定 |
| P1 | プレビューの Sky ノーツの横位置がずれている（0.5〜2マス） | §5 | **データ意味論** | 方針確定 |
| P2 | プレビューにも判定線を描きたい | §6 | 描画（新規） | 方針確定 |
| P3 | 配置ツールでノーツ/中継点の上をクリックしたら、置かずに選択する | §7 | 入力 | 方針確定 |
| P4 | タイムラインとプレビューの時間位置を同期する | §8 | 状態同期 | 方針確定 |

**§5 だけが譜面データの意味（`cellF` の基準）に触る**ので、他と切り離して単独コミットにする（§9）。

### 0.1 ユーザー確定事項（2026-08-02、着手前に確認済み）

- **S4 の切り分け**: 飛ぶのは**ドラッグ移動**のときで、**対象は Slide だけ**。
  「Ground へ移せない」のも **Slide**。→ §4.1 で原因を `forceSky` の途中反転と特定した。
- **P1 の統一先**: `cellF` は**左端に統一**（案A）。ゲーム側3実装（`NoteGeometry` /
  `Judge` / `ChartValidator`）の Slide 中心基準をエディタに合わせて直す。
- **P4 の同期元**: **`scrollTick`（判定線）**。停止中もホイールでスクロールすれば
  プレビューが追従する（再生中の「判定線＝現在時刻」と意味を揃える）。
- **P3 の Slide 2点目待ち**: 既存ノーツの上でも **Slide を完成させる**（その場合だけ横取りしない）。

---

## 1. 中継点グリフを「ノーツ幅の帯」にする（S1）

### 現状

`DrawWaypointGlyph`（`ChartEditorApp.cs:869-874`）は **`cellF` の位置に 6×6px の正方形**を描く。

```csharp
var r = new Rect(x - 3, y - 3, 6, 6);
if (marker == WaypointMarker.Visible) FillRect(p, r, color);
else FillRectOutline(p, r, color, 1f);
```

これを**シート本体（`:1010`, `:1039`）と高さレーン（`:1186-1196`）の両方**が使っている。
高さレーンは点が線の上の位置を示すだけなので 8×8 の正方形で適切だが、シート本体では

- 中継点が「幅を持つ帯の一部」であることが読めない（`width` が全く反映されない）
- 選択の黄枠（`:1065`、`Rect.MinMaxRect(x0-3, y-6, x1+3, y+6)` = ノーツ幅+6px × 12px）に対して
  6×6 は明らかに小さく、同じ画面上で粒度が揃っていない

というのがユーザー指摘（添付スクリーンショット）の中身。**高さレーン側は問題なし**とのことなので、
**シート本体側だけを変える**。

### 参照元

`ScoreEditor::drawHoldNote`（`TimelineNotes.cpp:284-287`）は `drawHoldStepOutline` が真なら
中継点に `drawHighlight(..., mid: true)` を描く。この `drawHighlight`（`:332-370`）は
**`laneWidth * note.width` の長さを持つ 9-slice スプライト**、つまり
**ノーツ幅いっぱいに広がる薄い枠**であって、点ではない。高さは `HIGHLIGHT_HEIGHT`
（本体ノーツの `notesHeight + 5` より低い）で、**本体より薄い帯**として区別している。

muses の「始点/終点は `DrawEndpointGlyph` で幅いっぱいの 8px 帯」（`:856-865`）と
発想が同じなので、**中継点も同じ土俵に載せる**のが素直。

### 方針

`DrawWaypointGlyph` を**シート本体用**と**高さレーン用**に分ける。

| | 形 | 寸法 | Visible | None / Invisible |
|---|---|---|---|---|
| シート本体（新） | ノーツ幅の帯 | `x0..x1` × `y±3`（高さ6px） | 塗りつぶし | 輪郭のみ(1px) |
| 高さレーン（現状維持） | 正方形 | 8×8 | 塗りつぶし+中心色 | 輪郭のみ(1px) |

- **幅は `wp.cellF` 〜 `wp.cellF + wp.width`**。`DrawEndpointGlyph` と同じ求め方
  （`NoteX(layerF, cellF, forceSky)` と `NoteX(layerF, cellF + width, forceSky)`）にして、
  始点/終点/中継点で横位置の規則を完全に揃える。
- **高さは 6px（`y±3`）**。始点/終点の 8px（`y±4`）よりわずかに薄くして、
  参照元と同じく「端点 > 中継点」の階層を付ける。
- **黄枠との非重なり**: 黄枠は `y±6`・`x0-3..x1+3`。中継点帯は `y±3`・`x0..x1` なので
  **上下に3px、左右に3px の余白**が残り、枠の内側に収まる。現状の 6×6 正方形と同じ余白量なので、
  選択の視認性は落ちない。
- 端点の描き分け（`marker` を見ずに常に塗りつぶし）は r2 §1 のまま維持する。
- 濃淡（`forceSky` 時の `HeightAlpha`）も現状のまま渡す。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:869-874` | `DrawWaypointGlyph` を「矩形を受け取って marker で塗り/輪郭を分岐する」形に一般化 |
| `ChartEditorApp.cs:1005-1011` | forceSky 側: `x` 1点でなく `x0..x1` を渡す |
| `ChartEditorApp.cs:1034-1040` | 通常側: 同上 |
| `ChartEditorApp.cs:1177-1197` | 高さレーン: 8×8 正方形のまま（呼び出し形だけ合わせる） |

---

## 2. 高さレーンの未選択カーブをノーツ色にする（S2）

### 現状

`DrawHeightLane`（`:1130-1145`）は未選択ノーツを `new Color(1f, 1f, 1f, 0.28f)`、
つまり**白28%＝灰色**で描く。r2 §2 の設計は「非選択: カーブ α=0.28 程度」と
**α しか決めておらず色相を決めていなかった**ため、実装が白を選んだ。

r2 §2 で「全ノーツを常時表示し、クリックで選択できる」に変えた結果、
**高さレーンは選択のための一次的な画面になった**。そこで種別が読めないと、
同時押しで複数のカーブが重なったときに「どれが Slide でどれが Tap か」が分からず、
掴む前の当たりが付けられない。灰色は「触れないもの」の色に見えてしまう、というのが指摘の中身。

### 方針

- **未選択も `NoteColor(note.kind)` を使い、α だけ 0.28 にする**（色相で種別、α で選択状態）。
- **選択中は現状どおり α=1**。r2 §9-1 で「0.28 は暫定値」としていたが、色相が付いて
  区別しやすくなるぶん**下げる余地がある**。まず 0.28 のまま実機で見て、
  うるさければ 0.20 まで落とす（値だけの調整なので後追いでよい）。
- 点の輪郭色（`DrawHeightCurve:1194` の `selected ? Color.white : col`）は既に `col` を使うので、
  `col` がノーツ色になれば自動的に追従する。**選択中の白／非選択の種別色**という対比になり、
  「選択中＝白い輪郭」が現状より際立つ。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1139` | 未選択の色を `new Color(1,1,1,0.28f)` → `NoteColor(note.kind)` の α=0.28 版へ |

---

## 3. 高さレーンの easing 巡回を Slide ツール限定にする（S6）

### 症状と原因

ユーザー報告: 「slide 選択時にクリックで切り替わるのは想定通り。しかし通常選択時にも、
**高さレーンに限り** easing が切り替わってしまう」。

シート本体の easing 巡回は `EditorTool.Slide` の case の中だけで仕込まれる
（`easingCycleCandidate` の設定は `:1458`、Slide ツールのブロック内）。
一方 `HandleHeightLanePointerDown`（`:1715-1743`）は **PointerDown の分岐で
`currentTool` を見る前に処理されており**（`:1387-1392`）、`heightEasingCycleCandidate` を
**ツールに関わらず**設定してしまう（`:1741`）。

```csharp
// :1385-1392
// 「ノーツの配置ではなく既存の値の編集なので、どのツールを選んでいても同じ挙動にする」
if (L.heightLane.width > 0f && L.heightLane.Contains(pos))
{
    HandleHeightLanePointerDown(L, pos, evt);   // ← ここで currentTool を見ていない
```

このコメント自体は **layerF のドラッグ編集**については正しい（高さレーンにノーツは置けないので、
どのツールでもドラッグ編集を許すのが自然）。**easing 巡回だけがツール依存**であるべきなのに、
そこが分離されていなかった。

### 方針

`heightEasingCycleCandidate` の設定を **`currentTool == EditorTool.Slide` のときだけ**にする。

```csharp
heightEasingCycleCandidate = currentTool == EditorTool.Slide
                             && index < note.points.Count - 1 ? hit : (NoteRef?)null;
```

- **ドラッグ編集・クリックによる選択はツール非依存のまま**。変わるのは「クリックだけして離した
  ときに `easingH` が1つ進むかどうか」だけ。
- r2 §6.3 の「シート本体のクリックは `easing`、高さレーンのクリックは `easingH`」という
  **軸と場所の対応は維持**される。Slide ツールでのみ両方が効く、という形に揃う。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1739-1741` | `heightEasingCycleCandidate` の設定条件に `currentTool == Slide` を追加 |

---

## 4. ドラッグ中の座標飛びと Sky→Ground（S4）

r2 §4 で `TryPaneAt`（ガター上で null）＋直前有効値の保持を入れたが、
ユーザー報告は「**座標は飛んだまま**」。追加の切り分けで

- 飛ぶのは**ドラッグ移動**、**対象は Slide だけ**
- 「Ground へ移せない」のも **Slide**

と分かった。**両方とも同じ1つの原因**である。

### 4.1 原因: `forceSky` がドラッグ中に反転する

editor-ui-rework-mmw.md §4 で入れた規則:

```csharp
// ChartEditorApp.cs:723-724
public float NoteX(float layerF, float cellF, bool forceSky) =>
    forceSky ? CellX(sky, cellF) : CellX(layerF >= 0.5f ? sky : ground, cellF);
```

`forceSky` の実体は `HasHeightVariation(note)` ——**「そのノーツの waypoint 間で layerF が
変化するか」というノーツ全体の性質**で、描画のたびに現在のデータから計算される（`:968`, `:1062`,
`:1796`, `:2500`）。

そのため **Slide の一部の点だけをドラッグして層をまたいだ瞬間に、ノーツ全体の描画先ペインが
切り替わる**。

| 操作 | `forceSky` | 見え方 |
|---|---|---|
| Ground の Slide（全点 layerF=0）の**1点だけ**を Sky ペインへドラッグ | `false` → `true` | **ノーツ全体が Ground ペインから Sky ペインへ一気に移る**（ペイン幅+ガター ≒ 500px の瞬間移動）＝ **「座標が飛ぶ」** |
| Sky の Slide（全点 layerF=1）の**1点だけ**を Ground ペインへドラッグ | `false` → `true` | `NoteX` は元々 Sky を返していたので **x が一切変わらない**（濃淡が変わるだけ）＝ **「Ground に移動できない」** |
| **全点**を選択してドラッグ | 変化なし（全点が同じ delta で動く） | ペインをまたいでもカーソルに正しく追従する。**現状でも問題なし** |
| 単発ノーツ（Tap/ExTap/Flick） | 常に `false`（点が1つなので変化しようがない） | **問題なし**。ユーザー報告が「Slide だけ」なのはこのため |

r2 §4 で入れた `TryPaneAt` / `ResolveCellDelta` / `ResolveLayerDelta` は
**ガター越えと盤面外クランプを正しく直しており、そこに残バグは無い**。飛びの原因は別だった。

**ペイン間をまたぐと `cellF` の差分が ±`Cells` 不連続になる点**（Ground の右端 12 と Sky の左端 0 は
画面上 26px 隣）は、**画面上はカーソルに正しく追従しているので不具合ではない**。修正しない。

### 4.2 方針

**シート本体のドラッグでは `forceSky` が絶対に反転しないようにする。**
高さ（`layerF`）の編集は高さレーンの担当、という editor-ui-redesign.md §7.5 /
r2 §2 の役割分担をシート本体側にも徹底する形になる。

**規則1: シート本体のドラッグで `layerF` を変えられるのは「そのノーツの全点が選択されている」ときだけ。**

- 単発ノーツは点が1つ＝常に全点なので、**従来どおり Ground↔Sky を自由に行き来できる**（回帰なし）。
- Slide を丸ごと選択してドラッグ → 全点が同じ `deltaLayer` で動くので `forceSky` は不変。
  **「Sky の Slide を Ground へ移す」はこの操作で行える**（現状でも正しく動く）。
- Slide の一部の点だけを選択してドラッグ → `deltaLayer = 0` に固定。層は動かない。
- 層をまたぐ Slide を丸ごと選んだ場合は `ResolveLayerDelta` が `minLayer=0 / maxLayer=1` で
  `d ∈ [0,0]` を返すので**元から動かない**（形を保つための当然の帰結。維持する）。

**規則2: 層を変えられないドラッグでは、有効なカーソル領域を「ドラッグ開始時のペイン」だけに限る。**

- `TryPaneAt` の戻り値が開始時と別のペインなら **null 扱い**にして `dragLastValid*` を更新しない。
  ガター・左右余白・高さレーンで既にそうしているのと同じ扱いにするだけ。
- ユーザー要望「**端より外側からはどこも移動できないようにし、戻す時はカーソル追従する**」が
  そのまま満たされる（外に出ている間は固定、ペインへ戻った瞬間からその位置に追従）。
  delta 方式なので**戻ったときのずれ（ドリフト）は発生しない**。
- 層を変えられるドラッグ（＝全点選択 or 単発ノーツ）では、従来どおり両ペインが有効。

**規則3: 混在選択の扱い。** 選択が複数ノーツにまたがり、**一部でも「全点が選択されていない
ノーツ」を含むなら、選択全体で層固定**（規則1・2を適用）とする。ノーツごとに層が動いたり
動かなかったりすると結果が読めないため、安全側に倒す。

**あわせて直す（ペースト側の残バグ）**: `EnterPasteMode`（`:2226`）だけ `PaneAt` のままで、
ガター・余白・高さレーン上で Cmd/Ctrl+V を押すとアンカーが `(layerF=0.5, cellF=6)` という
実在しない値に固定される（`PaneAt` の戻り値、`:733`）。その後カーソルをペインへ入れた瞬間に
最大6セルぶんゴーストがずれる。**`TryPaneAt` に変え、レーン外なら
ペーストモードに入らない**（`statusMessage` で「レーン上にカーソルを合わせてから」と促す）。

### 4.3 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1547-1562` | `BeginPointDrag`: 「層を変えてよいドラッグか」(`dragCanChangeLayer`) と開始ペインを決めて保持 |
| `ChartEditorApp.cs:1864-1879` | `OnSheetPointerMove`: 開始ペイン以外を null 扱い、`dragCanChangeLayer` が false なら `deltaLayer = 0` |
| `ChartEditorApp.cs:2217-2234` | `EnterPasteMode`: `PaneAt` → `TryPaneAt`、レーン外なら入らない |

---

## 5. プレビューの Sky ノーツ横位置ずれ（P1）

### 5.1 原因: `cellF` の基準がエディタとゲーム側で違う

ユーザー報告「0.5〜2マス、左から右に向かって線形的に段々左にずれる」の主因を特定した。

**`Waypoint.cellF` が「帯の左端」なのか「帯の中心」なのかが、実装によって食い違っている。**

| 実装 | Tap / Ex Tap / Flick | **Slide** |
|---|---|---|
| **エディタ**（`ChartEditorApp.cs:723-724, 959-961, 1025-1026`） | **左端** | **左端** |
| `Notes/NoteGeometry.cs:111-112, 128-129, 205` | 左端 | **中心**（`cellF ± width/2`） |
| `Gameplay/Judge.cs:182-194, 173-175` | 左端（`cell..cell+w`） | **中心**（`cellF ± width/2`） |
| `Chart/ChartValidator.cs:71-73, 121-128` | 左端 | **中心** |

つまり**ゲーム側3実装は「Slide だけ中心基準」で一貫しており、エディタだけが全種別で左端**。
結果、エディタで置いた Slide はプレビューで **`width/2` セルぶん左にずれる**。
`width` は 1〜4 セルで運用しているので **ずれ量 0.5〜2マス**——ユーザーの報告値と一致する。

「Sky のノーツ」に見えたのは、editor-ui-rework-mmw.md §4 により
**層をまたぐ Slide は Sky ペインにしか描かれない**ため。ずれているのは
「Sky のノーツ」ではなく「Slide」である可能性が高い（§5.3 で確認する）。

`ChartBuilder` のデモ譜面（`:98-113`）が Slide の `cellF` に `1.5 / 4.5 / 7.5 / 9.5 / 10.5` と
半端値を使い、幅を `1.2 * S` にしているのも中心基準の名残（web-prototype 時代の Arc が
中心基準だった）。

**なお `Judge.cs:161` の `CellOverlap` だけは Slide にも左端基準の式を使っており、
ゲーム側の中でも不整合がある**（同時押し判定に使われる）。どちらへ寄せるにせよ、ここも直す。

### 5.2 「線形的に段々左」という見え方について

`width/2` は定数なので、幅が一定なら**ずれ量も一定**になるはず。「左から右に向かって
段々」に見える理由として考えられるのは:

- **テスト譜面の幅が右へ行くほど広い**（ずれ量 = width/2 なので幅に比例する）
- **ステージが台形（遠近法）なので、同じセル数のずれでも画面上の px 量が場所で変わる**
- **`width/2` 以外にスケール方向の誤差も別に存在する**

3つ目の可能性は、シェーダ（`Shaders/Include/NotePlacement.hlsl` の `PlaceNote`）と
ステージ側（`StageDerive.LaneX`）の式を突き合わせて**完全に同一**であることを確認済み
（`laneK` / `zcFarGround` / `laneConverge` の扱いまで一致、uniform も `NoteView.ApplyStaticUniforms`
で正しく渡している）。したがって**スケール方向の誤差は無い**と判断している。
§5.3 で「幅を変えるとずれ量が変わるか」を確認したい。

### 5.3 方針: **左端に統一**（ユーザー確定）

ゲーム側3実装の Slide を**左端基準へ直し、エディタに合わせる**。

**この向きにする理由**:
- `cellF + width` が常に右端になり、`FlipCellF`（`:2261`）・矩形選択・ヒットテスト・
  `ResolveCellDelta` のクランプ（`cellF ∈ [0, Cells - width]`）が**全種別で1つの式**になる。
- 中心基準は「幅を変えると左右へ同時に広がる」ため、r2 §7.4-D の端ドラッグ幅変更
  （左端基準前提）と噛み合わない。
- 逆向き（エディタを中心基準へ）にすると、`NoteX` / `HitTestPoint` / `HitTestPointsInRect` /
  `EdgeGrabSign` / `ResolveCellDelta` / `FlipCellF` / ゴースト描画のすべてに
  「種別で基準が違う」分岐が入る。

**既存データの移行**: リポジトリ内に `.muses` ファイルは無い。
`ChartBuilder` のデモ譜面だけ `+width/2` すれば見た目が保たれる。
**ユーザーの手元（`Application.persistentDataPath`）に保存済みの譜面がある場合は、
Slide 行の `cellF` を `+width/2` する一度きりの変換が必要**（§10 で確認）。
必要なら変換スクリプトを用意する。

### 5.4 変更箇所

| ファイル | 変更 |
|---|---|
| `Notes/NoteGeometry.cs:128-129` | Visible 中継点マーカーを左端基準へ |
| `Notes/NoteGeometry.cs:205` | `PushSlideBand` の `Emit` を `cellF` 〜 `cellF + width` へ |
| `Gameplay/Judge.cs:161, 173-175, 186-187` | `InBand` / `Contains` / `CellOverlap` を左端基準へ統一 |
| `Chart/ChartValidator.cs:71-73, 121-128` | 種別分岐を削除 |
| `Chart/ChartBuilder.cs:85-115` | デモ譜面の Slide `cellF` を `+width/2` |
| `memory/note-spec.md` | §1.1 に「`cellF` は帯の左端」を明記（現状どこにも書かれていない）。rev.6 |

---

## 6. プレビューへの判定線描画（P2）

### 現状

ゲーム本体は `Overlay/StageOverlay.cs` が判定帯・判定線・セル境界を**スクリーン空間**で描く
（`GL` immediate mode + `Screen.width/height`、`:71-73` の `PxX/PxY/CellU`）。
一方 `PreviewSystem.BuildRig()`（`:75-144`）は Camera / StageView / NoteView しか組み立てず、
**`StageOverlay` を載せていない**。載せても `Screen.width/height` 基準なので
オフスクリーン RenderTexture には合わない。

### 方針

**`StageOverlay` は流用せず、`previewSurface`（`ChartEditorApp.UI.cs:386-391`）の
`generateVisualContent` に Painter2D で描く。**

- 判定線は**完全にスクリーン空間（NDC）で決まる**——`cfg.vGroundJudge` / `cfg.vSkyJudge` が
  そのまま画面上の縦位置、横は `-cfg.U`〜`cfg.U`（`StageOverlay.DrawBand:141-142` と同じ）。
  3D の投影計算は一切要らない。
- 要素ローカル座標への写像は
  `x = (u + 1) / 2 * w`、`y = (1 - v) / 2 * h`（UI Toolkit は y 下向きなので `PxY` の符号を反転）。
- RenderTexture は `previewSurface` の実サイズちょうどで作られ（`UpdatePreviewTexture:557-563`）、
  背景画像として引き伸ばし無しで貼られるので、**この写像だけで画素が一致する**。

**描く内容**（`StageOverlay` の簡略版。エディタで必要なものだけ）:

| 要素 | 色 | 根拠 |
|---|---|---|
| Ground 判定線 | `StageColors.Ground` | `StageOverlay:187-193` |
| Sky 判定線 | `StageColors.Sky` | 同上 |
| 判定帯の上下端（`vGroundTop/Bot`・`vSkyTop/Bot`） | 各層色 α=0.5 | `:180-184`。**任意**（§6 の確認事項） |
| セル境界の縦線 | 各層色 α=0.38 | `:175-179`。**任意** |

まず**判定線2本だけ**を入れ、帯・セル境界は「表示」メニューのトグルとして後追いする
（ユーザー要望は「判定線の描画」なので、最小で入れて実機で見てから足す）。

**必要な準備**: `PreviewSystem.cfg` は `private readonly`（`:32`）なので、
`public StageConfig Config => cfg;` を足して UI 側から読めるようにする。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `PreviewSystem.cs:32` | `Config` プロパティを公開 |
| `ChartEditorApp.UI.cs:386-391` | `previewSurface.generateVisualContent` を登録 |
| `ChartEditorApp.cs`（新規メソッド） | `GeneratePreviewOverlay(MeshGenerationContext)` |

---

## 7. 配置ツールでの暴発防止（P3）

### 要望

> ノーツ追加モード時、クリック位置にノーツまたは中継点（帯除く）が存在した場合、
> 暴発を避けるためそのノーツを選択する挙動に変更する。この時、モードまでは選択にはならない。

### 現状

`OnSheetPointerDown` の `switch (currentTool)`（`:1396-1541`）:

| ツール | 既存ノーツの点の上をクリックしたとき |
|---|---|
| `Tap` / `ExTap` / `Flick` | **無条件で新規配置**（重なって置ける） |
| `Slide` | **Slide の点なら**選択＋ドラッグ、それ以外の種別なら新規配置（`:1451-1460`） |
| `AddWaypoint` | 選択中 Slide への中継点挿入のみ |
| `Delete` | ヒットした点を削除 |
| `Select` | 選択＋ドラッグ |

つまり **r2 §5.3 で Slide ツールにだけ入れた「点の上なら配置しない」を、
配置ツール全体へ広げる**というのが今回の要望。

### 方針

`Tap` / `ExTap` / `Flick` / `Slide` の各 case の先頭で共通に:

```csharp
var hit = HitTestPoint(L, pos);      // 帯は対象外（HitTestPoint は点のみ、:2495-2513）
if (hit.HasValue)
{
    var hn = hit.Value;
    if (evt.shiftKey) ToggleSelectionMembership(hn);
    else if (!selection.Contains(hn)) SetSingleSelection(hn);
    if (selection.Contains(hn)) BeginPointDrag(rawTick, rawCell, layerF, pos, evt);
    // currentTool は変えない（＝「モードまでは選択にはならない」）
    break;
}
```

- **`HitTestPoint` は点だけを見る**（帯へのヒットテストは `HitTestBand` 系が別にあり、
  右クリックメニューの「ここに中継点を追加」でしか使わない）。要望の
  「（帯除く）」はそのまま満たす。
- **ドラッグまで許すか**は Slide ツールの既存挙動（`:1456`）に揃えて**許す**。
  「掴んで動かせないと、選択できても結局ツールを切り替える羽目になる」ため。
- **`currentTool` は変更しない**。要望の「モードまでは選択にはならない」の明示。
- **easing 巡回は Slide ツールのままに限定**（`easingCycleCandidate` は Slide の
  case でのみ設定する現状を維持）。他ツールでの誤爆を避ける。
- **Slide ツールで1点目待ち（`pendingSlideStart != null`）のときは、この横取りを行わない**
  （ユーザー確定）。途中まで置いた Slide を完成させたいのに、終点の位置にたまたま
  既存ノーツがあると完成できなくなるため。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:1396-1491` | 配置系4ツールの case 先頭に共通の横取り処理を挿入 |

---

## 8. タイムラインとプレビューの時間同期（P4）

### 現状

3つの時間位置が別々に動いている。

| 値 | 意味 | 実装 |
|---|---|---|
| `scrollTick` | 判定線（赤）に来る tick ＝**タイムラインの表示位置** | ホイール `:2014`、再生追従 `:326-339` |
| `cursorTick` | 再生位置カーソル（橙）＝**次に再生を始める位置** | 空白クリック `:1925`、↑↓キー `:2057-2058` |
| `preview.SongTime` | **3Dプレビューが映している時刻** | `preview.Seek` を明示的に呼んだときだけ動く |

**再生中**は `preview.SongTime` が真の値で、`scrollTick` も `cursorTick` もそこへ追従する
（`:326-346`）ので3つが一致する。**停止中は完全に切れている**——ホイールでスクロールしても、
カーソルを動かしても、プレビューは `Seek` が呼ばれるまで前の時刻を映したまま。
`preview.Seek` を呼ぶのは トランスポートの4ボタン（`UI.cs:1161-1168`）とスクラブスライダー
（`:1206`）だけ。

要望「タイムラインとプレビューの時間位置を同期する」はこの断絶のこと。

### 方針（案）

**停止中は「タイムライン側が真の値」として、そこから `preview.Seek` を1方向に駆動する。**
再生中は現状どおり `preview.SongTime` が真の値（逆方向）。役割の切り替えは
r2 §3 の `cursorTick` / `SongTime` の切り替え（`:342-346`）と同じ考え方で、既存の構造に乗る。

**同期元は `scrollTick`（判定線）**（ユーザー確定）。判定線は再生中「今の時刻」を指しているので、
停止中も同じ意味にするほうが一貫する。ホイールで曲を巻き戻し／早送りする感覚になり、
プレビューを見ながらの譜面確認がしやすい。

```csharp
// Update() 内、followPlayback の直後
if (!preview.IsPlaying)
{
    float want = TickToSeconds(scrollTick);
    if (Mathf.Abs(preview.SongTime - want) > 1e-3f) preview.Seek(want);
}
```

- `preview.Seek` は `clock.Seek` + `judge.Seek` + `noteView.UpdateScroll` + `MarkDirty` だけで
  再構築を伴わない（`PreviewSystem.cs:363-370`）ため、**毎フレーム条件付きで呼んでも軽い**。
  実際に再描画されるのは `sceneDirty` が立ったときだけ（`MaybeRender:344-355`）。
- **`cursorTick`（橙、再生開始位置）はこれまでどおり独立**。停止中の役割分担は
  「判定線＝いま見ている時刻」「橙カーソル＝▶を押したら再生を始める位置」になる。
- スクラブスライダー（`UI.cs:1206-1210`）は `Seek` と `cursorTick` を動かすが `scrollTick` は
  動かさない。同期方向が逆になって**引っ張り合う**ので、**スクラブ時は `scrollTick` も
  合わせる**（ドラッグ中はスライダーが真の値、離したら `scrollTick` 起点に戻る）。
- ▶/■/|◀/▶| のトランスポート（`UI.cs:1161-1168`）も同じ理由で `scrollTick` を合わせる。
  停止中に `preview.Seek` を呼ぶ箇所は**すべて `scrollTick` も更新する**、を規則にする。

### 変更箇所

| ファイル:行 | 変更 |
|---|---|
| `ChartEditorApp.cs:326-346` | 停止中の逆方向同期（`scrollTick` → `preview.Seek`）を追加 |
| `ChartEditorApp.UI.cs:1161-1168, 1206-1210` | トランスポート/スクラブで `scrollTick` も合わせる |

---

## 9. 実装順

1. **§3** 高さレーンの easing 巡回をツール限定に（数行・独立・明確な不具合）
2. **§2** 高さレーンの未選択色（1行）
3. **§1** 中継点グリフの拡大（描画のみ）
4. **§4** ドラッグの層固定と `EnterPasteMode` の残バグ（入力・独立）
5. **§7** 配置ツールでの暴発防止（入力・独立）
6. **§6** プレビューの判定線（新規描画・独立）
7. **§8** タイムライン⇔プレビューの時間同期（状態同期・独立）
8. **§5** `cellF` 基準の統一（**単独コミット**。譜面データの意味に触るため切り戻せるようにする）

---

## 10. 残る確認事項

| # | 節 | 内容 |
|---|---|---|
| Q1 | §5.3 | **手元（`Application.persistentDataPath`）に保存済みの `.muses` 譜面があるか**。あれば Slide 行の `cellF` を `+width/2` する一度きりの変換が要る（スクリプトを用意する） |

**実装後の確認では特に次を見てほしい**（実装時点では検証できない点）:

- §4: Slide の一部の点だけをドラッグしてもノーツ全体が飛ばないこと／全点選択で Sky↔Ground を移せること
- §5: プレビューとエディタで Slide の横位置が一致すること（Tap/ExTap/Flick に回帰が無いこと）
- §6: 判定線がプレビューの正しい高さに出ること（ウィンドウをリサイズしてもずれないこと）
- §8: ホイールスクロールでプレビューが追従し、再生⇔停止の切り替えで時刻が飛ばないこと

---

## 実装ログ（2026-08-02、同セッション内）

§9の実装順どおり§3→§2→§1→§4→§7→§6→§8→§5の順で全項目実装した。
**`dotnet build Assembly-CSharp.csproj` でコンパイル成功を確認済み**（警告12件はすべて
今回の変更と無関係な既存のもの）。Unity Editor上でのPlay確認は次回。

- **§3**: `HandleHeightLanePointerDown`の`heightEasingCycleCandidate`設定に
  `currentTool == EditorTool.Slide`条件を追加。ドラッグでの層編集・クリックによる選択は
  ツール非依存のまま維持。
- **§2**: `DrawHeightLane`の未選択色を`NoteColor(note.kind)`のα0.28に変更（旧: 白のα0.28）。
- **§1**: `DrawWaypointGlyph`のシグネチャを`(x,y,marker,color)`→`(Rect,marker,color)`に変更し、
  シート本体の2箇所(forceSky/非forceSky)で`NoteX(cellF)`〜`NoteX(cellF+width)`の帯を渡すよう変更。
  高さレーン側は元々別コード(正方形描画)だったため無変更。
- **§4**: `dragCanChangeLayer`/`dragStartPaneLayer`フィールドを新設。`BeginPointDrag`で
  `AllSelectedNotesFullySelected()`（選択中の各ノーツが全点選択されているか）から算出。
  `OnSheetPointerMove`は`dragCanChangeLayer`がfalseの間、開始ペインと異なるペイン
  (`SamePaneSide`で判定)への`dragLastValid*`更新を無視し、`deltaLayer`も強制0にする。
  `EnterPasteMode`を`PaneAt`→`TryPaneAt`に変更し、レーン外でVを押したときは
  ペーストモードに入らないよう修正（想定していた「アンカーが実在しない中間値に固定される」
  バグの直接原因だった箇所）。
- **§7**: Tap/ExTap/Flickのcaseの先頭に`HitTestPoint`による横取り処理を追加。
  Slideのcaseは`pendingSlideStart == null`のときだけ横取り判定を行うよう分岐を追加し、
  2点目待ち中は既存ノーツの上でも完成させる元の挙動を維持。
- **§6**: `PreviewSystem.Config`プロパティを追加。`previewSurface.generateVisualContent`に
  `GeneratePreviewOverlay`を登録し、`preview.Config`のNDC値(`vGroundJudge`/`vSkyJudge`)から
  Ground/Sky判定線をPainter2Dで描画。3D投影の再計算は不要（StageOverlay.DrawBandと同じ式）。
- **§8**: `Update()`に停止中の`scrollTick`→`preview.Seek`同期を追加。あわせて
  「再生停止直後のcursorTick書き戻し」ブロックに`scrollTick`も同期する変更を追加
  （followPlayback無効時に古いscrollTickへ引き戻されるのを防ぐため）。
  トランスポート4ボタン・スクラブスライダー・「先頭へ戻る」メニューの`preview.Seek`呼び出し
  すべてに`scrollTick`の更新を追加。
- **§5**: `Notes/NoteGeometry.cs`（Visible中継点描画・`PushSlideBand.Emit`）、
  `Gameplay/Judge.cs`（`CellOverlap`/`InBand`/`Contains`）、`Chart/ChartValidator.cs`
  （`ValidateStructure`のV6範囲チェック・`FirstCellRange`）からSlideの中心基準分岐を削除し、
  全種別で左端基準(`cellF`～`cellF+width`)に統一。`Chart/ChartBuilder.cs`のデモ譜面3箇所
  （Hold相当Slide1本、Arc相当Slide2本）の`cellF`を「旧中心値 - width/2」に変換し、
  見た目の位置を維持。`memory/note-spec.md`をrev.6に更新（§1.1に基準を明記、変更履歴追加）。
  保存済み`.muses`譜面は無かったため、データ移行は不要だった。

**次回セッション最優先事項**: Unity Editorでの実機確認。特に
§4（Slideの一部の点だけドラッグしてもノーツ全体が飛ばないこと／全点選択でSky↔Groundを
移せること）、§5（エディタとプレビューでSlideの横位置が一致すること）、
§6（判定線の位置がリサイズ後もずれないこと）、§8（ホイールスクロールでプレビューが
追従し、再生⇔停止の切り替えで時刻が飛ばないこと）を重点的に見る。

## 関連

- `memory/editor-ui-rework-r2.md` — 前段。§1〜§7（実装済み。§7・§3 は実機で問題なしと確認）。
- `memory/editor-ui-rework-mmw.md` — その前段。§4 の `forceSky`（層をまたぐ Slide を Sky ペインのみに描く）は §4.1(d)-2 の遠因。
- `memory/editor-ui-redesign.md` — さらに前段。§7.5 が高さレーンの初出。
- `memory/note-spec.md` — ノーツ仕様 rev.5。**§5 の決定により rev.6 で `cellF` の基準を明記する。**
- `memory/editor-spec.md` — Phase 4 機能仕様 rev.2。§2 のレイアウト図は既に古い。
- `memory/reference/MikuMikuWorld-master/` — 参照元ソース。
