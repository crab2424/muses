// Slide / Riser 用のハーネス部品。
// Tap（../note-tap）は「小さい物体を CPU で毎フレーム置き直す」方式だったが、Slide は時間方向に長い帯、
// Riser は層方向の壁なので、Unity と同じく**頂点シェーダで配置する**方式にする。
// 頂点は「ノーツ空間」の値（レーン座標 u・層 layerF・ノーツ時刻 t）を属性として持ち、
// 下の GLSL（NotePlacement.hlsl の移植）が毎フレーム uSongTime からワールド座標を求める。
// → ここで作ったデザインは、ほぼそのまま Unity の NoteGeometry/Note.shader に持ち込める。
import * as THREE from 'three';

export const EASE = {
  linear: (k) => k,
  smooth: (k) => k * k * (3 - 2 * k),
  sineIn: (k) => 1 - Math.cos((k * Math.PI) / 2),
  sineOut: (k) => Math.sin((k * Math.PI) / 2),
  sineInOut: (k) => -(Math.cos(Math.PI * k) - 1) / 2,
  quadIn: (k) => k * k,
  quadOut: (k) => 1 - (1 - k) * (1 - k),
};

/** セル（0..12）→ レーン座標 u（-1..+1） */
export const uAt = (cell, cells = 12) => -1 + (2 * cell) / cells;

/**
 * Slide の Waypoint 列を時間方向にサンプリングする（ChartMath.At 相当）。
 * 横(cellF/width)は区間開始側の easing、高さ(layerF)は easingH。
 * 戻り値: [{ t, cellF, width, layerF, seg, k }]（seg=区間番号, k=区間内の進み 0..1）
 */
export function sampleSlide(note, step = 0.03) {
  const P = note.points, out = [];
  for (let s = 0; s < P.length - 1; s++) {
    const a = P[s], b = P[s + 1];
    const n = Math.max(2, Math.ceil((b.t - a.t) / step));
    const eh = EASE[a.easing || 'linear'], ev = EASE[a.easingH || a.easing || 'linear'];
    for (let i = s === 0 ? 0 : 1; i <= n; i++) {
      const k = i / n, kh = eh(k), kv = ev(k);
      out.push({
        t: a.t + (b.t - a.t) * k,
        cellF: a.cellF + (b.cellF - a.cellF) * kh,
        width: a.width + (b.width - a.width) * kh,
        layerF: a.layerF + (b.layerF - a.layerF) * kv,
        seg: s, k,
      });
    }
  }
  return out;
}

/** 全マテリアルで共有する uniform（値はハーネスが毎フレーム更新）。ShaderMaterial の uniforms に展開して使う */
export function makeSharedUniforms(cfg, d) {
  return {
    uSongTime: { value: 0 },
    uZJudge: { value: d.zJudge }, uSpeed: { value: d.speed }, uZFar: { value: d.zFar },
    uYCam: { value: cfg.yCam }, uSkyH: { value: d.skyHeight },
    uSinT: { value: d.sinTheta }, uCosT: { value: d.cosTheta }, uTheta: { value: d.theta },
    uTanHalfPhi: { value: d.tanHalfPhi }, uLaneK: { value: d.laneK },
    uLaneConverge: { value: cfg.laneConverge }, uZcFarGround: { value: d.zcFarGround },
    uZcJudgeGround: { value: cfg.yCam * d.sinTheta + d.zJudge * d.cosTheta },
    uGroundNear: { value: d.groundNear },
  };
}

/**
 * 頂点シェーダ用。musePlace(u, layerF, noteTime, dz, yUp, out depth, out scale) がワールド座標を返す。
 *   u      : レーン座標（-1..+1、セルなら uAt(cell)）
 *   dz     : 奥行き方向のずらし（地上・判定線上のワールド単位。+ で奥）。厚み付けに使う
 *   yUp    : 面からの高さ（地上・判定線上のワールド単位）。空中・遠方では scale で縮む
 *   depth  : 奥行き（フラグメントの museClip に渡す）
 *   scale  : その地点での「地上・判定線上の1ワールド単位」の見かけの倍率
 */
export const GLSL_PLACE = /* glsl */ `
uniform float uSongTime, uZJudge, uSpeed, uZFar, uYCam, uSkyH, uSinT, uCosT, uTheta,
  uTanHalfPhi, uLaneK, uLaneConverge, uZcFarGround, uZcJudgeGround;
float museVAt(float h, float depth) { return tan(uTheta - atan(h / depth)) / uTanHalfPhi; }
float museDepthFromV(float h, float v) { return h / tan(uTheta - atan(v * uTanHalfPhi)); }
// NotePlacement.hlsl の層ごとの奥行き再マップ（帯の内側だけ）
float museRemap(float d0, float layerF) {
  if (layerF < 1e-6 || d0 <= uZJudge || d0 >= uZFar) return d0;
  float hL = uYCam - layerF * uSkyH;
  float vgj = museVAt(uYCam, uZJudge), vgf = museVAt(uYCam, uZFar);
  float pg = (museVAt(uYCam, d0) - vgj) / (vgf - vgj);
  float vj = museVAt(hL, uZJudge), vf = museVAt(hL, uZFar);
  return museDepthFromV(hL, vj + pg * (vf - vj));
}
vec3 musePlace(float u, float layerF, float noteTime, float dz, float yUp, out float depth, out float scale) {
  float d0 = uZJudge + (noteTime - uSongTime) * uSpeed + dz;
  depth = museRemap(d0, layerF);
  float a = (uYCam - layerF * uSkyH) * uSinT;
  float zcJ = a + uZJudge * uCosT, zcF = a + uZFar * uCosT;
  float c = clamp(uLaneConverge * zcF / uZcFarGround, 0.0, 1.0);
  float zc = a + depth * uCosT;
  float zcMix = mix(zc, zcJ, c);
  scale = zcMix / uZcJudgeGround;
  return vec3(u * uLaneK * zcMix, layerF * uSkyH + yUp * scale, -depth);
}
`;

/**
 * フラグメントシェーダ用。museClip(depth, layerF, eat) で表示範囲外を捨てる。
 *   最遠端より奥 / その層の手前端より手前 を捨てる。
 *   eat=1 なら判定線より手前（通過済み）も捨てる（オートプレイで押さえている Slide・判定済みの Riser）。
 */
export const GLSL_CLIP = /* glsl */ `
uniform float uZJudge, uZFar, uGroundNear;
void museClip(float depth, float layerF, float eat) {
  if (depth > uZFar) discard;
  if (depth < mix(uGroundNear, uZJudge, clamp(layerF, 0.0, 1.0))) discard;
  if (eat > 0.5 && depth < uZJudge) discard;
}
// 出力: 線形色 rgb を出力色空間(sRGB)へ変換してから premultiplied にする。
//   戻り値 = vec4(srgb * (alpha + add), alpha)。マテリアルは Blend One / OneMinusSrcAlpha（CustomBlending）で使う。
//   add > 0 で「通常合成 α に加えて加算ぶん」を足せる（現行の地上帯: alpha 0.30 / add 0.22）。
// ※ Unity 本体は Linear 色空間でブレンドするが、three.js はここで sRGB 変換後にブレンドする。
//   半透明の重なりの明るさは近似になる（sRGB 空間で premultiply すると Unity の見え方にかなり近い）。
//   この関数を使う場合は #include <colorspace_fragment> を書かないこと（二重変換になる）。
vec4 museOut(vec3 rgb, float alpha, float add) {
  vec3 s = linearToOutputTexel(vec4(rgb, 1.0)).rgb;
  return vec4(s * (alpha + add), alpha);
}
`;
