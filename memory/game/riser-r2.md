# Riser / Diver r2 — 譜面エディタ対応と見た目の作り込み

`game-rework-r1.md` §3 で設計し、コミット `bbc9ae6` でゲーム側（データモデル・判定・handoff・
壁ジオメトリ）を実装した Riser/Diver の続き。この文書は **(A) 譜面エディタからの配置・編集**と
**(B) ゲーム側の見た目（半透明化＋矢印）**を扱う。

配置は `memory/game/` にした。エディタ側の変更が主だが、Riser/Diver という1機能を
エディタ・ゲーム両側で完結させる文書であり、`game-rework-r1.md` §3.9 が
「エディタ側を触るときに合わせて決めたい」と明示的に持ち越した未決事項を解決する続編にあたるため。

---

## 0. 前提（調査で確定した現状）

### 0.1 既に実装済みのもの（今回触らない）

| 領域 | 実装 | 場所 |
|---|---|---|
| データモデル | `NoteKind.Riser` / `Waypoint.layerTo` | `Chart/ChartNote.cs:13,109` |
| シリアライザ | `to=` オプション（`layerTo != layerF` のときだけ出力、省略時は `layerF` と同値） | `Chart/ChartSerializer.cs:345,370` |
| 判定 | `Judge.UpdateRiserPending`（方向制約つき Δv 閾値）、Ex巻き込み対象外、縦連は Flick と同扱い | `Gameplay/Judge.cs:562-620` |
| handoff | `Contact.layerHandoffUntil/To` + 終端層 `EnterEvent` の合成発火 | `TouchInput/Contact.cs:32`, `Judge.cs:609-619` |
| 壁の描画 | `PushRiserWall`（layerF 方向に12分割スイープする垂直な壁） | `Notes/NoteGeometry.cs:252-290` |
| 検証 | V7（layerTo 範囲外）/ V13（layerTo == layerF） | `Chart/ChartValidator.cs:83-90` |

**草案6「riser からそのまま slide に接続」は判定側が既に完成している**（handoff、note-spec §4.6.4）。
エディタ側で追加実装は不要で、**終端層に Slide 始点を置くだけで成立する**。
「複雑なら見送り」という懸念は解消済み。

### 0.2 エディタ側は完全に未対応

`ChartEditorApp` は Riser を一切扱えない。`EditorTool`(`:44`) に項目が無く、
`NoteColor`(`:3410`) は Riser で `Color.white` に落ち、右クリックの種別変更(`:2444-2446`)は
Tap/ExTap/Flick のみ、`BuildWaypointRows` に `layerTo` 行が無い。
**エディタから Riser を含む譜面は作れず、読み込んでも白い矩形として描かれるだけ。**

### 0.3 実装に効く発見（重要）

1. **`Note.shader` は頂点色の alpha を使っていない。**
   フラグメントは `a = IN.state * aFar * aNear` で、`Varyings.color` が `float3` のため
   `IN.color.a` はそもそも渡っていない（`Note.shader:60,79,86`）。
   **頂点色を薄くしても半透明にならない**ので、草案2（半透明化）にはシェーダ修正が要る（§3.1）。
   既存ノーツの頂点色は全て alpha=1（`Color` の3引数ctor / `ColorFromHex(hex, alpha=1f)` /
   `Color.white`）なので、`a *= IN.color.a` を足しても**回帰しない**ことを確認済み。

2. **高さレーンのドラッグは単発ノーツの layerF を 0/1 にスナップする。**
   `ChartEditorApp.cs:2612` の
   `wp.layerF = points.Count == 1 ? Mathf.Round(layer) : layer;` がそれで、
   Riser も `points.Count == 1` なので**このままでは部分移動（`layerF=0, layerTo=0.5` 等）が作れない**。
   note-spec §4.6.1 が明示的に許している仕様なので、Riser を例外にする必要がある（§6.3）。

3. **`heightDragPointIndex = -1` は「ドラッグ中でない」の番兵として既に使われている**
   （`:349, :440, :2743`）。layerTo ハンドルを `index = -1` で表す案は使えず、別フィールドが要る（§6.2）。

---

## 1. 確定事項（ユーザー決定、2026-08-06）

| # | 決定 |
|---|---|
| 1 | **Riser/Diver は独立ノーツのまま**（`NoteKind.Riser` + `layerTo`）。データモデル変更なし。Tap に重ねた場合は 2ノーツ・2コンボ |
| 2 | エディタのノーツシートでは **開始層のペインにだけ描き、矢印パターンで方向を示す** |
| 3 | ゲーム側の壁には **中央に大きい矢印を1つ** 乗せる |
| 4 | 配置ツールは **1ツール「層移動」**。Ground クリック→上昇(Riser)、Sky クリック→下降(Diver) |

決定1により `game-rework-r1.md` §3.9-1 / note-spec §9-6 の未決事項
「Diver を独立 kind にするか」は **`layerTo` の大小で表す方式で確定**（現行実装のまま）。
UI 上は「Riser（上昇）」「Diver（下降）」と別名で見せるが、内部は同一 kind。

---

## 2. データモデル：変更なし（確認結果）

決定1により `ChartNote.cs` / `ChartSerializer.cs` / `ChartFormat.cs` は**一切変更しない**。
既存の編集機能が自動的に Riser へ追従することも確認済み：

- **Undo / コピペ**: `Waypoint` は `struct`（`ChartNote.cs:104`）で、`CloneChart` は
  `new List<Waypoint>(n.points)`（`:894`）と値コピーしている。`layerTo` は自動的に保存・復元される。**変更不要**。
- **左右反転**: `FlipSelected` / `FlipCellF`（`:3059-3073`）は `cellF` しか触らず、
  `layerF`/`layerTo` は不変。Riser でも正しく動く。**変更不要**。
- **幅変更（端ドラッグ）**: `EdgeGrabSign`（`:2558`）は点の `layerF`/`cellF`/`width` だけを見るので
  Riser も 1点ノーツとしてそのまま動く。**変更不要**。
- **矩形選択**: `HitTestPointsInRect`（`:2572`）は `HasHeightVariation`（`:3426`、`points.Count < 2` で false）
  により Riser を開始層ペインで当てる。決定2と整合。**変更不要**。

---

## 3. ゲーム側の見た目

### 3.1 半透明化（`Note.shader`）

草案2「譜面を覆う構造のため半透明にすべき」への対応。§0.3-1 のとおりシェーダ修正が必須。

```hlsl
// Varyings: float3 color → float4 color
OUT.color = IN.color;                       // vert: .rgb だけ拾っていたのをやめる
...
float a = IN.state * aFar * aNear * IN.color.a;   // frag: 頂点色の alpha を乗せる
if (a <= 0.003) discard;
return half4(IN.color.rgb * (0.7 + 0.6 * IN.state), a);
```

- 既存ノーツは全て alpha=1 なので**見た目は完全に不変**（§0.3-1）。
- この変更で「頂点色の alpha で個別に透過度を決める」という手段が全ノーツ種別に開く。
  Riser の壁以外にも今後使える（例: 未判定ノーツのフェード演出）。

**壁の alpha は 0.35 を初期値**とする（`NoteGeometry` の色定数側で指定）。
実機で「後ろの譜面が読めるか」と「壁の存在感」を見て調整する前提の仮値。

### 3.2 矢印ジオメトリ（`NoteGeometry.PushRiserWall`）

決定3どおり**壁の中央に大きい矢印（シェブロン）を1つ**。

**なぜジオメトリで描くか**: `Note.shader` は `ZWrite Off` + `ZTest Always` + 単一メッシュ
（`NotesRenderQueue = 3010`）なので、**壁の後に矢印の三角形を積むだけで確実に手前に描かれる**。
renderQueue の追加もシェーダの改造も UV チャンネルの追加も要らない。
（フラグメントで手続き的に描く案は、シェブロン形状に2次元のローカル座標が必要で
`uv3` の新設を伴う。壁の分割数に依らず綺麗になる利点はあるが、今回のように矢印が1つなら
利点が小さいので採らない。）

`PushRiserWall` 内、既存の壁 quad を全部積んだ**後**に、既存の `EmitQuad` と同じ積み方で
腕2本（＝quad 2枚、4三角形、12頂点）を追加する。座標は壁と同じ `(u, layerF)` 空間で指定すれば、
頂点シェーダの `PlaceNote` が層ごとのレーン収束補正 `c` を含めて正しく変形してくれる。

```
uc      = (u0 + u1) * 0.5
halfW   = (u1 - u0) * 0.30          // ノーツ幅の 60% を矢印の横幅にする
dir     = sign(layerTo - layerF)    // +1: 上昇(Riser) / -1: 下降(Diver)
span    = abs(layerTo - layerF)
lMid    = (layerF + layerTo) * 0.5
armH    = span * 0.34               // 矢印全体の layer 方向の高さ
thick   = span * 0.16               // 腕の太さ
lTip    = lMid + dir * armH * 0.5   // 先端（進行方向側）
lBase   = lMid - dir * armH * 0.5

左腕 quad: (uc-halfW, lBase) (uc-halfW, lBase+dir*thick) (uc, lTip+dir*thick) (uc, lTip)
右腕 quad: (uc+halfW, lBase) (uc+halfW, lBase+dir*thick) (uc, lTip+dir*thick) (uc, lTip)
```

- 係数がすべて `span` 比例なので、**部分移動（`layerF=0 → layerTo=0.5`）でも矢印が潰れず相似に縮む**。
- 矢印の色は**白・alpha 0.9**（壁の 0.35 に対して十分なコントラスト）。添付画像の
  「白い縁取り＋濃い塗り」の縁取り側に相当する。
- **y オフセットは付けない**。壁と同じ平面に置き、前後関係は積む順序だけで決める
  （オフセットを付けると壁と矢印が視差でずれて見える）。

### 3.3 色

| 用途 | 値 | 備考 |
|---|---|---|
| Riser（上昇） | `#4AFFA0` alpha 0.35 | 既存 `cRiser`（`NoteGeometry.cs:100`）を流用し alpha だけ足す |
| Diver（下降） | `#C86AFF` alpha 0.35 | **新規** |
| 矢印 | `#FFFFFF` alpha 0.9 | Riser/Diver 共通 |

**Diver をマゼンタ(#FF4AC8 系)にしない理由**: ゲーム側の `cFlick` が既に `#FF4AC8` で、
添付画像どおりのマゼンタにすると Flick と混同する。色相を紫寄りにずらした `#C86AFF` を仮採用する。
実機で見分けが付くかは要確認（§11-1）。

---

## 4. エディタ：配置ツール「層移動」

決定4により**ツールは1つ**。`EditorTool`（`ChartEditorApp.cs:44`）に `LayerMove` を追加する。

```
enum EditorTool { Select, Tap, ExTap, Slide, Flick, LayerMove, AddWaypoint, Delete, Event }
```

**配置の挙動**（`OnSheetPointerDown` の配置分岐に追加）:

| クリックしたペイン | 生成される Waypoint |
|---|---|
| Ground | `layerF = 0, layerTo = 1`（上昇＝Riser） |
| Sky | `layerF = 1, layerTo = 0`（下降＝Diver） |

- `kind = NoteKind.Riser`、`cellF`/`width` は Tap 配置と同じロジック（現在の既定幅・スナップ）を流用。
- **部分移動は配置時には作れない**。置いた後に高さレーンで `layerTo` を調整する運用（§6）。
- **既存ノーツの点を踏んだら選択に横取り**する（r7 §1 で他の配置ツールに入れた規則をそのまま適用）。
  これがあることで「Tap の上に Riser を重ねる」操作では、Tap を選択してしまわないよう
  §7.1 のインスペクタ経由の付与を使うか、少しずらしてから高さレーンで揃えることになる
  → **重ねる操作はインスペクタからの付与（§7.1）が主動線**という位置づけにする。
- ツールバーのラベルは「層移動⇕」。数字キーによるツール切替（r5 §3）にも登録する。

**配置ゴースト**（`DrawPlacementGhost`、`:1870`）: カーソルのあるペインに応じて
上向き（Ground）／下向き（Sky）の矢印パターン付き矩形を出す。§5 の本描画と同じ形にする
（r5 で確立した「ゴーストと実際の配置位置・形状を一致させる」原則）。

---

## 5. エディタ：ノーツシートの描画

決定2どおり**開始層（`layerF`）のペインにだけ**描く。`HasHeightVariation` は
`points.Count < 2` で false を返す（`:3426`）ので、既存の `forceSky` 判定に手を入れる必要はない。

### 5.1 描き方

`GenerateNotesSheet` の 1点ノーツ分岐（`:1609-1616`）に Riser 用の分岐を足す。
現状は `FillRect(..., col)` の1行（不透明な 8px の矩形）。Riser は「他ノーツの上に重ねる」用途があるため、
**下のノーツが透ける描き方**にする:

1. 薄い塗り: `FillRect(rect, col with alpha 0.28)`
2. 枠線: `FillRectOutline(rect, col, 1.5f)`（不透明）
3. 矢印パターン: 矩形の内側に白い小三角（7px 幅 × 6px 高、9px 間隔）を**横方向に反復**。
   個数 = `max(1, floor((x1 - x0 - 2) / 9))`、矩形中央に寄せて配置。向きは
   `layerTo > layerF` なら上向き、そうでなければ下向き。

矩形の高さは既存どおり `y ± 4`（8px）を維持する。**時間軸方向には張り出さない**
（張り出すと「tick がずれている」ように見え、重ね置きの判断を誤らせるため）。

Painter2D には三角形を描くヘルパーが無いので `FillTriangle(p, a, b, c, col)` を新設する
（`FillQuad` と同じ `BeginPath`/`LineTo`/`Fill` の形）。

### 5.2 色関数の拡張

`NoteColor(NoteKind)`（`:3410`）は kind しか受け取らないため、Riser と Diver を区別できない。
**`NoteColor(Note note)` のオーバーロードを新設**し、`kind == Riser` のとき
`layerTo > layerF ? Riser色 : Diver色` を返す。既存の呼び出し箇所
（`:1608` シート本体、`:1800`/`:1806` 高さレーン、ゴースト）をこちらへ差し替える。
`NoteColor(NoteKind)` は複数選択の一括変更 UI など Note を持たない箇所のために残す。

エディタ側の色は §3.3 のゲーム側と同じ値を使う（Riser `#4AFFA0` / Diver `#C86AFF`、
シート上は alpha を用途別に指定）。

> **注意**: エディタの Slide 色は `(0.4, 0.9, 0.6) = #66E699` で、Riser の `#4AFFA0` と色相が近い。
> ただし Slide は「長い帯」、Riser は「矢印パターン入りの単発矩形」で形状が明確に違うため、
> 実用上は識別できると判断する。紛らわしければ調整（§11-1）。

---

## 6. エディタ：高さレーン（`layerTo` の指定）

草案4「高さレーンで2点（始点・移動先）を真横に描画して移動先を指定」に対応。

### 6.1 描画（`DrawHeightCurve`、`:1811`）

Riser は `points.Count == 1` なので、現状は点が1つ描かれるだけで `layerTo` が見えない。
`kind == Riser` のとき、**同じ y（同じ tick）に2点を横並びで描く**:

- 始点: `x = LayerToX(layerF)`、既存と同じ 8px の四角
- 終点: `x = LayerToX(layerTo)`、**矢じり（三角）**で描く（点と区別する）
- 2点を結ぶ**水平線**（2px）。線と矢じりの向きで移動方向が読める

選択状態による濃淡（`selected` で alpha を 0.28 / 1.0 に切り替え）は既存の規則をそのまま適用する。

### 6.2 掴む・ドラッグする（`HandleHeightLanePointerDown`、`:2490`）

- `FindClosestHeightPoint` に **layerTo ハンドルも候補として含める**。
- **§0.3-3 のとおり `heightDragPointIndex = -1` は「ドラッグ中でない」の番兵として使用中**なので、
  layerTo ハンドルの識別には**専用の bool フィールド `heightDragTargetIsLayerTo` を新設**する
  （`FindClosestHeightPoint` の戻り値にも同じフラグを足す）。
- **選択の粒度は変えない**。layerTo は Waypoint ではないので `NoteRef` には載せず、
  layerTo ハンドルを掴んだときの選択は `NoteRef(note, 0)`（実体の点）とする。
- easing 巡回（`heightEasingCycleCandidate`）は Riser では常に無効（区間を持たないため）。

### 6.3 ドラッグの適用（`:2605-2617`）

現行の1行を、**Riser を連続値の例外にする**形へ変更する（§0.3-2）:

```csharp
var wp = heightDragNote.points[heightDragPointIndex];
float layer = L.XToLayer(pos.x);
// Riser は note-spec §4.6.1 で部分的な層移動を許しているため、単発ノーツだが連続値を通す。
bool continuous = heightDragNote.kind == NoteKind.Riser || heightDragNote.points.Count > 1;
float v = continuous ? layer : Mathf.Round(layer);
if (heightDragTargetIsLayerTo) wp.layerTo = Mathf.Clamp01(v);
else                          wp.layerF  = Mathf.Clamp01(v);
heightDragNote.points[heightDragPointIndex] = wp;
```

- `layerF == layerTo` になるのは**止めない**（既存の V13 警告に任せる）。
  ドラッグ中に値をクランプで捻じ曲げるより、検証で気付かせるほうが既存の設計と一貫する。
- `Mathf.Clamp01` は新規に追加する（現行は範囲外を許して V7 で警告している。
  ただし高さレーンは物理的に 0〜1 の帯なので、ここで入る値は元々範囲内。保険）。

### 6.4 シート本体のドラッグで層を移したとき

Riser を Ground ペインから Sky ペインへドラッグすると `layerF` が 0→1 になる。
このとき `layerTo` を放置すると `layerF == layerTo == 1` になり V13 警告に落ちる。

**規則**: ドラッグで `layerF` が変わったとき、
**`layerTo` が 0 または 1（＝全移動）のときだけ反対側へ自動反転**し、
中間値（＝手で設定した部分移動）はそのまま保持する。

- 既定のまま置いた Riser を別の層へ移す操作は自然に追従する。
- 高さレーンで調整した部分移動の値は勝手に壊されない。
- 部分移動を保持したまま層を移すと `layerF == layerTo` になりうるが、その場合は V13 が拾う。

---

## 7. エディタ：インスペクタと右クリックメニュー

### 7.1 インスペクタの「層移動」ドロップダウン（草案5）

`RebuildInspector`（`ChartEditorApp.UI.cs:1068`）の単一選択パスに追加する。

**(a) 選択が Tap / Ex Tap / Flick（1点ノーツ）のとき** — 「層移動」行を出す:

| 選択肢 | 動作 |
|---|---|
| 無し | 同位置の Riser ノーツがあれば削除 |
| 上昇（Riser） | 同位置に `kind=Riser, layerF=選択ノーツのlayerF, layerTo=(layerF<0.5 ? 1 : 0)` のノーツを生成。既にあれば方向だけ更新 |
| 下降（Diver） | 同上（`layerTo` が逆） |

生成する Riser の `tick` / `cellF` / `width` / `scrollGroup` は選択ノーツからコピーする。
`PushUndo(coalesce: false, "層移動を付与")` を先に呼ぶ。

**「同位置」の判定と、その割り切り**:
決定1（独立ノーツ）を採ったので、Tap と Riser を結ぶ**明示的なリンクは持たない**。
ペアの探索は「同 tick かつ `cellF`・`width` が一致する `kind==Riser` のノーツ」で毎回行う。

> **帰結（設計上の割り切り）**: 付与した後に Tap だけをドラッグで動かすとペア関係は切れ、
> ドロップダウンの表示は「無し」に戻る（Riser はその場に残る）。
> これは独立ノーツ方式の必然で、**ドロップダウンは「現在の状態」ではなく
> 「その場で生成/削除するアクション」に近い**という位置づけになる。
> 実運用では、重なった2ノーツは**矩形選択で両方まとめて掴める**ため一緒に動かせる。
> 明示リンクを導入するかは §11-2 に残す。

**(b) 選択が Riser のとき** — 「種別: Riser」ラベルの代わりに以下を出す:

- 「種別: 層移動（上昇 / 下降）」ラベル
- **方向** ドロップダウン: 上昇(Riser) / 下降(Diver) → `layerTo` を `1` / `0` に設定
- **移動先 layerTo** の `FloatField`（0〜1）→ 部分移動をキーボードから直接入力できる
  （高さレーンのドラッグと同じ値。`BuildWaypointRows`(`UI.cs:1176`) に Riser 限定行として追加）

「無し」は (b) には出さない（Riser 自体の削除は既存の「このノーツを削除」ボタンが担う）。

### 7.2 複数選択時の一括変更

`RebuildInspector` の複数選択パス（`UI.cs:1103`）の種別ドロップダウン
（現状 Tap / Ex Tap / Flick）に **上昇(Riser) / 下降(Diver)** を追加する。
選択が全て1点ノーツのときのみ表示される既存条件（`allSinglePoint`）がそのまま使える。
Riser へ変換する場合は `layerTo` も §7.3 の規則で設定する。

### 7.3 右クリックメニューの種別変更（`:2444-2446`）

「Riser（上昇）に変更」「Diver（下降）に変更」を追加する。

`ChangeNoteKind`（`:2547`）は `note.kind == kind` で早期 return するため、
**Riser ⇄ Diver（kind が同じで layerTo だけ違う）はこの関数を通せない**。
`ChangeNoteKind(Note, NoteKind, float? layerTo = null)` に拡張し、

- 他種別 → Riser: `layerTo = (layerF < 0.5 ? 1 : 0)`
- Riser → 他種別: **`layerTo = layerF` に戻す**（V13 警告を残さないため。必須）
- Riser → Riser（方向反転）: 早期 return の条件を `kind が同じ かつ layerTo も同じ` に緩める

---

## 8. 既存機能との相互作用（まとめ）

| 機能 | 対応 |
|---|---|
| Undo / Redo・コピペ・左右反転・幅変更・矩形選択 | **変更不要**（§2 で確認済み） |
| AddWaypoint ツール | Riser は `points.Count == 1` 固定。`HitTestSlideBand` が Slide のみを対象にしているため自然に除外される。念のため Riser を明示的に対象外にする |
| 3Dプレビュー | `PreviewSystem` は `NoteView`/`NoteGeometry` をそのまま使うので、§3 の変更が自動で反映される。**追加作業なし** |
| ゲーム本体 | 同上。§3 のシェーダ変更は全ノーツ共通だが alpha=1 のため回帰なし |

---

## 9. 検証（`ChartValidator`）

- **V7 / V13 は既存のまま**で足りる（範囲外・移動なし）。
- **今回は追加しない**: 「Riser の終端層に handoff 窓（仮 200ms）内で始まる Slide 始点があるか」の
  情報レベル検証は有用だが、`handoffWindowMs` が実機未確定の仮値のため、
  値が固まってから入れる（§11-3）。

---

## 10. 実装順序

依存関係が薄いので分割してコミットできる。確認しやすさの順:

1. **§3 ゲーム側の見た目**（シェーダ alpha → 矢印ジオメトリ → Diver 色）。
   `dotnet build` で確認でき、エディタの3Dプレビューにも自動で出るので、
   **エディタから Riser を置けるようになる前に見た目を固められる**。
   確認にはデモ譜面（`ChartBuilder`）へ Riser / Diver を1本ずつ追加する。
2. **§5 シート描画 + §5.2 色関数**（読み込んだ Riser が正しく見えるところまで）。
3. **§4 配置ツール**（置けるようになる）。
4. **§6 高さレーン**（`layerTo` を編集できるようになる）。§6.3 の連続値例外を忘れない。
5. **§7 インスペクタ・右クリックメニュー**（重ね付与の主動線）。
6. **§6.4 層ドラッグ時の layerTo 自動反転**（細かい追従、最後でよい）。

`Judge` は純粋 C# クラスなので、判定側の回帰は `JudgeSmokeTest`（Riser のシナリオは
`bbc9ae6` で追加済み）と「スクラッチへ複製して `dotnet run`」で確認できる。
ただし今回の変更は判定に触れないため、主な検証は**実機（またはエディタ Play）での目視**になる。

---

## 11. 未決事項（実装前または実機確認時に決める）

1. **Diver の色 `#C86AFF`** が、ゲーム側で Flick(`#FF4AC8`)と、エディタ側で Slide(`#66E699`)と
   十分に見分けられるか。実機で紛らわしければ調整する（Flick の色は `NoteGeometry.cs:98` に
   「仮の専用色」と明記されており、そちらを動かす選択肢もある）。
2. **Tap を動かしたときに重ねた Riser を追従させるか**（§7.1 の割り切り）。
   追従させるなら `Note` に明示リンク（例 `attachedTo`）が要り、決定1の「データモデル変更なし」から外れる。
   まず現行方式（矩形選択で両方掴む）で運用してみて、不便なら再検討する。
3. **`riserReachFrac`（仮 1.0）/ `handoffWindowMs`（仮 200ms）の実値**。
   note-spec §4.6.7 の未決事項がそのまま残っている。iPad 実機で「Riser の後、指が自然にどこで止まるか」を
   見てから決める。§9 の追加検証もこれ待ち。
4. **壁の alpha 0.35 と矢印の比率（`armH`/`thick` の係数）**。実機で後ろの譜面の可読性を見て調整。
5. **Riser 連打の最小間隔レギュレーション**（note-spec §9-8 から継続）。
   40mm のストロークを要求するので高 BPM の連打には使えない。譜面制作時の制約として要検討。
