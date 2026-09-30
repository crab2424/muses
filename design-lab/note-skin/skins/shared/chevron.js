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
//        wp = chevronPlace(position, aExtra, aColor, layer, m, depth, sc);
//    をワールド座標に使う。layer は varying vLayer に、
//    m（ノーツ起点からの移動量、層）は varying でフラグメントへ渡す。
// 3. フラグメント: m < 0 または m > span（壁の根元より下・到達点より上）は discard（境界は fwidth(m) で薄く消す）。
//    見た目（色・発光・縁）は各スキンで決める。壁のフラグメントには ∧ の模様を描かない。
//
// ## 位相（リード指定）
//   ノーツ時刻基準。判定時刻（uSongTime == noteT）に先端が到達点（m = span）へ届く。1周期は cycleSec 秒（高さに依らず共通）。
//   1周期 P = span + (th + sl) + pad なので、壁の上に同時に2枚は見えない。Diver は dir=-1 で ∨ が下へ流れる。
// ## 幅
//   幅に依らず ∧ は常に1つ（ノーツの全幅にわたる大きい1本）。横に並べると「大きいノーツ」と「隣接した小さいノーツの集まり」の
//   区別がつかないため（r3 §13、ユーザー指定）。幅1でも ∧ に読める（傾きは層単位で固定）。

export const CHEVRON_DEFAULTS = {
  th: 0.32,        // 厚み（層）。腕の縦方向の太さ。r3: 0.16 → 0.32（2倍、ユーザー指定）
  sl: 0.30,        // 傾き（層）。中央→端での下がり量
  pad: 0.05,       // 周期の余白（層）
  cycleSec: 0.5,   // 1周期の秒数（Riser の高さに依らず共通。ユーザー指定 r3 §10）。旧 speed(層/秒) は廃止
  tag: 4,          // aExtra.w に入れる印
  yUp: 0,          // 面からの高さ（ワールド単位、musePlace の yUp）
};

/**
 * 頂点シェーダ用 GLSL。uSongTime は glsl.place の uniform を使う（musePlace より後ろに置くこと）。
 *   vec3 chevronPlace(vec3 pos, vec4 extra, vec4 col, out float layer, out float m, out float depth, out float sc)
 *     pos=position, extra=aExtra, col=aColor。戻り値はワールド座標。
 *   全頂点で共通の基準層 Lc（先端中心の層を壁の範囲にクランプ）で、各頂点の u における壁上の位置と層方向の微分を取り、
 *   頂点ごとの層差ぶんその方向へずらして置く。基準層が全頂点で共通なので ∧ 全体が一緒に動き（端と中央の速さが揃う）、
 *   腕は画面上で直線のまま、隣り合う ∧ の継ぎ目も一致する。
 *   （以前は頂点ごとに自分の層で musePlace していたため、奥行き再マップの層依存で Diver の端と中央の速さがずれ、
 *     途中で形が変わった。r3 §10）
 */
export function chevronGLSL(params = {}) {
  const p = { ...CHEVRON_DEFAULTS, ...params };
  const f = (x) => x.toFixed(5);
  return /* glsl */ `
float chevronTipM(float noteT, float span) {
  float P = span + ${f(p.th + p.sl)} + ${f(p.pad)};
  // 1周期 = cycleSec 秒（高さに依らず共通）。判定時刻（uSongTime == noteT）に先端が到達点（m = span）へ届く
  return mod((uSongTime - noteT) / ${f(p.cycleSec)} * P + span + ${f(p.pad)}, P) - ${f(p.th * 0.5 + p.pad)};
}
vec3 chevronPlace(vec3 pos, vec4 extra, vec4 col, out float layer, out float m, out float depth, out float sc) {
  float dirSign = col.w, span = col.y, offsetM = col.x;
  float tipM = chevronTipM(pos.z, span);
  float Lc = pos.y + dirSign * clamp(tipM, 0.0, span);
  m = tipM + offsetM;
  layer = pos.y + dirSign * m;
  vec3 p0 = musePlace(pos.x, Lc, pos.z, 0.0, extra.y, depth, sc);
  float d1, s1;
  vec3 pL = musePlace(pos.x, Lc + 0.01, pos.z, 0.0, extra.y, d1, s1);
  return p0 + (layer - Lc) * (pL - p0) / 0.01;
}`;
}

/** ∧ の板（腕ごとのクアッド）の頂点を B に積む。note = { cellF, width, layerF, layerTo, t, dir? }。 */
export function buildChevrons(B, note, params = {}) {
  const p = { ...CHEVRON_DEFAULTS, ...params };
  const uAt = p.uAt;
  const span = Math.abs(note.layerTo - note.layerF);
  const dirSign = note.layerTo < note.layerF || note.dir === -1 ? -1 : 1;
  const diver = dirSign < 0 ? 1 : 0;
  const n = 1; // 幅に依らず1つ（r3 §13）
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
