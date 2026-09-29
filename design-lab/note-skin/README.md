# ノーツスキン比較ラボ

**スキン = 全ノーツ種別の見た目一式**（Tap / Ex Tap / Flick / Slide / Riser / Diver）。プレイヤーが設定で選ぶ前提で、
「ネオン」（既定）と「キーキャップ」の 2 スキンを同じ画面で比較する。方針は `memory/game/note-design-lab-r3.md`。

## 起動

`.claude/launch.json` の `design-lab`（port 5181）→ http://localhost:5181/note-skin/
`?v=neon.js` で絞り込み、`?o=key:value,...` でスキンの options の初期値を指定（現在 options を持つスキンはない）。

## ファイル

- `index.html` — タイル表示・操作 UI（note-long のハーネスを拡張）
- `patterns.js` — 比較用譜面: 全種類 / Riser・Diver / Tap系 隣接 / 密集
- `skins/*.js` — スキン 1 つ = 1 ファイル。`skins/registry.js` の順に並ぶ
- 共通部品: `../note-tap/stage.js`（ステージ・Tap の CPU 配置）、`../note-long/long.js`（頂点シェーダ配置 `musePlace` / `museClip` / `museOut`）

## スキンの書き方（契約）

```js
export default {
  id, name, model, concept, unityCost,       // 日本語
  options: [                                  // 任意。ヘッダに切替 UI が出て、変えるとスキンを作り直す
    { key: 'pulse', label: 'パルス', choices: [['on', 'あり'], ['off', 'なし']], default: 'on' },
  ],
  create(ctx) {
    return {
      makeTap(spec) { return object3D; },     // Tap / Ex Tap / Flick 1個
      updateTap(obj, info) {},                // 任意。毎フレーム
      releaseTap(obj) {},                     // 任意。画面外に出たとき
      makeSlide(note) { return object3D; },   // Slide 1本
      makeRiser(note) { return object3D; },   // Riser（上昇）/ Diver（下降）1個
      beforeRender({ t, dt }) {},             // 任意
    };
  },
};
```

`ctx = { THREE, cfg, d, colors, uniforms, glsl: { place, clip }, sampleSlide, uAt, EASE, envMap, renderer, dims(widthCells), opts }`

- `opts` … `options` で選ばれている値（`{ pulse: 'on' }` など）
- `colors` … `{ tap, exTap, flick, slide: { ground, sky, riser }, diver }`（Unity の `NoteColors.cs` と同じ値）

### Tap 系（makeTap）— Tap ラボ（`../note-tap/README.md`）と同じ CPU 配置

`spec = { kind: 'tap' | 'extap' | 'flick', widthCells, layer(0/1), wWorld, halfT, cellWorld }`

- ローカル座標: x = 横（中心0、全幅 `wWorld`）、z = 進行方向（`-halfT..+halfT`、+z が手前）、y = 上（面が y=0）。
- ハーネスが毎フレーム **ルートの position / scale を上書き**する（空中の縮小・厚み下限・奥行き再マップ込み）。ルートの変形はいじらない。
- `info = { t, dt, progress(0=判定線,1=最遠端), depth, layer, sx, sz, timeToHit }`。遠方で厚みを縮める等はここで。
- 形の決まり: Tap / Ex Tap は `( )` カプセル、**Flick は端を尖らせた `< >`**。Ex Tap は Tap と同形で色違い（黄）。
- 描画順は Unity の NoteDrawOrder（Slide < Tap < Flick < Ex Tap < Riser/Diver）。子メッシュの `renderOrder` は相対順として保たれる。

### Slide / Riser / Diver — note-long（`../note-long/README.md`）と同じ頂点シェーダ配置

- Slide の `note` は note-long と同じ。始点と `marker: 'visible'` の点だけマーカーを描く。
- Riser / Diver の `note = { kind:'riser', t, cellF, width, layerF, layerTo, dir }`。
  **`dir = -1`（layerTo < layerF）が Diver**: 色は `colors.diver`（紫）、向きは反転（矢印が下向きに流れる）。形は Riser と同じ。
  部分移動（0→0.5、1→0.5）もある。
- Riser / Diver の ∧（∨）は**分厚い1枚が約 0.46 層/秒で流れる**（両スキン共通）。位相は**ノーツ時刻基準**で、
  判定時刻に ∧ の先端が到達点へ届く（`uSongTime - noteT` で求める。どの Riser でも同じ見え方になり、スキン間で揃う）。

## 評価観点

note-tap / note-long の評価観点に加えて:

1. **一式の統一感**（Tap と Slide・Riser が同じ素材に見えるか）
2. **スキン間で情報量が同じ**か（色・形・大きさ・帯の端と中央の位置が一致し、どちらのスキンでも有利不利がない）
3. Unity への移植: **両スキンで頂点の積み方（メッシュ）を共通にし、フラグメントシェーダだけ差し替える**形になっているか
