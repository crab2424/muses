# Tap ノーツ デザイン比較ラボ

Unity 本体のステージ（`SampleScene` の StageConfig: phi 82 / yCam 10 / theta 38 / vGroundJudge −0.55 /
vSkyJudge 0.18、NoteView の厚み設定、`NotePlacement.hlsl` の空中奥行き再マップ）を three.js で再現し、
Tap ノーツの見た目案を**実際のゲーム視点・実際のスクロールで**並べて比較するためのページ。

## 起動

```
npm --prefix web-prototype exec -- vite design-lab/note-tap --port 5180
```
（`.claude/launch.json` の `tap-lab`）。`?v=a.js,b.js` で表示するバリアントを絞れる。

## ファイル

- `stage.js` — ステージ再現・ノーツ配置（編集しない）
- `patterns.js` — 比較用譜面（標準 / 隣接・階段 / 連打・同時押し / 幅1・12）
- `index.html` — タイル表示・操作 UI
- `variants/*.js` — デザイン案 1 つ = 1 ファイル。`variants/registry.js` に並べたものが表示される

## バリアントの書き方（契約）

```js
export default {
  id: 'unique-id',
  name: '短い名前（日本語可）',
  model: 'Opus 5.5' など担当モデル名,
  concept: '狙い・解決する問題（1〜3文）',
  unityCost: 'Unity 移植時のコストと懸念（1〜3文）',
  create(ctx) {           // タイルごとに1回
    // ctx = { THREE, cfg, d, colors, envMap, renderer, dims(widthCells) }
    return {
      makeNote(spec) { return object3D; },   // ノーツ1個
      update(obj, info) {},                  // 任意。毎フレーム
      release(obj) {},                       // 任意。画面外に出たとき
      beforeRender({ t, dt }) {},            // 任意。タイル描画直前に1回
    };
  },
};
```

### makeNote(spec)
`spec = { kind: 'tap', widthCells, layer(0=地上/1=空中), wWorld, halfT, cellWorld }`

- **ローカル座標系**: x = 横（中心0、全幅 `wWorld`）、z = 進行方向（`-halfT..+halfT`、**+z が手前＝カメラ側**）、
  y = 上（面の上が y=0、上に積む）。寸法は「地上・判定線上」の値。
- ハーネスが毎フレーム `position` と `scale = (sx, sx, sz)` を上書きする（空中の縮小・厚みの下限・奥行き再マップを反映）。
  **ルート Object3D の position/scale はいじらないこと**（子の変形・マテリアル uniform は自由）。
- 同じ幅のジオメトリ・マテリアルはキャッシュして使い回すこと（ノーツは頻繁に生成・破棄される）。
- `halfT` = 0.06 × zJudge ≈ 0.39（判定線上、地上）。`wWorld` は 3 セルで約 4.1。つまり**横長で非常に薄い**。

### update(obj, info)
`info = { t, dt, progress(0=判定線,1=最遠端), depth, layer, sx, sz, timeToHit }`

## 前提（Unity 本体の現状と制約）

- 背景 `#a0b298`（明るい灰緑）、地上面 `#3a2f6b` 不透明、空中面 `#6b2a55` α0.2（背景・地上が透ける）。
- Tap の色は `#4aa3ff` 固定（「操作」の色、層で変えない）。Ex Tap は黄 `#ffd54a`、Flick は赤 `#ff4a4a` で `< >` 形、
  Slide 中継点は角丸矩形。**Tap は `( )` カプセル形で、これらと形でも区別できる必要がある**。
- 現行の縁は**スクリーン空間一定幅 1.5px の白**（遠方でも隣接ノーツが分離して見えるため。note-visual-r1 §2.2）。
- 画面上の厚みは奥行きで最大約 23 倍変わる。遠方では数 px の線になる。
- Unity 側は全ノーツを 1 メッシュにまとめ `ZWrite Off / ZTest Always` の半透明で描いている（遠方の Z ファイティング対策）。
  厚みのある立体にするなら自己遮蔽・描画順の扱いが要る（頂点数も増える、iPad で 60fps 必須）。
  **制約を破る案も歓迎だが、`unityCost` に正直に書くこと。**

## 評価観点

1. 一目で「Tap」と読めるか（Ex Tap / Flick / 中継点との誤読がないか）
2. 隣接ノーツ（同時刻・横並び、階段）が 1 本に融合して見えないか
3. 遠方（線になる距離）でも存在と位置が読めるか、点滅しないか
4. 空中（半透明面の上）でも地上と同等に読めるか
5. 16 分・24 分の連打で詰まって見えないか
6. 背景・ステージ色との調和、質感
7. Unity への移植コスト・iPad での描画負荷
