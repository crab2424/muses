**実装済み（2026-08-01、同セッション内、Unity Editor未検証）**: 本書の§1〜§7・移植候補8件を
全項目実装した。詳細は本ファイル末尾の「実装ログ」、コミット前の申し送りは
[[muses-unity-port-progress]] を参照。

# 譜面エディタ UI 改修設計（MikuMikuWorld 参照、2026-08-01 rev.1）

`memory/editor-ui-redesign.md` §7 完了後にユーザーから出た 7 項目の要望に対する設計。
今回はじめて**参考元のソースコード**（`memory/reference/MikuMikuWorld-master/`、C++/ImGui/OpenGL）
を読める状態になったので、文章ベースだった §7 までと違い**実装の根拠を参照元の該当箇所に紐づけて**書く。

- 参照元の主要ファイル: `MikuMikuWorld/ScoreEditor.cpp`（タイムライン骨格・カーソル・グリッド）、
  `TimelineNotes.cpp`（ノーツ描画とノーツ単位のマウス操作）、`Editing.cpp`（コピペ・編集コマンド）、
  `ScoreEditor.h`（状態一覧）。
- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（描画・入力）と
  `ChartEditorApp.UI.cs`（UI Toolkit の要素構築）。

**参照元との構造上の最大の差**（これが §5 の難しさの根源）:
MikuMikuWorld は **hold の始点・中継点・終点がすべて独立した `Note` オブジェクト**で
`score.notes` に平置きされ、`parentID` で親 hold に紐づく（`Note.h`、`Score.h`）。
そのため選択・ドラッグ・削除がすべて「点」単位で自然に成立している。
muses は `Note.points : List<Waypoint>` という**入れ子構造**で、選択も `List<Note>`（ノーツ単位）。
参照元の操作感をそのまま持ってくるには、選択の粒度を点単位へ下げる必要がある（§5.2）。

---

## 1. 貼り付けのカーソル追従（ペーストモード）

**現状**: `ChartEditorApp.cs:1518` `PasteClipboard()` は Cmd+V の瞬間に
`scrollTick`（＝判定線位置）を基準にして**即座に確定挿入**する。位置を選べない。

**参照元の方式**（`Editing.cpp:80-107, 188-207, 109-176`）:

| 段階 | 実装 |
|---|---|
| コピー時 | `copy()` で `leastTick` を求め、**クリップボード内の tick を 0 基準へ正規化**（`Editing.cpp:61-63`） |
| Ctrl+V | `paste()` は挿入せず `pasting = true` にし、**その瞬間のカーソルのレーンを `pasteLane` に記録**するだけ |
| 毎フレーム | `previewPaste()` が `note.tick + hoverTick`、`note.lane + (現在のレーン - pasteLane)` の位置に `hoverTint`（半透明）でゴースト描画 |
| 左クリック | `confirmPaste()` で実挿入 → 履歴 push → 貼り付けたノーツを選択状態にする |
| Esc | `cancelPaste()` |

レーンだけ「V を押した時点からの差分」なのは、**押した直後は元の cellF のまま出る**ようにするため
（動かさずに確定すれば元の横位置が保たれる）。tick は 0 正規化なので先頭ノーツがカーソルに吸い付く。
この非対称は意図的で、そのまま採用してよい。

**muses での実装方針**:

- 状態: `bool pasting` / `float pasteAnchorCell` / `float pasteAnchorLayer` を追加。
- `CopySelectionToClipboard()`（`ChartEditorApp.cs:1509`）で **tick を最小値基準に正規化してから**格納する。
- Cmd+V は `pasting = true` にするだけ。`sheetHoverPos` は既にあるので追従計算はそこから。
- ゴースト描画は既存の `DrawPlacementGhost()`（`ChartEditorApp.cs:853`）と同じ場所・同じ考え方で
  `DrawPasteGhost()` を追加する。**配置ゴーストと同様に「PointerDown 側の計算をなぞる」方針を守る**
  （ずれると「ゴーストの位置でクリックしたのに違う場所に貼られた」になる。§7 のゴースト実装時と同じ理由）。
- `pasting` 中は配置ツールのゴースト・配置クリック・矩形選択をすべて抑止する
  （参照元も `updateNotes` で `!isPasting()` を配置プレビューの条件に入れている、`ScoreEditor.cpp:940`）。
- Esc と右クリックでキャンセル。確定後は貼り付けたノーツを選択状態にする（参照元どおり）。
- **層(layerF)の扱い**は参照元に無い muses 固有の論点。cellF と同じく「V を押した時点の層からの差分」を
  足す（Ground でコピー → Sky ペインへカーソルを動かして確定 = Sky へ貼れる）。

**副産物として得られるもの**: 参照元の `flipPaste()`（左右反転貼り付け、`Editing.cpp:92`）は
同じ仕組みの上に `copyNotesFlip` を用意するだけで載る。§7.2 の「反転」と合わせて検討。

---

## 2. ガター内のグリッド線を消す

**原因**: `ChartEditorApp.cs:695` が小節/拍/スナップ線を

```csharp
FillRect(p, new Rect(lanesXMin, y, lanesXMax - lanesXMin, thickness), c);
```

と **Ground 左端〜Sky 右端の 1 本の矩形**で描いており、間にあるガター（26px）を横断している。

**参照元の扱い**（`ScoreEditor.cpp:503-504, 515-528`）: グリッド線は
`x1 = canvasPos.x + laneOffset` 〜 `x2 = x1 + timelineWidth` の**レーン領域内だけ**に引かれ、
余白には出ない。一方で**小節線だけは `x1 - MEASURE_WIDTH` 〜 `x2 + MEASURE_WIDTH` と余白へはみ出す**
（`ScoreEditor.cpp:545`）。小節番号のテキストと線を繋げて読ませるため。

**方針**:

- スナップ線・拍線は **Ground と Sky で 2 本に分割**し、ガターには引かない。
- **小節線だけは左余白へ延長**する（`L.leftMargin.x` 〜 `L.ground.xMax` と `L.sky` の 2 本）。
  小節番号ラベルは §7.2 で左余白へ退避済みなので、参照元と同じ「線と番号が繋がる」状態になる。
- ガターに何も描かれなくなるので、**ガター幅 26px は縮められる**（§4 を実装すると
  ガターを横切る描画が完全に無くなるため。セル幅の確保に効く）。

---

## 3. 再生位置カーソル（橙色の横線）

**現状**: 赤い判定線（`ChartEditorApp.cs:757`）しか無く、これは「再生追従時に songTime が来る画面上の位置」
＝スクロール基準であって、**時間軸上の位置を指すものではない**。再生は常に `preview.SongTime` の続きから始まる。

**参照元の方式**: `currentTick` が編集カーソルの実体（`ScoreEditor.h:76`）。

- `updateCursor()`（`ScoreEditor.cpp:565-584`）: **Select モード・非再生中・ノーツ非ホバー時の左クリック**で
  `currentTick = hoverTick`。描画は「レーン幅いっぱいの線 ＋ 左側に三角形のつまみ」。
- `update()`（`EditorWindows.cpp:526-545`）: **再生中は `time` が進み `currentTick` はそこから導出、
  停止中は逆に `currentTick` から `time` を導出**する（＝停止中はカーソルが真、再生中は時刻が真）。
- `togglePlaying()`（`ScoreEditor.cpp:254-269`）: `audio.playBGM(time)` で**カーソル位置から再生**し、
  `playStartTime = time` を記録する。`stop()` は `time = currentTick = 0` に戻す。

**muses での実装方針**:

- `int cursorTick` を追加。橙 `#FFA030` 前後で判定線と同じ太さ・同じ横幅（高さレーン右端まで）に描く。
  左余白に参照元と同じ三角つまみを置くと、判定線（赤・固定位置）との役割の違いが一目で分かる。
- Select ツールで**ノーツに当たらないクリック**をしたら `cursorTick = スナップ後の tick`。
  参照元と同じく、これは矩形選択の開始と両立する（クリックだけなら選択解除＋カーソル移動、
  ドラッグすれば矩形選択。参照元 `ScoreEditor.cpp:569` も同じ条件で共存させている）。
- 再生開始（▶ / Space）で `preview.Seek(TickToSeconds(cursorTick))` してから `Play()`。
- **停止中と再生中でどちらが真かを切り替える**（参照元の設計をそのまま採る）:
  - 停止中: `cursorTick` が真。橙線はクリックで動かせる。
  - 再生中: `preview.SongTime` が真。既存の `followPlayback`（`ChartEditorApp.cs:240`）で
    `scrollTick` が追従するのは今までどおり。
  - **一時停止したらその時刻を `cursorTick` に書き戻す**（＝そこから再開できる）。
  - ■（停止）は `cursorTick` を動かさずその位置へ戻す（＝同じ箇所を繰り返し聴ける）。
- 貼り付け基準（§1）とキーボード移動の基準も、`scrollTick` ではなく `cursorTick` に寄せる。

**注意**: `ChartFormat.SecondsToTick` / `TickToSeconds` は `chart.bpmEvents` を引数に取り、
これは読み込み時に `song.bpmEvents` からコピーされる（`ChartSerializer.ReadChart`）。
BPM をエディタで編集した直後は `chart.bpmEvents` 側の再同期が要る点に注意（既存の `MarkPreviewDirty` 経路を確認すること）。

---

## 4. layerF による横移動をやめ、濃淡で高さを表す

**現状の問題**（ユーザー指摘そのもの）: `ChartEditorApp.cs:555`

```csharp
public float CombinedX(float layerF, float cellF) =>
    Mathf.Lerp(CellX(ground, cellF), CellX(sky, cellF), Mathf.Clamp01(layerF));
```

**横軸 1 本に cellF と layerF が混ざっている**。layerF を変えると cellF が同じでもノーツが横に動く。
これは §7.5 で高さレーンを作る根拠として既に指摘済みの構造欠陥だが、
**高さレーンを足しただけで本体側の混線は残っていた**。ユーザーの「sky のノーツは高さを変えても場所は変化しないはず」
は正しい。

**方針（採用案）**: **`CombinedX` の lerp を廃止し、x は純粋に cellF だけから決める。
layerF は「Ground ペインと Sky ペインそれぞれでのアルファ」で表す。**

```
xGround = CellX(L.ground, cellF)      // どちらのペインでも同じ cellF → 同じ相対位置
xSky    = CellX(L.sky,    cellF)
alphaGround = f(1 - layerF)
alphaSky    = f(layerF)
```

- Tap / Ex Tap / Flick は `layerF` が 0 か 1 なので、**片方のペインで α=1・もう片方で α=0** となり
  **現状の見た目と完全に一致する（回帰なし）**。変わるのは層を跨ぐ Slide だけ。
- 層を跨ぐ Slide は「Ground ペインで薄れていき、Sky ペインで濃くなっていく」という
  2 本の帯として描かれる。x が動かないので cellF の変化だけが横方向に見える＝**軸の意味が 1 対 1 になる**。
- α のカーブは線形だと中間が沈むので `Mathf.Pow(k, 0.7f)` 程度から始める（見た目の調整値）。
  ただし **α(0) は必ず 0**にする（Ground 専用ノーツが Sky ペインに薄く出てはいけない）。
- α が閾値（0.05 程度）未満のペインは描画自体を省く（Slide 1 本あたりの塗り面積が 2 倍になるため）。

**波及する箇所**:

- ヒットテスト（`ChartEditorApp.cs:1636`）と矩形選択（同 `:1282`）は、**両ペインの矩形を候補にする**。
  層を跨ぐ Slide は両方で掴める（見えている方を掴めばよい）。
- `PaneAt`（同 `:559`）はそのまま使える（ドラッグで層を変える手段として §7.4-B で実装済み）。
  むしろ **x が動かなくなることで「ペインをまたぐドラッグ＝層だけ変える」が直感的になる**。
- ガターを横切る描画が消える → §2 と合わせてガターは純粋な仕切りになる。

**残るリスク**: 半透明の帯が 2 本見えることで「同時押しの 2 本」と誤読される可能性。
高さレーン（§7.5）が正確な読み取り手段として既にあるので許容と判断するが、
実機で紛らわしければ「非主ペイン側は塗りではなく輪郭線で描く」に切り替える余地を残す。

---

## 5. Slide の編集モデル刷新

参照元でもっとも学ぶところが多い領域。4 つの要望に分けて設計する。

### 5.1 両端に tap の見た目の始点・終点を描く

参照元は `drawHoldNote()` の最後で **`drawNote(start)` / `drawNote(end)` を無条件に呼ぶ**
（`TimelineNotes.cpp:328-329`）。始点・終点は実体が独立した `Note` なので、tap と同じ描画関数で描かれる。

muses 側は `note.points.Count >= 2` の分岐（`ChartEditorApp.cs:714-738`）が帯と Visible 中継点しか描いていない。
**帯を描いた後に `points[0]` と `points[^1]` を、単発ノーツと同じ矩形（`:706-713` と同一形状）で
不透明に描く**だけでよい。中継点の白い小四角（`:736`）もこの機会に
「Visible＝実線の四角 / Invisible＝輪郭のみ」など区別を付けられる（参照元 `HoldStepType` の
Visible/Invisible/Ignored 3 値に相当、`TimelineNotes.cpp:287, 298`）。

### 5.2 帯の中では反応せず、始点・終点・中継点にだけ反応する ★最大の変更

**参照元の構造**: `updateNotes()`（`ScoreEditor.cpp:892-913`）は hold について
`updateNote(start)` / `updateNote(end)` / 各 `updateNote(mid)` を呼ぶだけで、
**帯（hold curve）に対するヒットテストは一切していない**。`updateNote()`（`TimelineNotes.cpp:14-157`）は
その 1 点の矩形に対して L（左端リサイズ）/ M（移動）/ R（右端リサイズ）の 3 つの不可視ボタンを置く。
選択ハイライト `drawSelectionBoxes()` も**選択された点ごと**に `drawHighlight` を描く（`ScoreEditor.cpp:1095-1104`）。
つまりユーザーの要望は、参照元ではそもそもその形になっている。

帯が使われるのは `findClosestHold()` → `isHoldPathInTick()`（`ScoreEditor.cpp:794-832, 1106-1124`）だけで、
用途は「**中継点追加ツールのときに、どの hold に足すかを決める**」の 1 点のみ。

**muses に必要な変更**:

1. **選択の粒度を点単位に下げる。** `readonly struct NoteRef { Note note; int index; }` を導入し、
   `selection` を `List<NoteRef>` にする。単発ノーツは `NoteRef(note, 0)` で表現できるので概念は増えない。
   - §7.4 で入れた「`selectedNote`（単一）を派生値として残す」構成は**そのまま活かせる**
     （`selection.Count == 1` のときだけ実体を指す、を `NoteRef` 版に読み替えるだけ）。
   - `HitTestNote` は `NoteRef?` を返すよう変更。多点ノーツは**帯の補間ではなく `points` の各矩形**とだけ当たる。
   - 黄色枠は選択された点にだけ描く（`ChartEditorApp.cs:740-749` の全体バウンディングボックスを廃止）。
2. **削除の意味を決める。** 参照元 `deleteSelected()`（`Editing.cpp:209-251`）は
   **始点/終点を消したら hold 全体を消し、中継点を消したらその点だけ消す**。この規則をそのまま採る。
3. **ドラッグは掴んだ点だけを動かす**（＝要望の「slide を一部分だけ調整」）。
   ノーツ全体を動かしたいときは矩形選択で全点を選ぶ。参照元と同じ操作体系になる。
4. **ドラッグ後の正規化が必須になる。** 参照元は `noteControl()` の release 時に
   **始点と終点の tick が逆転していたら swap し、`sortHoldSteps()` で中継点を tick 順に並べ直す**
   （`ScoreEditor.cpp:751-789`）。muses の `Note.points` は **tick 昇順であることを
   `InterpAtTick` / `ResolveSlideComboPoints` / `ChartSerializer` が暗黙に前提**しているので、
   点単位ドラッグを入れるなら**この正規化なしでは破綻する**。PointerUp で必ず走らせる。
5. **中継点追加ツールを「帯クリック」方式にできる。** 現状の `AddWaypoint`（`ChartEditorApp.cs:1075`）は
   「先に Slide を選択しておく」必要があるが、参照元の `findClosestHold()` 方式なら
   帯の上をクリックするだけで対象が決まる。帯のヒットテストはここでだけ生き残る、という整理になる。

**この項目は §5 の中で唯一データ構造に触るので、他の項目より先に着手する。**

### 5.3 始点・中継点のクリックで easing 変更、ドラッグで部分調整

**参照元の方式**（`TimelineNotes.cpp:101-129`）: `ImGui::IsItemDeactivated()`（＝マウスを離した瞬間）に
**`!isMovingNote`（＝ドラッグしていない＝ただのクリック）** かつ現在のツールが
`InsertLong` / `InsertLongMid` なら `cycleEase()`（Alt 併用なら `cycleStepType()`）を呼ぶ。
つまり「**その種別のツールを持った状態でクリック＝属性の巡回、ドラッグ＝移動**」という
1 つのボタンに 2 つの意味を持たせる操作体系。Flick 方向・critical 化も同じ仕組み（`InsertFlick` → `cycleFlicks()`）。

**muses での実装**:

- `EditorTool.Slide` を選んだ状態で既存 Slide の**始点または中継点**をクリック（ドラッグせず離す）→
  その waypoint の `easing` を巡回。ドラッグしたらその点の移動（§5.2-3）。
- **巡回対象は全 7 種ではなく 4 種程度に絞る**（`Linear → InOut → In → Out`）。
  muses の `Easing` は 7 値あり、クリック 7 回は実用的でない。全種はインスペクタで選ぶ。
  参照元も `cycleStepType` は 3 値（`Editing.cpp:494` の `% 3`）に留めている。
- 実装位置は PointerUp。「ドラッグしたか」は PointerDown 時の座標との距離で判定する
  （参照元の `isMovingNote` 相当のフラグを立てる）。
- 同じ枠組みで **Flick ツールで Tap をクリック → Flick 化** など、種別変更ショートカットも後から足せる。

### 5.4 斜め線・曲線を滑らかに描く

**現状**: 帯は `stepTicks`（約 4px）ごとの**軸並行矩形の積み重ね**（`ChartEditorApp.cs:716-729`）。
斜めになるほど階段状になる。参照元も同じく分割方式だが、
分割数を **`steps = ceilf(|endY - startY| / 10)` と画面上のピクセル距離で決めている**
（`TimelineNotes.cpp:175`）点が違う（muses は tick 基準なので、ズーム倍率によって粗密が変わる）。

ただし参照元は OpenGL でテクスチャ四角形を並べる方式なので階段は避けられない。
**UI Toolkit の `Painter2D` はベクタ描画でアンチエイリアスが効くため、参照元より良い方法が取れる**:

- **帯を 1 本の塗りつぶしパスにする。** 左端を上から下へ `LineTo` で辿り、右端を下から上へ辿って
  `ClosePath()` → `Fill()`。矩形の積み重ねでなく 1 つの多角形になるので、**斜辺が AA される**。
  現状の「矩形ごとに ±1px 重ねる」ごまかし（`:727` の `-1` / `+1`）も不要になる。
- **easing 区間は `Painter2D.BezierCurveTo` / `QuadraticCurveTo` が使える**（Unity 6.5 に存在を確認済み）。
  ただし muses の easing 7 種を厳密なベジェ制御点に落とすのは非自明なので、
  **まずは §7.5 の高さレーンで採った「約 6px 刻みの折れ線」（`ChartEditorApp.cs:820`）を
  帯にも適用**し、1 本のパスにするだけで十分滑らかになるはず。ベジェ化はその先の最適化。
- 高さレーンの `FillLine`（同 `:618`）も、`Painter2D.Stroke` に `lineJoin = Round` を設定した
  折れ線 1 本に置き換えると接合部の欠けが消える。

---

## 6. ノーツ画像（.png）の反映は可能か

**可能。ただし `Painter2D` ではなく別の API を使う。**

- `Painter2D` は色の塗りしか扱えず**テクスチャを貼れない**。
- `MeshGenerationContext.Allocate(int vertexCount, int indexCount, Texture texture)` が
  Unity 6.5 に存在することを確認済み（`UnityEngine.UIElementsModule.xml`）。
  頂点を自前で積んで `Vertex.uv` を設定すれば**テクスチャ付きの四角形を描ける**。
- **必須の注意点**: UI Toolkit は小さいテクスチャを動的アトラスへまとめるため、
  `MeshWriteData.uvRegion`（アセンブリ上に存在を確認済み、公式 XML ドキュメントには未記載）で
  UV をアトラス内の領域へ再マップしないと**別の画像が表示される**。
- 参照元は 1 枚のスプライトシート＋9 スライス相当の 3 分割（左端/中央伸縮/右端）で
  任意幅のノーツを描いている（`TimelineNotes.cpp:439-483`）。**同じ 3 分割方式が muses でも使える**
  （`width` が 0.1 セル〜複数セルまで可変なので、単純な引き伸ばしだと端が歪む）。
- **スタンドアロンビルドの制約**: `AssetDatabase` 等の Editor 専用 API は使えないので、
  画像は `Resources` 同梱にするか、実行時に `File.ReadAllBytes` + `ImageConversion.LoadImage` で
  外部 PNG を読む。後者なら**ユーザーが自作画像を差し替えられる**ようになり、
  editor-spec.md §2 の「素材確認 UI」とも噛み合う。
- **`Painter2D` と `Allocate` を同じ `generateVisualContent` 内で混在させたときの描画順**は
  未検証。TabView / IMGUIContainer で 2 度踏んだ「UI Toolkit の前提は実機で確認するまで信用しない」
  の教訓どおり、**実装前に小さい実機テストで確かめる**こと。

**今回は実装しない**（ユーザー確認済み）。着手時はこの節が出発点になる。

---

## 7. その他、参照元から移植する価値がある点

効率（＝譜面を打つ速さ）に効く順。

| # | 項目 | 参照元 | 内容と muses での価値 |
|---|---|---|---|
| 1 | **ノーツ SE の先読みスケジュール** | `ScoreEditor.h:121` `audioLookAhead = 0.1`、`ScoreEditor.cpp:418-485` | 参照元は**ノーツ時刻の 0.1 秒前**に音声イベントを積み、正確な時刻で鳴らす。muses の `PreviewSystem.PlayNoteSe`（`:281`）は「その時刻を跨いだフレームで `PlayOneShot`」なので**最大 1 フレーム遅れ＋ジッタ**がある。譜面の詰まり具合を耳で確認する用途では効く。 |
| 2 | **キーボードでのカーソル移動と自動スクロール** | `nextTick`/`previousTick`/`centerCursor`（`ScoreEditor.cpp:292-304, 382-407`） | ↑↓で 1 スナップ移動し、画面端に来たら自動スクロール。§3 の `cursorTick` 導入とセット。**打ち込み速度への寄与が最も大きい**。 |
| 3 | **再生追従スクロールの 2 モード** | `ScrollMode::Page` / `Smooth`（`EditorWindows.cpp:531-540`） | 現状は毎フレーム追従（Smooth 相当）のみ。Page（画面単位でめくる）は高速時に格段に読みやすく、実装も数行。 |
| 4 | **Undo 履歴に説明文字列** | `pushHistory("Paste notes", ...)`（`Editing.cpp:175` ほか） | メニューに「元に戻す: 貼り付け」と出せる。`UndoSnapshot` に `string label` を足すだけ。 |
| 5 | **左右反転（選択の反転／反転貼り付け）** | `flip()` / `flipPaste()`（`Editing.cpp:503-526, 92-102`） | muses では `cellF = Cells - cellF - width`。§1 のペーストモードの上に載る。 |
| 6 | **パターンプリセット** | `PresetManager.h`、`insertingPreset`（`ScoreEditor.cpp:936`） | よく使う形を保存して貼る。ゴースト描画の経路をペーストと共有しているので、§1 実装後なら追加コストが小さい。 |
| 7 | **小節ジャンプ** | `gotoMeasure()`（`ScoreEditor.cpp:409-416`） | 「#48 へ飛ぶ」入力欄。長い譜面で効く。 |
| 8 | **統計** | `ScoreStats`（`ScoreStats.h`） | 種別別ノーツ数と総コンボ。muses は `comboTimes` を持っているので**理論値の検算**にも使える。右パネルの「統計」セクションに追加。 |

**採らない**と判断したもの:
- ImGui の `InvisibleButton` を 3 つ並べる L/M/R 方式（`TimelineNotes.cpp:38-154`）は、
  UI Toolkit では要素を大量生成することになり不利。§7.4-D で入れた「端 ±4px を掴む」自前判定を維持する。
- SUS 入出力（`SUSIO.cpp`）は muses の独自形式と無関係。

---

## 8. 実装順

依存関係と「壊れたときの切り分けやすさ」で並べる。

1. **§2 ガターのグリッド線**（独立・数行・他に影響しない）
2. **§4 layerF の濃淡表現**（`CombinedX` を触るので §5 のヒットテスト変更より前にやる。
   Tap/Ex Tap/Flick に回帰が出ないことをここで確認しておく）
3. **§5.2 選択の点単位化 ＋ §5.1 両端の描画 ＋ ドラッグ後の tick 正規化**
   （データ構造に触る最大の塊。ここを先に通せば §5.3 は上に載るだけ）
4. **§5.3 クリックで easing 巡回**
5. **§3 再生位置カーソル（橙線）**
6. **§1 ペーストモード**（§3 の `cursorTick` を基準に使えるようにしてから）
7. **§5.4 帯の 1 パス化・平滑化**（見た目のみ。いつやってもよいが、§5.2 で描画分岐を触った後が無駄がない）
8. §7 の移植候補から、ユーザーが優先するものを選ぶ

---

## 9. ユーザーに確認したい未決事項

1. **§3 橙線の再生中の挙動**: 本書では「停止中はクリックで移動する再生開始位置、
   一時停止したらそこへ書き戻す、■ は動かさずそこへ戻る」と設計した。
   「橙線は再生開始位置に固定したまま動かさない（ループ開始マーカー的）」という解釈もありうる。
2. **§4 の濃淡カーブ**と、層を跨ぐ Slide が両ペインに出ることの是非（実機で見てからの判断でよい）。
3. **§5.2 の削除規則**: 「始点/終点を消したら Slide 全体、中継点なら 1 点だけ」で良いか
   （参照元の規則をそのまま採用した）。
4. **§5.3 の easing 巡回対象**を 4 種に絞ることの是非。
5. **§7 の移植候補**のうち、どれを次の増分に入れるか。

---

## 実装ログ（2026-08-01、同セッション内、Unity Editor未検証）

ユーザーから §9 の未決事項5件への回答を得たうえで、本書 §1〜§7・移植候補8件を全項目実装した。
**このセッションではUnity Editorが未起動のためコンパイル未確認**（brace/paren対応の静的チェックと
手動コードレビューのみ）。次回セッション冒頭でUnity Editorを開いてConsoleのエラー有無を必ず確認する。

**ユーザー回答（§9への回答、実装に反映済み）**:
1. 橙線・削除規則は提案どおり（参照元準拠）。
2. easing巡回は7種全部（インスペクタからも変更できるため巡回が7回でも許容、との判断）。
3. **高さ情報(layerFが変化)を持つSlideはSkyペインのみに描画**（Groundには一切描かない）。
   濃淡は0でも完全透明にしない（下限`HeightAlphaFloor=0.22`）。単発ノーツ・高さ変化の無いSlideは
   従来どおり自分の層のペインだけに不透明で描く（回帰なし）。

**§4/§5 (`ChartEditorApp.cs`)**:
- `SheetLayout.CombinedX`（layerFでlerp）を`NoteX(layerF, cellF, forceSky)`に置き換えた。
  `forceSky`は`HasHeightVariation(note)`（waypoint間でlayerFが変わるか）で決める。
- `NoteRef{Note note, int index}`を導入し、`selection`を`List<Note>`から`List<NoteRef>`（点単位）へ
  変更。`selectedNote`は`selection.Count==1`の時だけそのNoteを指す後方互換フィールドとして維持。
- `HitTestNote`を`HitTestPoint`（点のみ、選択/削除/ドラッグ用）と`HitTestSlideBand`
  （帯のみ、右クリックの「中継点を追加」専用、参照元`findClosestHold`相当）に分割。
- Slideの両端に常にTap同等の矩形（`DrawEndpointGlyph`）を描画。選択ハイライトは
  選択された点だけを囲む矩形に変更（旧: ノーツ全体のバウンディングボックス）。
- ドラッグは`dragOriginByRef: Dictionary<NoteRef, Waypoint>`で「掴んだ点だけ」を動かす。
  ドラッグ確定時(`OnSheetPointerUp`)に`NormalizePointsOrder`でtick順へ並べ替え
  （ドラッグ中は並べ替えない。参照元`noteControl`のswap/sortと同じタイミング）。
  Slideツールで既存の始点/中継点をクリック(ドラッグ無し)した場合は`easingCycleCandidate`経由で
  easingを巡回（全7種、`Enum.GetValues`で巡回）。ドラッグと判定する閾値は画面3px。
- 帯の描画は「高さ変化なし」なら1本の塗りつぶしパス(`FillBand`)、「高さ変化あり」なら
  区間ごとのquad(`FillQuad`、濃淡が変わるため1本のパスにできない)。

**§3 (`ChartEditorApp.cs`)**: `cursorTick`を追加。橙線は`FillTriangle`で左余白につまみを描画。
停止中はクリック/↑↓キーで移動、再生中は`preview.SongTime`が真の値
（`wasPlayingLastFrame`で遷移を検知し書き戻す）。▶は`TogglePlayFromCursor`で
必ず`cursorTick`位置から再生開始。■は`cursorTick`へ戻る(0ではない)。|◀/▶|は0/末尾へ。

**§1 (`ChartEditorApp.cs`)**: `pasting`フラグ方式のペーストモード。`CopySelectionToClipboard`で
tickを0正規化。`EnterPasteMode`→`DrawPasteGhost`（追従プレビュー）→`ConfirmPaste`
（左クリック確定）/`CancelPaste`（右クリック/Esc）。cellF/layerFは「Vを押した瞬間のホバー位置」
との差分を各ノーツへ加算（参照元`pasteLane`方式、動かさず確定すれば元位置を保つ非対称）。

**移植候補8件 (`PreviewSystem.cs`, `ChartEditorApp.cs`, `ChartEditorApp.UI.cs`, `ChartEditorRoot.uxml`)**:
1. SE先読み: `AudioSource.PlayScheduled`＋8個のプールで dspTime 基準の予約再生に変更
   （旧: `PlayOneShot`即時再生でフレーム単位の遅れ・ジッタがあった）。
2. ↑↓キーでのカーソル移動+自動スクロール: `OnSheetKeyDown`に追加（`notesSheet`にフォーカスがある間
   だけ反応するため、他のテキスト/数値入力欄の矢印キー操作とは衝突しない設計）。
3. 再生追従Page/Smooth: `ScrollFollowMode`列挙＋右パネルのトグル。
4. Undo履歴の説明文字列: `UndoSnapshot.label`＋`PushUndo(coalesce, label)`（デフォルト"編集"、
   主要な操作(配置/削除/貼り付け/種別変更等)にはその場で個別ラベルを渡した。既存の大半の
   `PushUndo(coalesce: false)`呼び出しはデフォルト値のままで動く=シグネチャ変更に伴う機械的な
   書き換えは不要）。メニュー/ボタンのツールチップに「元に戻す: {label}」を表示。
5. 左右反転: `FlipSelected()`（メニュー「選択を反転」）と`EnterPasteMode(flip:true)`
   （メニュー「反転して貼り付け」）。`FlipCellF`共通ヘルパー。
6. パターンプリセット: 右パネルに新規Foldout「プリセット」(`ChartEditorRoot.uxml`に追加)。
   **ディスク永続化は未実装**（アプリ実行中のみ有効、次回増分候補として明記）。
7. 小節ジャンプ: 右パネルに整数入力+ボタン。`SongAddr.ToTick(meters, measure, 1, 0)`。
8. 統計: **実装済みだった**（`BuildStatsText()`、`fold-stats`）。前回セッションで既に完了しており
   今回は変更不要と判明。

**既知の簡略化・次回確認すべき点**:
- パターンプリセットはメモリ内のみ（保存しても再起動で消える）。ディスク保存が要るかは
  ユーザー確認が必要。
- 反転貼り付けの鏡像基準は「盤面全体の中央」(`Cells - cellF - width`)。参照元は同じ基準。
- SE先読みのプール数(8)は経験的な値。同時に9個以上のノーツ音が0.1秒以内に重なる譜面では
  スケジュールが古いソースを上書きする可能性がある（実用上は稀という想定、実機で問題が
  出たら増やす）。
- **Unity Editor未検証**: コンパイルエラーの有無、実際の見た目・操作感（特にSlideの点単位選択・
  Sky限定描画・ペーストモード）はすべて次回セッションでの実機確認が必要。

## 関連

- `memory/editor-ui-redesign.md` — §1〜§7（実装済み）。本書はその続き。
- `memory/editor-spec.md` — Phase 4 機能仕様 rev.2。
- `memory/note-spec.md` — ノーツ仕様 rev.4。§4・§5 の layerF / Waypoint の意味の根拠。
- `memory/reference/MikuMikuWorld-master/` — 参照元ソース（本書の引用元）。
