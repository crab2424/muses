# 譜面エディタ r13 — 実機確認で見つかった不具合8件の設計

`riser-r2.md`（コミット `5e20116`）の実機確認中に見つかった既存不具合5件＋Riser/Diver関連2件、
および確認の過程で判明したプレビュー側の不整合1件を扱う。**このうち §8 だけはエディタUIではなく
プレビュー(3D)とゲーム共通コードの問題**だが、報告と原因が一体なので同じ文書に置く。

出典: [[muses-unity-port-progress]] 「Riserエディタ対応、実機確認完了・新規不具合8件を報告受領」。

---

## 0. 確定事項（ユーザー決定、2026-08-06）

| # | 項目 | 決定 |
|---|---|---|
| 1 | 貼り付けの横位置 | **選択範囲の中心をカーソルに合わせる**（現行の「コピー元の列からの相対移動」を廃止） |
| 2 | 重なり時の描画順（下→上） | **slide → tap → flick → extap → riser/diver** |
| 3 | クリック有効範囲 | **縦±9px・横±6px**（現行 縦±6px・横±3px） |
| 4 | 端ドラッグ「範囲が狭い」の意味 | **掴める範囲（端±4px）が狭い**（＝可変できる幅の上下限ではない） |
| 5 | 層移動⇕ツールでの横取り | **Riser/Diverの上だけ横取り、それ以外は配置** |
| 6 | フレームレート | **タイムラインは正常。再生中・プレビュータブで悪い**（§8） |
| 7 | プレビューの見た目 | **Thickness Min Frac（ノーツの奥行き厚み）が反映されていない**（§8.1） |

---

## 1. 不具合1: 貼り付け位置がコピー元の列に依存する

### 1.1 原因

`EnterPasteMode`(`ChartEditorApp.cs:3204`) が V を押した瞬間のカーソル位置を
`pasteAnchorCell` / `pasteAnchorLayer` に記録し、`ComputePasteTransform`(`:3230`) が
**その時点からの相対移動量**を全点に足している（`rawDeltaCell = pasteLastValidCell - pasteAnchorCell`）。
tick だけはクリップボードが 0 正規化済み（`NormalizedClonesOfSelection`, `:3126`）なので
カーソル絶対位置へ吸着する、という非対称な設計だった（参照元 MikuMikuWorld の pasteLane 方式）。

結果、**V を押してからマウスを動かさなければ元の列のまま**貼られる。決定1でこれを改める。

### 1.2 新しい変換規則

| 軸 | 規則 |
|---|---|
| tick | **変更なし**（クリップボード先頭 tick がカーソル tick へ吸着） |
| cellF | **クリップボード全体のセル範囲の中心**がカーソルのセルへ来る |
| layerF | **クリップボード全体の層の中心**がカーソルのペイン(0 or 1)へ来る |

tick が「最小値合わせ」・cellF が「中心合わせ」という非対称は残るが、これは意図的:
時間軸は「今いる拍から先へ置く」、横軸は「見えている塊の中心を置く」がそれぞれ自然な操作だから。

**cellF の実装**（`CellFFromCenter`(`:3475`) と同じ考え方を点群へ拡張する）:

```csharp
// clipboard 全体のセル範囲
float minCell = min(w.cellF), maxEdge = max(w.cellF + w.width);
float spanW   = maxEdge - minCell;
float step    = clipboard.Exists(n => n.kind == NoteKind.Slide) ? 0.5f : 1f;
// 中心をカーソルへ → 左端をスナップ → 盤面内へクランプ
float newMin  = Mathf.Clamp(SnapCellTo(hoverCell - spanW * 0.5f, step), 0f, Cells - spanW);
float deltaCell = newMin - minCell;
```

- **`ResolveCellDelta` は使わない**。あれは「デルタをスナップしてから点群ごとクランプ」する関数で、
  中心合わせだと *左端が格子に乗る保証が無くなる*（幅が奇数セルのとき 0.5 セルずれる）。
  左端を先にスナップしてからデルタを求めることで、`CellFFromCenter` による単発配置と
  完全に同じ格子に乗る。
- `spanW > Cells` は起こらない（コピー元が盤面内なので）。念のため `Cells - spanW` が負なら 0 にする。

**layerF の実装**:

```csharp
float minLayer = min(w.layerF), maxLayer = max(w.layerF);
float centerL  = (minLayer + maxLayer) * 0.5f;
float rawDeltaLayer = paneLayer - centerL;              // paneLayer = TryPaneAt の 0 or 1
float deltaLayer    = ResolveLayerDelta(allPts, rawDeltaLayer);  // 既存のまま(点群ごとクランプ)
```

- Ground だけのクリップボード（center=0）を Sky ペインへ → delta=1 → 正しく移る。
- 両層にまたがるクリップボード（center=0.5）を Sky ペインへ → delta=0.5 → `ResolveLayerDelta` が
  0 にクランプ（既に 0〜1 を使い切っているため動かせない）。形が壊れない。

### 1.3 消えるもの・残るもの

- `pasteAnchorCell` / `pasteAnchorLayer` は**不要になるので削除**。
- `pasteLastValidCell` / `pasteLastValidLayer` は**残す**（ガター上で `TryPaneAt` が null を返す間、
  直前の有効なペイン位置を使い続けるため。r2 §4 の理由はそのまま生きている）。
- `EnterPasteMode` の「ペイン上にマウスを合わせてから」の早期 return は**削除**（§2 で必要）。

---

## 2. 不具合2: コンテキストメニューから貼り付けられない

### 2.1 原因

`OnSheetRightClick`(`:2497`) が `GenericDropdownMenu` を出すと、ポインタがメニュー要素へ移るため
`OnSheetPointerLeave`(`:2992`) が発火して **`sheetHoverPos = null`** になる。
その状態でメニューの「貼り付け」を選ぶと `EnterPasteMode` の

```csharp
var pane = !sheetHoverPos.HasValue ? null : L.TryPaneAt(sheetHoverPos.Value.x);
if (!pane.HasValue) { statusMessage = "貼り付け先(...)にマウスを合わせてから..."; return; }
```

に落ちて**貼り付けモードに入らない**。「コンテキストにカーソルが乗っているため判定にならない」という
報告そのもの。`ComputePasteTransform` も `sheetHoverPos.Value` を無条件に読むため、
早期 return を外すだけでは NullReference になる。

### 2.2 対処

1. **右クリック位置を憶える**: `OnSheetRightClick` の先頭で `contextMenuPos = pos` を保存する。
2. **EnterPasteMode の早期 return を削除**。§1 でアンカーが不要になったので、そもそも
   「押した瞬間の位置」を取る必要が無い。
3. **ホバー位置のフォールバックを一本化**する:

```csharp
/// 貼り付けの基準に使うシート内座標。ポインタがシート外(コンテキストメニュー上・
/// インスペクタ上)にある間は直前の右クリック位置を使う。
private Vector2? PasteReferencePos => sheetHoverPos ?? contextMenuPos;
```

   `ComputePasteTransform` / `ConfirmPaste` / `DrawPasteGhost` はこれを使い、
   **どちらも無い場合だけ**ゴーストを描かず・確定しない（NullReference の防止）。

これにより、右クリック→「貼り付け」を選ぶと**右クリックした位置にゴーストが出た状態**で
貼り付けモードに入り、そのまま左クリックで確定できる。

> **既知の割り切り**: `GenericDropdownMenu` を閉じるクリックはシートまで届かないため、
> 「メニューの項目を選ぶクリック」と「確定のクリック」で2回押すことになる。
> Ctrl+V と同じカーソル追従モードに統一するための代償で、これは正常な挙動とする
> （右クリック位置へ即確定する案は §10-1 に残す）。

---

## 3. 不具合3 + 8: 重なり時の描画順とクリック優先度

決定2の順序（下→上）を**描画とヒットテストの両方**で共有する。片方だけ直すと
「手前に見えているのに掴めない」が起きるため、必ず1つの関数から導く。

```csharp
/// 重なったときにどちらが手前かの優先度（大きいほど手前）。
/// 描画は昇順（後に描いたものが上）、ヒットテストは降順（先に当たったものを返す）。
private static int DrawPriority(Note n) => n.kind switch
{
    NoteKind.Slide => 0,
    NoteKind.Tap   => 1,
    NoteKind.Flick => 2,
    NoteKind.ExTap => 3,
    NoteKind.Riser => 4,   // Riser/Diver は常に最前面（不具合8）
    _              => 1,
};
private const int DrawPriorityCount = 5;
```

### 3.1 描画（`GenerateNotesSheet:1636`）

現行の `foreach (var note in chart.notes)` を **優先度ごとの5パス**にする:

```csharp
for (int pri = 0; pri < DrawPriorityCount; pri++)
    foreach (var note in chart.notes) { if (DrawPriority(note) != pri) continue; /* 既存の本体 */ }
```

- **ソートしない**理由: `chart.notes` は編集のたびに変わるためキャッシュの無効化管理が要る。
  5パスなら追加コストは「種別比較 5N 回」だけで、アロケーションもキャッシュ整合性の心配も無い。
  タイムラインの描画は既に毎フレーム走っているが正常な fps が出ている（§0-6）ので実測上も問題ない。
- 同じ優先度どうしは従来どおり `chart.notes` の順（＝後から置いたものが手前）。
- 選択ハイライト（`:1752`）と高さレーンは今までどおり全ノーツの**後**に描く。

`DrawHeightLane`(`:1829`) の未選択ノーツのループにも同じ5パスを適用する（選択中を最後に描く
既存規則はその上に乗せる）。高さレーンでも Riser の2ハンドルが他ノーツに埋もれないようにするため。

### 3.2 ヒットテスト（`HitTestPoint:3518`）

現行は `chart.notes` を逆順に1回走査している。これを**優先度の降順 × リスト逆順**に変える:

```csharp
for (int pri = DrawPriorityCount - 1; pri >= 0; pri--)
    for (int idx = chart.notes.Count - 1; idx >= 0; idx--)
    { if (DrawPriority(chart.notes[idx]) != pri) continue; /* 既存の当たり判定 */ }
```

これで「見えている手前のノーツが必ず掴める」が保証され、不具合8（Riser/Diver の選択を優先）は
**専用の分岐を書かずに満たされる**。§4 でクリック範囲を広げても、重なりの解決が決定的なので
誤選択が増えない（これが §4 を安全に実施できる前提でもある）。

`HitTestPointsInRect`(`:2753`) は全件を返すので順序不問、変更しない。
`HitTestSlideBand`(`:3544`) は Slide 専用なので変更しない。

---

## 4. 不具合5: クリック有効範囲を広げる

`HitTestPoint` の許容量を決定3の値にする（描画矩形は縦±4px のまま変えない）。

```csharp
if (Mathf.Abs(mouse.y - y) > yTol) continue;                       //  6f → yTol
if (mouse.x >= min - 6f && mouse.x <= max + 6f) return ...;         //  3f → 6f
```

**`yTol` はズームとスナップで頭打ちにする**:

```csharp
// 拡大時は決定どおり9px。縮小して隣のスナップ位置が18px未満に詰まった場合だけ、
// 隣のtickのノーツを掴んでしまわないよう間隔の半分まで自動的に狭める（下限4px＝描画矩形）。
float yTol = Mathf.Clamp(L.pxPerTick * SnapTicks * 0.5f, 4f, 9f);
```

これを入れないと、低ズーム＋細かいスナップ（1/32 等）で
「1つ上のノーツが選ばれる」という新しい暴発を作ることになる。

**同時に直す**: `DrawPlacementGhost`(`:2051`) は `HitTestPoint(...).HasValue` でゴーストを消しており、
配置可否の判定とゴーストの表示が同じ関数を共有している（r5 の「ゴーストと実際の配置を一致させる」原則）。
§6 でツールごとに規則が分かれるので、**共通の述語へ切り出す**:

```csharp
/// そのツールで pos にノーツを置けるか（置けないなら既存ノーツの選択に横取りする）。
/// OnSheetPointerDown と DrawPlacementGhost が必ず同じ答えを使うための唯一の判定。
private NoteRef? PlacementBlockedBy(SheetLayout L, Vector2 pos, EditorTool tool)
{
    var hit = HitTestPoint(L, pos);
    if (!hit.HasValue) return null;
    // riser-r2 §4 改（r13 §6）: 層移動⇕は他ノーツへの重ね置きが主用途なので、
    // Riser/Diver に当たったときだけ横取りする。
    if (tool == EditorTool.LayerMove && hit.Value.note.kind != NoteKind.Riser) return null;
    return hit;
}
```

---

## 5. 不具合6: 端ドラッグの掴める範囲が狭い

`EdgeGrabSign`(`:2739`) の `const float grab = 4f` が原因（決定4）。

```csharp
float w = Mathf.Abs(x1 - x0);
// 決定4: 掴める幅を広げる。ただし細いノーツで中央（移動ドラッグ用）が消えないよう、
// 片側は幅の30%までに制限する（1セル幅でも中央に40%が残る）。
float grab = Mathf.Clamp(w * 0.30f, 3f, 8f);
```

- 1セル幅のノーツ（既定 `laneWidthPx` でおよそ 30px）なら片側 9px→ 上限 8px、中央に 14px 残る。
- 上限 8px は §4 の横方向許容 6px より少し広い。`EdgeGrabSign` は `HitTestPoint` が当たった後にしか
  評価されないので、実効的には「ノーツ矩形の外 6px＋内 8px」が掴める帯になる。
- 幅の上下限（`Mathf.Clamp(..., 0.1f, Cells - cellF)`, `:2820`）は**変更しない**（決定4）。

---

## 6. 不具合7: 層移動⇕ツールで置けない場所がある

`OnSheetPointerDown` の `case EditorTool.LayerMove`(`:2229`) が、他の配置ツールと同じ
「既存の点の上は選択へ横取り」（r7 §1）をそのまま踏襲していたため、
**Tap の上に Riser を重ねる**という Riser 本来の主用途が塞がれていた。

決定5どおり、§4 の `PlacementBlockedBy` に規則を集約して:

```csharp
case EditorTool.LayerMove:
{
    var hitExisting = PlacementBlockedBy(L, pos, EditorTool.LayerMove);  // Riserのみ非null
    if (hitExisting.HasValue) { /* 従来どおり選択＋ドラッグ開始 */ break; }
    /* 以降は既存の配置処理そのまま */
}
```

- §3.2 でヒットテストが Riser を最優先するようになったので、
  **Tap と Riser が重なっている場所では Riser が返る → 横取り（既存Riserの微調整）**、
  **Tap しか無い場所では Tap が返る → 横取りせず配置**、と両立する。
- ゴースト側も `PlacementBlockedBy` を通るので、「置けるのにゴーストが消える」不整合が出ない。
- 他のツール（Tap/ExTap/Flick/Slide/AddWaypoint）の挙動は一切変わらない。

`riser-r2.md` §4 の「重ねる操作はインスペクタからの付与が主動線」という位置づけは、
これでシート上の直接配置に戻る。インスペクタからの付与（§7.1）は補助として残す。

---

## 7. 不具合4改め: プレビュー（3D）の不整合とフレームレート

**タイムラインは正常**（決定6）。以下はすべて `PreviewSystem` とゲーム共通コード側の問題。

### 7.1 Thickness Frac がプレビューに反映されない（決定7の症状）

**原因**: プレビューの rig は `PreviewSystem.BuildRig()` が**コードから組み立てる**（`PreviewSystem.cs:134`）。
`NoteView` は `AddComponent` で作られるため、Inspector でチューニングされた値ではなく
**C# のフィールド初期値**が使われる。

| 値 | ゲーム(SampleScene.unity) | コード既定(= プレビュー) |
|---|---|---|
| `NoteView.thicknessFrac` | **0.06** (`:784`) | **0.025** (`NoteView.cs:28`) |
| `NoteView.thicknessMinFrac` | 0.004 (`:785`) | 0.004 (`NoteView.cs:30`) |
| `StageConfig.skyFillAlpha` | **0.2** (`:526`) | **0.05** (`StageConfig.cs`) |

`thicknessFrac` が 2.4 倍違う。これが「ノーツの奥行き厚みが潰れて見える」の正体。
`thicknessMinFrac` は同値だが、シェーダは
`halfThickness = max(_ZJudge * _ThicknessFrac, depth * _ThicknessMinFrac)`（`NotePlacement.hlsl:48`）
なので、`thicknessFrac` が小さいと **(a) 手前のノーツが薄くなる** だけでなく
**(b) 下限側（Min Frac）が勝ち始める奥行きが `depth > zJudge × Frac/MinFrac` ＝
15×zJudge から 6.25×zJudge へ手前に来る**。つまり画面のかなり手前から「画面上一定厚み」の
点滅防止側だけが効いた見た目になる。「Thickness Min Frac しか反映されていない」という
ユーザーの表現とそのまま一致する。

**対処（推奨）: コード既定値をゲームの実測値へ合わせる。**

- `NoteView.thicknessFrac` の初期値を `0.025f → 0.06f`、`StageConfig.Default().skyFillAlpha` を
  `0.05f → 0.2f` にする。
- **ゲーム側は変わらない**（シーンが自分の値を直列化して持っているため）。プレビューだけが追いつく。
- `showTouchDebug`（シーン true / 既定 false）は開発用トグルなので合わせない。

**より根本的な対処（次段階、§10-2）**: `StageConfig` と厚み2値を **ScriptableObject 1個**に出し、
シーンとプレビューの両方がそれを参照する。今回の症状はコード既定とシーンの二重管理が生む
**構造的なドリフト**なので、値を合わせるだけでは同じ事故が再発する。
ただし ScriptableObject 化はシーン側の再配線（Unity Editor 操作）が要るため、
今回は既定値合わせで直し、資産化は別コミットにする。

### 7.2 【併発】RenderTexture のアスペクト変化にノーツだけ追従しない

調査中に見つかった、まだ報告されていない不整合。

- ノーツの頂点は `NoteGeometry.Build(cfg, d, ...)` が **`Derived` を焼き込んで**生成し、
  `_LaneK`(= `U × aspect × tan(φ/2)`, `StageDerive.cs:178`) 等の uniform も
  `NoteView.ApplyStaticUniforms` が **Build 時にしか**設定しない。
- 一方 `StageController.Update()` は `cam.aspect` の変化を検知して `Derived` と
  **ステージ形状だけ**を作り直す（`StageController.cs:69`）。
- プレビューは `EnsureRenderTexture` でパネル寸法に合わせて `cam.aspect` を張り替える
  （`PreviewSystem.cs:607`）ため、**ウィンドウ／パネル幅を変えるとステージだけが変形し、
  ノーツは旧アスペクトの幅のまま**になる。ゲーム本体は起動後にアスペクトが変わらないので露見しない。

**対処**: `PreviewSystem` が最後に Build したアスペクトを憶え、`Tick()` で
`cam.aspect` の変化を検知したら `Rebuild` 相当（`noteView.Build` + `judge` の作り直しは不要なので
`NoteView` 側に `RebuildGeometry(cfg, derived)` を切り出す）を1回だけ走らせる。
リサイズ中の連打を避けるため、**変化が止まってから 0.15 秒後に1回**（`TickWorkspacePersistence` と
同じ遅延パターン）。

### 7.3 フレームレート: `NoteView.SetNoteAlpha` がメッシュ全体を毎回アップロードしている

**これが「再生中・プレビュータブで特に悪い」の主犯と考えている。**

```csharp
public void SetNoteAlpha(NoteRuntime rt, float alpha)      // NoteView.cs:139
{
    if (notesUv0 == null || rt.alpha == alpha) return;
    rt.alpha = alpha;
    for (int i = rt.vStart; i < rt.vStart + rt.vCount; i++) notesUv0[i].x = alpha;
    notesMesh.SetUVs(0, notesUv0);        // ← ノーツ1個の変更で配列全体を再アップロード
}
```

- `NoteView.Build` のコメントに「600秒・BPM150 のデモ譜面で既に約8万頂点」とある。
  `notesUv0` は `Vector2[80000]` ＝ **1回あたり約 640KB** のマーシャリング＋GPU 転送。
- 再生中は判定のたびに呼ばれる（`Judge` 内 15 箇所）。16分・BPM150 なら毎秒10回で 6.4MB/s。
- さらに **`Judge.Seek`(`Judge.cs:69`) は全ノーツに対して `setAlpha` を呼ぶ** ため、
  シーク1回で **O(ノーツ数 × 総頂点数)**。2000ノーツなら 1.2GB 相当の転送になる。
  エディタは停止中に `scrollTick` が動くたび `preview.Seek` を呼ぶ（`ChartEditorApp.cs:667`）ので、
  **ホイールでスクロールしただけでも**これが走る。

**対処**: 書き込みと転送を分離する。

```csharp
private bool alphaDirty;
public void SetNoteAlpha(NoteRuntime rt, float alpha)
{
    if (notesUv0 == null || rt.alpha == alpha) return;
    rt.alpha = alpha;
    for (int i = rt.vStart; i < rt.vStart + rt.vCount; i++) notesUv0[i].x = alpha;
    alphaDirty = true;                    // 転送はしない
}
/// 1フレームに最大1回だけ実際に転送する。judge.Update / judge.Seek の直後に呼ぶ。
public void FlushAlpha()
{
    if (!alphaDirty) return;
    notesMesh.SetUVs(0, notesUv0);
    alphaDirty = false;
}
```

- 呼び出し側: `PreviewSystem.Tick()`（`judge.Update` の後）、`PreviewSystem.Seek()`、
  `PreviewSystem.Rebuild()`、**および `GameController` の同じ位置**。
  「判定を進めたら Flush」を規則として1箇所にコメントで明記する。
- `notesMesh.MarkDynamic()` を `Build` で呼んでおく（毎フレーム書き換わる前提を Unity に伝える）。
- **ゲーム本体(iPad)にも同じだけ効く**。`52b8749` で直したスタッタリングとは別の経路。

### 7.3-b 【重要な訂正】§7.3 は的外れだった（実機確認後、2026-08-06）

`FlushAlpha` 化そのものは正しい改善だが、**通常再生では `SetNoteAlpha` を呼ぶパスを一切通らない**
（`Judge.Update` が走るのはオートプレイON時か、停止中のシーク時だけ）。
そのため「再生中のfpsが悪い」の原因ではなく、実機で効果が出なかったのは当然だった。
以下の §7.6 / §7.7 が真因。**§7.3 の変更自体は残す**（シーク・オートプレイでは実際に効くため）。

### 7.6 【真因1】未保存の間ずっと 0.3 秒ごとに `preview.Rebuild` が走り続けていた

`ChartEditorApp.Update`:

```csharp
if (dirty && !draggingAnything && Time.unscaledTime - lastPreviewRebuildRealtime > 0.3f)
    preview.Rebuild(song, chart, ...);
```

`dirty` は **save/load でしか false に戻らない**（`:873` の保存時と `:764` の読み込み時のみ）。
つまり一度でも編集すると、**保存するまで 0.3 秒ごとに永久に** `Rebuild` が走る。
`Rebuild` の中身は「時刻再解決＋コンボ点再計算＋スクロールタイムライン構築＋小節時刻構築＋
`NoteGeometry.Build`(約8万頂点)＋`Mesh` の頂点/色/UV×3/三角形の全再アップロード＋
`new Judge`＋`Prepare`＋`Reset`」で、毎秒 3.3 回の巨大な CPU/GC スパイクになる。

**原因**: 「保存が必要か(`dirty`)」と「プレビューに未反映の編集があるか」を同じフラグで
兼用していたこと。前者は保存まで下がらないのが正しい仕様なので、兼用が成立しない。

**対処**: `previewDirty` を新設し、`Rebuild` を1回走らせたら落とす。
`dirty` への代入は20か所以上あるため、**`dirty` をプロパティ化**して setter で
`previewDirty = true` を立てる（代入箇所の取りこぼしを構造的に防ぐ）。
`OpenChartFromPath` / 保存直後の `Rebuild` 呼び出しでも `previewDirty = false` にする。

### 7.7 【真因2】再生中の描画間引き閾値がフレーム間隔と同値でコマ落ちしていた

`PreviewSystem.MaybeRender`:

```csharp
bool shouldRender = clock.Running
    ? Time.realtimeSinceStartup - lastRenderRealtime >= RenderIntervalSec  // 1/60秒
    : sceneDirty;
```

閾値 `1/60 = 16.67ms` は **60Hz VSync でのフレーム間隔そのもの**。実測間隔がこれをわずかでも
下回ったフレームは描画が飛ばされ、次フレームまで持ち越される＝不定期なコマ落ちになる。
120Hz 環境では常に2フレームに1回しか描かれない。
タイムラインは Painter2D で毎フレーム描き直すため、**プレビューだけがカクついて見えた**
（報告「タイムラインは正常、プレビューが悪い」と完全に一致）。

**対処**: 再生中はアプリのフレームレート自体が VSync / `targetFrameRate` で律速されているので、
ここで追加の間引きをする意味がない。`clock.Running || sceneDirty` にして毎フレーム描く。
`RenderIntervalSec` / `lastRenderRealtime` は廃止。

**結果（実機確認済み）**: 8.3ms / 120fps / 最悪フレーム 12ms。0.3秒周期のスパイクは消え、
120Hz ディスプレイに追従できるようになった。

### 7.8 Thickness は「値の乖離」だけが原因だった（§7.1 の続き・決着）

診断表示（uniform の読み戻し）で確認した結果、`_ZJudge=3.1`（理論値 3.0999 と一致）、
`_ThicknessFrac` の読み戻しも設定値と一致、frac 項が正しく勝っており、
**シェーダへの経路・計算はすべて正常**だった。§7.1 で疑ったような
「uniform が届いていない」「Material が別インスタンス」といった不具合は無かった。

`_ZJudge = 0` なら `max()` の第1項が常に負けて Frac が無効化される
（＝「Min Frac しか効いていない」の症状そのもの）という仮説は、実測 3.1 で否定された。

### 7.9 決着: 厚みの値をユーザーが確定し、設定として永続化する

実機と見比べたユーザーが **`thicknessFrac = 0.06` / `thicknessMinFrac = 0.01`** で確定。
`minFrac` は従来値 0.004 の 2.5 倍で、遠方のノーツの画面上の最小厚みが効く範囲が広がる
（`max()` の第2項が勝ち始める奥行きが `zJudge×Frac/MinFrac` = 78.6 → 18.6 と手前に来る）。

- `NoteView` の C# 既定値と `EditorSettings` の既定値を両方この値にする。
- **スライダーを恒久的な設定として残し、`EditorSettings` へ永続化する**
  （ハイスピード・音量と同じ「エディタ側の表示設定」の扱い。譜面ファイルには入れない）。
- `Frac` スライダーの下限は 0 ではなく 0.001 にする。0 だと `max()` の第1項が常に負けて
  「スライダーが効かない」状態を UI から作れてしまうため。
- 調査用に入れた uniform 読み戻しの診断表示は役目を終えたので削除する。

**ゲーム本体 `SampleScene.unity` の `thicknessMinFrac` もユーザー判断で 0.01 へ揃えた**
（2026-08-06、シーン YAML 直接編集、`0.004 → 0.01`）。`thicknessFrac` は元々両者とも 0.06 で
一致していたので、これでプレビューとゲームの厚みは完全に一致した。
§7.1 で見つけた「コード既定とシーンの二重管理によるドリフト」の構造自体は残っているため、
将来また値がズレる可能性はある。根本解決は §10-2 の ScriptableObject 化。

### 7.4 二次的な毎フレーム O(N)（計測後に判断）

| 箇所 | 内容 | 対処案 |
|---|---|---|
| `PreviewSystem.PlayNoteSe`(`:492`) | 毎フレーム全ノーツ＋全コンボ点を走査 | `runtimes` は開始時刻順ソート済み（`Judge.Prepare` のコメント）。`Judge` と同じ **cursor + 早期 break** にする。`Seek` で cursor を巻き戻す |
| `AutoplayDriver.Step`(`AutoplayDriver.cs:36`) | 毎フレーム全 runtime 走査＋`List<Contact>` を新規確保 | 同じく cursor + break、リストは使い回す |
| `PreviewSystem.ChartEndSec`(`:449`) | 毎フレーム全ノーツ走査（`SyncModelToUi` のシークバー上限） | `Rebuild` 時に1回計算してキャッシュ |
| `previewSurface.MarkDirtyRepaint()`(`UI.cs:1798`) | 停止中も毎フレーム | `preview` 側が実際に描画したフレームだけ（`MaybeRender` が true を返したとき）に絞る |

いずれも §7.6 / §7.7 に比べれば小さい。**§7.6・§7.7 で 120fps・最悪12ms まで回復したので、
現時点では着手しない**（必要になったときの候補として残す）。

### 7.5 計測手段（先に入れる）

原因の確定と効果測定のため、**ステータスバーに frame time を出す**。

- `Time.unscaledDeltaTime` の移動平均（32フレーム）で `f"{ms:0.0}ms ({fps:0}fps)"`。
- デバッグ用に「そのフレームで `cam.Render()` したか」「`FlushAlpha` が実際に転送したか」の
  直近1秒間のカウンタも併記する（表示は設定モーダルのトグルで ON/OFF）。
- これがあれば §7.3 の前後比較が実機なしで即座に取れる。**§7 の作業はこれを最初に入れる。**

---

## 8. 併せて見つかった別件（今回の8件とは独立）

1. **【要対応・実機に効く】`SampleScene.unity` の `cfg` に `riserReachFrac` と `handoffWindowMs` が無い。**
   Riser 実装（`bbc9ae6`）でフィールドを追加した後にシーンを保存していないため、
   Unity のデシリアライズで **両方 0 のまま実機が動いている**。
   `riserReachFrac=0`（到達判定が無条件成立）・`handoffWindowMs=0`（Slide への handoff が効かない）
   という、Riser の仕様（note-spec §4.6）と食い違う状態。
   → Unity Editor で StageController の cfg を開き、既定値（1.0 / 200ms）を入れてシーンを保存する
   **ユーザー操作が必要**。[[muses-unity-port-progress]] の未解決事項3（Riser の実機確認）は
   これを直してから行うべき。
2. `BuildStatsText`(`UI.cs:1896`) が Riser を種別内訳に数えない（合計には入る）。1行追加で済む。
3. `PreviewSystem` の `StageConfig` はコード既定、ゲームはシーン直列化、という二重管理そのもの（§7.1）。

---

## 9. 実装順序

依存関係で決まる順。1〜3 はエディタ、4〜6 はプレビュー/ゲーム共通なので別コミットにできる。

1. **§3 描画順＋ヒットテスト優先度**（`DrawPriority` の導入）。§4〜§6 がこの上に乗る。
2. **§4 クリック範囲 ＋ `PlacementBlockedBy` の切り出し ＋ §5 端ドラッグ**（当たり判定まわりを一括）。
3. **§6 層移動ツールの例外**（`PlacementBlockedBy` が出来ていれば数行）。
4. **§1 §2 貼り付け**（アンカー廃止 → 中心合わせ → 右クリック位置フォールバック）。
5. **§7.5 計測表示 → §7.3 FlushAlpha**（効果を数値で確認する）。
6. **§7.1 既定値合わせ → §7.2 アスペクト追従**（見た目の確認は Unity Editor の Play で可能）。
7. §8-1 のシーン修正（ユーザー操作）、§8-2。

検証は従来どおり `dotnet build` でのコンパイル確認＋Unity Editor での Play。
§7.3 は `JudgeSmokeTest` に「`SetNoteAlpha` の呼び出し回数と Flush 回数」を見るケースを足せば
純粋 C# 側で回帰を検知できる（`Judge` は UnityEngine 非依存、`setAlpha` はコールバック注入）。

---

## 10. 未決事項

1. **コンテキストメニューの「貼り付け」を右クリック位置へ即確定にするか**（§2.2 の割り切り）。
   まず追従モード統一で運用し、2クリックが煩わしければ「貼り付け（ここへ）」を別項目で足す。
2. **`StageConfig` + 厚み2値の ScriptableObject 化**（§7.1）。ドリフトの構造的な解決策だが
   シーン再配線が要るため今回は見送り。
3. **§7.4 の二次最適化に着手するか**は §7.3 後の実測値しだい。
4. §4 の `yTol` をズーム連動にした場合の体感（低ズームで狭く感じないか）は実機で要確認。
