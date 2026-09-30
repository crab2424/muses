# ステージ背景比較ラボ

背景テーマを、実際のゲーム視点（Unity の StageConfig 既定値）でステージ・ノーツと重ねて比較する。
設計と制約は `memory/game/stage-bg-r1.md`（**必ず先に読む**）。

## 起動

`.claude/launch.json` の `design-lab`（port 5181）→ http://localhost:5181/stage-bg/
- `?v=sanctuary.js` で絞り込み（`?v=_example.js` は見本）。`?skin=keycap.js` / `?skin=` でノーツの種類。`?aspect=1.3333`。

## ファイル

- `index.html` — ハーネス（背景 → 共通の暗幕 → 深度クリア → ステージ＋ノーツ）。暗部の輝度をタイル左上に表示。
- `shared/zone.js` — 暗部ゾーン・共通の暗幕・`SPAWN_GLSL`
- `themes/*.js` — テーマ1つ = 1ファイル。`themes/registry.js` の順に並ぶ。`themes/_example.js` が見本。

## テーマの書き方（契約）

```js
export default {
  id, name, model, concept, palette, motion, perf, unityCost,   // 日本語の説明（詳細欄に出る）
  clearColor: '#000000',                  // 背景を描く前のクリア色
  shade: { color: '#05060c', strength: 0.5 },   // 共通の暗幕（0 = なし）
  stage: {                                // 任意。ステージの面・線の色（地上面は暗く保つ: 相対輝度 ≤ 0.10）
    groundFill, groundFillAlpha, groundLine, groundLineAlpha, groundJudge,
    skyFill, skyFillAlpha, skyLine, skyLineAlpha, skyJudge,
  },
  create(ctx) {
    return {
      object,               // THREE.Object3D。背景シーンに入る（ステージより先に描かれ、深度はクリアされる）
      update({ t, dt }) {}, // 任意。t = 実時間(秒)。音楽・一時停止には反応しない
      dispose() {},         // 任意
    };
  },
};
```

`ctx = { THREE, cfg, d, camera, renderer, aspect, zone, glsl: { spawn }, laneX(u, layerF, z) }`

- ワールド座標: カメラは (0, 8, 0) で −z 方向を 38° 見下ろす。地上面 y=0、空中面 y=`d.skyHeight`(6.12)、
  判定線 z=−`d.zJudge`(3.1)、最遠端 z=−`d.zFar`(78.4)。地平線は画面上端のすぐ下（v=0.98）なので、
  背景に写るのは主に**ステージの左右と眼下**。
- `zone` … 暗部ゾーン `{ u0, u1, v0, v1, feather, farGround, farSky, uniforms }`（NDC）。
  シェーダーで使うときは `uniforms: { ...zone.uniforms, ... }` と展開し、フラグメントに `${glsl.spawn}` を差し込む。
  `museBaseNdc()` がこのピクセルの画面座標、`museSpawnMask(ndc)` がゾーン内1・外0。
- 全画面の板を置くときは `_example.js` のように base NDC の頂点を `uBaseToClip` で変換する（ラボの拡大表示でずれないため）。
- 3D の物体（柱・破片など）は普通にワールド座標に置けばよい。

## 評価観点

1. 雰囲気（テーマらしさ・参考画像の「形式」＝中央の消失点へ向かう対称構図、奥が暗い）
2. **ノーツの視認性**（ネオン・キーキャップの両方。暗部の計測値が緑＝基準内か）
3. 負荷（`memory/game/stage-bg-r1.md` §5 の制約内か）
4. Unity への移植のしやすさ
