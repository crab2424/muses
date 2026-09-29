# Slide / Riser デザイン比較ラボ

Tap ラボ（`../note-tap`）と同じステージ再現の上で、**Slide（帯）と Riser（層を上る壁）** の見た目案を並べて比較する。
Diver（下降）は今回対象外。文脈として Tap も出るが、Tap は全タイル共通で現行デザイン。

## 起動

`.claude/launch.json` の `design-lab`（= `npm --prefix web-prototype exec -- vite design-lab --port 5181`）→
http://localhost:5181/note-long/ 。`?v=a.js,b.js` で絞り込み、`?lite=1` で軽量モード。

## 仕組み（Tap ラボとの違い）

Slide は時間方向に長い帯、Riser は層方向の壁なので、**Unity と同じく頂点シェーダで配置する**。
メッシュは生成時に「ノーツ空間」の値で頂点を焼き、`ctx.glsl.place` の `musePlace()` が毎フレーム
`uSongTime` からワールド座標を求める（`NotePlacement.hlsl` の移植、空中の奥行き再マップ込み）。
**ここで作った頂点の積み方とシェーダは、ほぼそのまま Unity の `NoteGeometry` / `Note.shader` に持ち込める。**

## バリアントの書き方（契約）

```js
export default {
  id, name, model, concept, unityCost,   // Tap ラボと同じ（日本語）
  create(ctx) {
    return {
      makeSlide(note) { return object3D; },  // Slide 1本（生成時に1回）
      makeRiser(note) { return object3D; },  // Riser 1個
      beforeRender({ t, dt }) {},            // 任意
    };
  },
};
```

`ctx = { THREE, cfg, d, colors, uniforms, glsl: { place, clip }, sampleSlide, uAt, EASE, envMap, renderer }`

- `note`（Slide）: `{ kind:'slide', points: [{ t, cellF, width, layerF, easing, easingH, marker }] }`
  - `t` 秒、`cellF` 左端セル（0..12）、`width` セル数、`layerF` 0=地上〜1=空中（連続値、層を跨ぐ Slide あり）
  - `marker: 'visible'` の点は中継点マーカーを描く。**始点は常に描く**（Tap と同じ時刻の「押し始め」）
  - `sampleSlide(note, step=0.03)` → `[{ t, cellF, width, layerF, seg, k }]`（easing 込みで時間方向に標本化）
- `note`（Riser）: `{ kind:'riser', t, cellF, width, layerF(=0), layerTo(=1 または 0.5), dir(+1) }`
  - **1つの時刻にある、判定線に平行な垂直の壁**（地上 layerF → 空中 layerTo）
- `uAt(cell)` → レーン座標 u（-1..+1）
- **頂点**: `position` 属性に `(u, layerF, t)` を入れ、追加属性は自由。頂点シェーダで
  `${ctx.glsl.place}` を差し込み、`vec3 wp = musePlace(u, layerF, t, dz, yUp, depth, scale);`
  → `gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);`
  - `dz`: 奥行き方向のずらし（+ で奥、地上判定線上のワールド単位。Tap の半厚み = `d.zJudge * cfg.thicknessFrac` ≈ 0.30）
  - `yUp`: 面からの高さ（地上判定線上のワールド単位。空中・遠方では自動で縮む）
- **フラグメント**: `${ctx.glsl.clip}` を差し込み、冒頭で `museClip(depth, layerF, 1.0);`
  （最遠端より奥・手前端より手前・判定線を越えた部分＝オートプレイで食べた部分を捨てる）。
  出力は `gl_FragColor = museOut(linearRgb, alpha, add);`（premultiplied）で、マテリアルは
  `blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor`。
  **`museOut` を使う場合は `#include <colorspace_fragment>` を書かない**。
- uniform は `{ ...ctx.uniforms, 自分の uniform }` で展開して共有する（`uSongTime` をハーネスが毎フレーム更新）。
- マテリアルはバリアント内で使い回す。ジオメトリはノーツごとに作ってよい（画面外に出たらハーネスが dispose）。
- 見本: `variants/00-current.js`（現行の再現）。

## 前提（Unity 本体の現状と制約）

- 色: Slide 地上 `#35e8ff`（水色）、Slide 空中 `#4affa0`（緑）、Riser `#4affa0`（空中 Slide と意図的に同色）、
  Tap `#4aa3ff`。層を跨ぐ Slide は layerF で色を連続補間。
- 地上帯は「暗めの半透明＋重なるほど明るく」（α0.30 + 加算0.22、Unity では重なり4枚まででステンシル打ち止め）、
  空中帯は α0.35 の透明（重なるほど緑に飽和）。
- 帯の左右端と中央にスクリーン空間一定幅の白線（fwidth）。遠方でも消えないのが要件。
- Riser は以前の「大面積の半透明の壁」が重い（ZTest Always でオーバードロー）＋見づらいという理由で、
  **縁線2本＋白い矢印3つ**に減らした経緯がある（`memory/game/note-visual-r1.md` §6）。壁の塗りを戻すなら理由を書く。
- Unity は全ノーツを 1 メッシュ・`ZWrite Off / ZTest Always`・メッシュ順で描く。帯同士の重なり順はノーツ順で決まる。
- Tap ラボでの結論（2026-09-29 時点）: ユーザーは Sonnet 5.5 の「キーキャップ」「ネオンドーム」（立体感のある Tap）を好んだ。
  Slide の始点・中継点マーカーは Tap と「同素材・別色」の方針（note-visual-r1 §7）。

## 評価観点

1. 帯の範囲（幅・左右端）と中央（なぞる位置）が一目で読めるか。曲線・層跨ぎでも追えるか
2. 地上の帯の重なり、空中の半透明帯の重なりが破綻しないか
3. 帯の上を Tap が通るときに Tap が埋もれないか（密集）
4. Riser: 「ここで上に振る」が一瞬で読めるか、横幅・どこからどこまで上がるか（部分移動 0→0.5）が読めるか
5. 遠方で消えない・点滅しない
6. Unity への移植コスト・iPad 描画負荷（大面積の半透明はオーバードローが重い）
