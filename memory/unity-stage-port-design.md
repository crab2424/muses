# stage.ts → Unity 移植の設計（2026-07-30）

`web-prototype/src/stage.ts`（＋ `main.ts` のカメラ配置部分）を Unity/C# へ移す際の設計。
`StageConfig.cs` / `StageDerive.cs` は移植済み（Node 照合で数値一致確認済み）で、本書はその次の一手。

対象は **静的なステージ（床面・空中面・レーン境界線）とカメラ配置** のみ。
ノーツ（`notes.ts`）・判定帯オーバーレイ（`overlay.ts`）・入力（`input.ts`）は含まない。

---

## 1. まず決めるべき 4 点（結論）

| 論点 | 結論 |
|---|---|
| 座標系（Three は -z 奥、Unity は +z 奥） | **奥行き `d` をそのまま `z = +d` に置く**。`derive` の数式・`LaneX` は一切変更しない |
| レーン境界線の描き方（Unity に LineSegments 相当がない） | **`MeshTopology.Lines` の Mesh** で 1:1 移植。線幅が欲しくなったら板ポリ化（3-3 に移行案） |
| 奥行きカット・フェードの実装 | **専用シェーダ 1 本（`Muses/StageDepth`）を書く**。CPU 側で焼かない。理由は 3-4 |
| ロジックとシーンの分離 | **頂点生成は純粋な static クラス**（Node 照合できる形）、`MonoBehaviour` は Mesh/Material/GameObject の世話だけ |

---

## 2. 座標系とカメラ

### 2-1. Three → Unity の変換規則

Three.js は右手系でカメラが **-z** を向く。Unity は左手系でカメラが **+z** を向く。
どちらも「カメラのローカル +x が画面右、+y が画面上」なので、

> **Three のワールド点 `(x, y, -d)` は Unity の `(x, y, +d)` と同じ位置に見える。**

したがって変換は **奥行きの符号を反転するだけ**で、x も y も、`StageDerive` の数式も、
`LaneX` の戻り値も手を入れる必要がない。`stage.ts` の中で `-n0` / `-f0` と書いてある箇所を
Unity では `+n0` / `+f0` にする、それだけ。

カメラの向きも同様に符号だけ変わる:

| | Three | Unity |
|---|---|---|
| 位置 | `(0, cfg.yCam, 0)` | `(0, cfg.yCam, 0)` |
| 回転 | `rotation.x = -d.theta`（rad） | `localEulerAngles.x = +theta_deg` |

Unity は「X 回転が正 = 下を向く」なので符号が逆になる。`theta` は rad なので deg 変換が必要
（`StageDerive` は rad を返す。`Mathf.Rad2Deg * d.theta`）。

> **注意**: `Derived.theta` はクランプ後の値。`cfg.thetaDeg` をそのまま使ってはいけない。

### 2-2. カメラ設定の対応表

`main.ts` の `rebuild()` の該当部分をそのまま写す。

| Three | Unity |
|---|---|
| `camera.fov = cfg.phiDeg`（垂直画角） | `cam.fieldOfView = cfg.phiDeg` |
| `camera.aspect = w/h` | `cam.aspect`（読み取り側。derive の入力に使う） |
| `camera.near = max(0.01, d.zJudge*0.01)` | `cam.nearClipPlane = Mathf.Max(0.01f, d.zJudge*0.01f)` |
| `camera.far = d.drawFar * 1.5` | `cam.farClipPlane = d.drawFar * 1.5f` |
| `renderer.setClearColor(cfg.bgColor)` | `cam.clearFlags = SolidColor; cam.backgroundColor = ...`（色空間は 3-5） |

**Unity 固有の確認事項（Inspector で 1 度見るだけ）**
- `Camera > Physical Camera` を **オフ**（オンだと焦点距離とセンサーサイズ側が正になり `fieldOfView` の意味が変わる）。
- `fieldOfView` の **FOV Axis を Vertical**（Horizontal だと `phiDeg` の意味が変わる）。
- Projection は Perspective。

### 2-3. aspect の扱い（リビルドの契機）

Web は `window.resize` / `orientationchange` でリビルドしていた。Unity では相当するイベントがないので、
`Screen.width` / `Screen.height`（実際に使うのは `Camera.aspect`）を毎フレーム見て、
**前フレームと変わったらリビルド**する。iPad の回転・Editor の Game ビューのサイズ変更が同じ経路で拾える。

`cfg` を Inspector で編集したときも同じ経路に乗せたい（GUI 相当がまだ無い期間の調整手段になる）。
`OnValidate()` でダーティフラグを立てる形にする。

---

## 3. ジオメトリとマテリアル

### 3-1. `stage.ts` のロジック要約（移植対象）

層ごと（ground: `y=0, layerF=0` / sky: `y=d.skyHeight, layerF=1`）に:

1. `n0 = max(d.zJudge * 0.02, L.near)`、`f0 = d.zFar`。`f0 <= n0` ならその層はスキップ。
2. `xAt(u, z) = StageDerive.LaneX(cfg, d, u, L.layerF, z)`。
3. **面**: `fillAlpha > 0.001` のときだけ、4 頂点（近左・近右・遠右・遠左）＋三角形 2 枚。色は `grid` 色。
4. **レーン境界線**: `k = 0..cfg.cells` のうち `k % step == 0` または `k == cells` のものだけ、
   `u = -1 + 2k/cells` の縦線を近端→遠端で 1 本ずつ。`step = max(1, round(L.step))`。
5. `cfg.hardFarEdge` なら最遠端の横線 1 本を追加。線の色は `L.color`、alpha は **0.45 固定**。
6. `cfg.showLaneFloor == false` なら何も描かない。

`renderOrder` は面 0・線 1（＝線が後）。

### 3-2. 面の Mesh

`Mesh` に `vertices`（4）と `triangles`（`0,1,2, 0,2,3`）を入れるだけ。
Three 側の `computeVertexNormals()` は不要（アンリットなので法線を使わない）。

`bounds` は自動計算に任せる。ただし面が薄い（y 一定の平面）ので、
カメラ外判定で消えることは無い（判定線の手前〜最遠端まで広がるので画面を覆う）。

### 3-3. レーン境界線の Mesh

`Mesh.SetIndices(indices, MeshTopology.Lines, 0)` を使う。
頂点は `stage.ts` と同じ「2 頂点で 1 本」の並びなので、インデックスは `0,1,2,3,...` を順に並べるだけ。

- **利点**: `LineSegments` と 1:1。ジオメトリの数値を Node と直接照合できる。コードが最小。
- **制約**: 線幅は 1px 固定（プラットフォーム依存で太らせられない）。iOS/Metal では描画自体は可能。
  Retina の iPad では細く見えるので、Phase 2（視認性）で**板ポリ化**する可能性が高い。
- **移行の余地の作り方**: 「線 1 本 = (始点, 終点)」のリストを返すところまでを純ロジックにしておけば、
  板ポリ化はそのリストを消費する側の差し替えで済む。

### 3-4. シェーダ `Muses/StageDepth`

Three の `stageMaterial()` の移植。フラグメントで奥行きによる discard とフェードをする。

```
uColor  → _Color   (Color)
uAlpha  → _Alpha   (Float)
uNear   → _Near    (Float)
uFar    → _Far     (Float)
uHardFar→ _HardFar (Float, 0/1)
```

```hlsl
Shader "Muses/StageDepth"
{
    Properties
    {
        _Color   ("Color", Color) = (1,1,1,1)
        _Alpha   ("Alpha", Float) = 1
        _Near    ("Near Depth", Float) = 0
        _Far     ("Far Depth", Float) = 100
        _HardFar ("Hard Far Edge", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off            // Three の side: DoubleSide 相当
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float depth : TEXCOORD0; };

            float4 _Color; float _Alpha; float _Near; float _Far; float _HardFar;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 ws = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.depth = ws.z;   // Unity では +z が奥（2-1）
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (IN.depth > _Far || IN.depth < _Near) discard;
                float a = _Alpha * (_HardFar > 0.5 ? 1.0
                        : 1.0 - smoothstep(_Far * 0.55, _Far, IN.depth));
                if (a <= 0.001) discard;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
}
```

**なぜ CPU 側で焼かないか**: ジオメトリは既に `n0`〜`f0` に切って作るので discard は実質冗長で、
`hardFarEdge=false` のフェードだけがシェーダの実質的な仕事。頂点カラーで近似する手もあるが、
**このシェーダは `notes.ts` の移植でそのまま必要になる**（ノーツは毎フレーム動くので
カットは GPU 側でしかできない）。ここで書いておくのが結局安い。

**描画順**: `ZWrite Off` の半透明なので描画順が見た目を決める。`renderOrder` の 0/1 は
`material.renderQueue` の 3000（面）/ 3001（線）で表す。層をまたいだ前後関係
（ground の面と sky の面のどちらが先か）は Three では追加順（ground → sky）で決まっていたので、
**面 ground 3000 / 面 sky 3001 / 線 ground 3002 / 線 sky 3003** と 4 段に割るのが安全。

**マテリアルの持ち方**: シェーダ 1 本から実行時に 4 インスタンス（面×2、線×2）を作り、
`SetColor` / `SetFloat` で値を流す。Inspector で触る値は `StageConfig` 側にあるので、
Material をアセットとして 4 つ置く必要はない。
シェーダ参照は `[SerializeField] Shader stageShader;` にして Inspector で 1 度ドラッグする
（`Shader.Find` は Always Included Shaders への登録が要るので避ける）。

### 3-5. 色空間の落とし穴（実機で色がズレる原因になる）

このプロジェクトは **Linear color space**（`ProjectSettings.asset: m_ActiveColorSpace: 1`）。
`StageColors` の値（`0x8b5cf6` 等）は sRGB 表記。

- `Material.SetColor` は Linear 空間のとき **sRGB → Linear 変換を自動で行う**。
  `SetVector` は変換しない。**必ず `SetColor` を使い、シェーダ側のプロパティも `Color` 型で宣言する**
  （上のシェーダは `_Color ("Color", Color)`）。
- C# 側で `new Color(r/255f, g/255f, b/255f)` を作るのは正しい（sRGB 値として渡す）。
- 背景色 `cfg.bgColor`（`"#a0b298"`）は `ColorUtility.TryParseHtmlString` で取れる。
  `Camera.backgroundColor` も sRGB として扱われる。

半透明の合成が Web（Three）と Unity で完全一致するとは限らないので、
**色・alpha の一致は数値照合ではなくスクリーンショット比較で確認する**（5 章）。

---

## 4. クラス構成

```
Assets/Scripts/Stage/
  StageConfig.cs          （移植済み）
  StageDerive.cs          （移植済み）
  StageGeometry.cs        ★新規・純ロジック。cfg + Derived → 頂点/インデックス
  StageView.cs            ★新規・MonoBehaviour。Mesh / Material / 子GameObject の管理
  StageController.cs      ★新規・MonoBehaviour。main.ts の rebuild() 相当（カメラ＋View の統括）
  StageDeriveSmokeTest.cs （既存の使い捨て検証用）
Assets/Shaders/
  StageDepth.shader       ★新規
```

### `StageGeometry`（純ロジック・`MonoBehaviour` ではない）

```csharp
public struct LayerGeometry
{
    public bool visible;              // f0 <= n0 なら false
    public float y;
    public float near, far;           // n0, f0（シェーダの _Near/_Far に渡す）
    public Vector3[] planeVertices;   // 4 頂点。fillAlpha <= 0.001 なら null
    public int[] planeTriangles;
    public Vector3[] lineVertices;    // 2 頂点 = 1 本
    public int[] lineIndices;         // MeshTopology.Lines 用
}

public static class StageGeometry
{
    public static bool Build(StageConfig cfg, in Derived d, Layer layer,
                            out LayerGeometry geo);
}
```

`stage.ts` の `for (const L of layers)` の中身がそのままここに入る。
**`MonoBehaviour` から切り離す理由**: Node 照合（5 章）を Play 実行なしで回せるようにするため。
層ごとの見た目パラメータ（色・fillAlpha・step・near）は `cfg` と `d` と `Layer` から引ける
（`stage.ts` の `layers` 配列に相当する分岐だけ小さなヘルパにまとめる）。

### `StageView`

- 子 GameObject を 4 つ（`GroundPlane` / `GroundLines` / `SkyPlane` / `SkyLines`）を自分で生成し、
  `MeshFilter` + `MeshRenderer` を持たせる。**シーンに手で作らない**
  （`cfg` 変更でジオメトリが変わるので、シーン側に固定物を置く意味がない）。
- `Rebuild(cfg, d)`: `StageGeometry.Build` → `Mesh` へ流し込み → マテリアルへ uniform 反映。
  Mesh は `Clear()` して**作り直すのではなく再利用**する（Web の dispose/再生成と違い、
  Unity では毎回 `new Mesh()` すると GC を踏む）。
- `cfg.showLaneFloor == false` のときは 4 つとも `MeshRenderer.enabled = false`。
- **`[ExecuteAlways]` を付ける**。Play せずに Scene ビューでステージが見えるようになり、
  この先の調整サイクルが目に見えて速くなる（Editor 実行時の Mesh は `DestroyImmediate` 管理が必要な点だけ注意）。

### `StageController`

`main.ts` の `rebuild()` に相当。`cfg`（`StageConfig`、`[SerializeField]` で Inspector に出す）を持ち、

1. `d = StageDerive.Derive(cfg, cam.aspect)`
2. カメラへ反映（2-2）
3. `view.Rebuild(cfg, d)`
4. `Derived` を公開（後で `notes` / `overlay` / `input` が読む）

`Update()` で aspect 変化と `OnValidate` 由来のダーティフラグを見てリビルド。

---

## 5. 検証手順

`StageDerive` のときに確立した「Node で参照値を作って突き合わせる」方式を踏襲する。

1. **ジオメトリの数値照合**（自動化できる部分）
   Node 側で `stage.ts` と同じロジック・同じ `memory/settings.json` から頂点配列を計算し、JSON に出す。
   Unity 側は `StageGeometry.Build` の結果を JSON で吐く使い捨てスクリプトを置き、両者を diff。
   照合対象: 層ごとの `visible` / `n0` / `f0` / 面 4 頂点 / 線の本数 / 各線の始点終点。
   **`aspect` を揃えること**（Node 側に Unity の `cam.aspect` の実測値を渡す）。
2. **見た目の照合**（数値では追えない部分）
   同じ `settings.json` で Web プロトタイプと Unity を並べてスクリーンショット比較。
   ズレる可能性が高いのは **色（3-5 の色空間）・半透明の重なり・線の太さ**の 3 点で、
   いずれも「数式が合っているか」とは別問題なので、混ぜて扱わない。

---

## 6. Unity Editor 側の手作業（ユーザーがやる分）

コード編集だけでは終わらない部分。3 つだけ。

1. `Assets/Shaders/` フォルダに `StageDepth.shader` が入るので、**エラーが出ていないか Console を確認**
   （シェーダのコンパイルエラーは Play しなくても出る）。
2. `SampleScene` に空の GameObject を作り（名前は `Stage` など）、
   **`StageController` と `StageView` をアタッチ**。`StageController` の
   - `Camera` 欄に Main Camera をドラッグ
   - `Stage View` 欄に自分自身（同じ GameObject）をドラッグ
   - `Stage Shader` 欄に `StageDepth` シェーダをドラッグ
3. Main Camera の Inspector で **Physical Camera オフ / FOV Axis = Vertical / Projection = Perspective** を確認。
   （背景色はコードから設定するので触らなくてよい）

---

## 7. 今回はやらないこと（意図的な先送り）

- **線幅**（3-3）。1px で見づらければ Phase 2 で板ポリ化する。
- **GUI 相当のパラメータ調整 UI**。当面は Inspector 上の `StageConfig` を直接編集で足りる
  （`OnValidate` 経由で即リビルドされるので、Web の GUI に近い体験になる）。
- **設定の永続化**（ロードマップ Phase 0 の項目 G）。Unity では `StageConfig` を
  `ScriptableObject` にすればアセットとして保存できるので、そのときに設計する
  （今 `[Serializable] class` のままにしてあるのは、まだ決めていないため）。
- **フォグ・背景・視認性**（Phase 2）。`hardFarEdge=true` の現行設定ではフェードが効いていないので、
  シェーダのフェード経路は移植するが調整はしない。
- `notes.ts` / `overlay.ts` / `input.ts`。ただし `Muses/StageDepth` シェーダと
  `StageController` が公開する `Derived` は、その 3 つが乗る土台になる。
