// Riser / Diver の ∧（∨）を「腕ごとのクアッド」として描くための共有部品（ネオン・キーキャップ共通）。
//
// なぜ壁のフラグメント模様ではなく板にするか:
//   壁を層方向に分割して musePlace で置くと、奥行き再マップのせいで分割の区切りごとに斜線が折れて階段状になる。
//   ∧ の腕は「腕の両端の頂点だけ」を musePlace で置けば、画面上で直線になる。
//
// ## 使い方
// 1. 頂点を積む（JS）:  buildChevrons(B, note, { uAt, yUp, ...寸法 })
//      B は各スキンの builder で、`B.v(u, L, t, dz, yUp, lx, tag, c, side)` を持つこと（neon.js の builder と同じ）。
//      1頂点あたり: position=(u, L=note.layerF, t=note.t)   dz=0  yUp=params.yUp
//                   lx(=aExtra.z)  = 腕に沿った 0..1
//                   tag(=aExtra.w) = params.tag（既定 4）。頂点シェーダで「∧の板」を見分ける印
//                   c(=aColor)     = [offset, span, diver(0/1), dir(+1/-1)]
//                        offset: この頂点の ∧ 先端位置からの層方向のずれ（= -傾き*横位置 ± 厚み/2）
//                        span  : Riser の層移動量 |layerTo-layerF|（壁の高さ）
//                        dir   : 進行方向の符号（Riser +1 / Diver -1）。層 = layerF + dir * m
//                   side(=aSide)   = 腕の断面方向 -1（下辺）/ +1（上辺）
// 2. 頂点シェーダ（GLSL）: chevronGLSL(params) を musePlace より後ろに連結し、tag>3.5 の頂点で
//        float m; float layer = chevronLayer(position.y, aColor.w, position.z, aColor.x, aColor.y, m);
//    の layer を musePlace の layerF 引数と varying vLayer に使う。m（先端基準でなくノーツ起点からの移動量、層）は
//    varying でフラグメントへ渡す。
// 3. フラグメント: m < 0 または m > span（壁の根元より下・到達点より上）は discard（境界は fwidth(m) で薄く消す）。
//    見た目（色・発光・縁）は各スキンで決める。壁のフラグメントには ∧ の模様を描かない。
//
// ## 位相（リード指定）
//   ノーツ時刻基準。判定時刻（uSongTime == noteT）に先端が到達点（m = span）へ届く。速さは speed 層/秒で一定。
//   1周期 P = span + (th + sl) + pad なので、壁の上に同時に2枚は見えない。Diver は dir=-1 で ∨ が下へ流れる。
// ## 幅
//   幅に応じて ∧ を横に並べる（tileCells セルごとに1つ、最低1つ）。幅1でも ∧ に読める（傾きは層単位で固定）。

export const CHEVRON_DEFAULTS = {
  th: 0.16,        // 厚み（層）。腕の縦方向の太さ
  sl: 0.30,        // 傾き（層）。中央→端での下がり量
  pad: 0.05,       // 周期の余白（層）
  speed: 0.46,     // 流れる速さ（層/秒）
  tileCells: 3.0,  // ∧ 1つ分のセル幅の目安
  tag: 4,          // aExtra.w に入れる印
  yUp: 0,          // 面からの高さ（ワールド単位、musePlace の yUp）
};

/** 頂点シェーダ用 GLSL。uSongTime は glsl.place の uniform を使う（musePlace より後ろに置くこと）。 */
export function chevronGLSL(params = {}) {
  const p = { ...CHEVRON_DEFAULTS, ...params };
  const f = (x) => x.toFixed(5);
  return /* glsl */ `
float chevronLayer(float layerF, float dirSign, float noteT, float offsetM, float span, out float m) {
  float P = span + ${f(p.th + p.sl)} + ${f(p.pad)};
  float pos = mod((uSongTime - noteT) * ${f(p.speed)} + span + ${f(p.pad)}, P) - ${f(p.th * 0.5 + p.pad)};
  m = pos + offsetM;
  return layerF + dirSign * m;
}`;
}

/** ∧ の板（腕ごとのクアッド）の頂点を B に積む。note = { cellF, width, layerF, layerTo, t, dir? }。 */
export function buildChevrons(B, note, params = {}) {
  const p = { ...CHEVRON_DEFAULTS, ...params };
  const uAt = p.uAt;
  const span = Math.abs(note.layerTo - note.layerF);
  const dirSign = note.layerTo < note.layerF || note.dir === -1 ? -1 : 1;
  const diver = dirSign < 0 ? 1 : 0;
  const n = Math.max(1, Math.round(note.width / p.tileCells));
  const h = p.th / 2;
  const arm = (fa, xla, fb, xlb) => {
    const ua = uAt(note.cellF + fa * note.width), ub = uAt(note.cellF + fb * note.width);
    const oa = -p.sl * xla, ob = -p.sl * xlb;
    const V = [[ua, oa - h, 0, -1], [ub, ob - h, 1, -1], [ub, ob + h, 1, 1], [ua, oa - h, 0, -1], [ub, ob + h, 1, 1], [ua, oa + h, 0, 1]];
    for (const [u, off, lx, side] of V) B.v(u, note.layerF, note.t, 0, p.yUp, lx, p.tag, [off, span, diver, dirSign], side);
  };
  for (let i = 0; i < n; i++) {
    const f0 = i / n, f1 = (i + 1) / n, fc = (f0 + f1) / 2;
    arm(f0, 1, fc, 0); // 左の腕（端 → 先端）
    arm(fc, 0, f1, 1); // 右の腕（先端 → 端）
  }
}
