// 暗部ゾーン（ノーツ出現位置付近）の定義と、全テーマ共通の暗幕。
//
// ゾーン = 最遠端の断面（地上の奥の辺〜空中の奥の辺、レーン幅）を画面座標（NDC、ズームなしの基準画面）で
// 囲った長方形に余白を足したもの。ここは地上面が不透明でないうえ空中面が半透明（alpha 0.2）なので、
// 背景がそのままノーツの背後に透ける。stage-bg-r1.md の「暗部の仕様」がこれの正。
//
// 座標の約束: テーマ・暗幕が扱う NDC は常に「ズームなしのゲーム画面」基準（base NDC）。
// ラボの拡大視点やタイル分割でずれないよう、ハーネスが毎回 uniform を更新する:
//   uFragToBase: base_ndc = gl_FragCoord.xy * xy + zw     （フラグメントで使う）
//   uBaseToClip: clip.xy  = base_ndc * xy + zw            （頂点で使う。スクリーン空間の板を置くとき）
// Unity では前者は「スクリーンUV*2-1」、後者は恒等になる。

export const ZONE_MARGIN = { side: 0.06, top: 0.08, bottom: 0.02 };
export const ZONE_FEATHER = 0.14; // 暗幕の縁のぼかし幅（NDC）

export function makeZone(THREE, cfg, d, camera, laneX) {
  const p = (x, y, z) => new THREE.Vector3(x, y, z).project(camera);
  const gL = p(laneX(cfg, d, -1, 0, d.zFar), 0, -d.zFar), gR = p(laneX(cfg, d, 1, 0, d.zFar), 0, -d.zFar);
  const sL = p(laneX(cfg, d, -1, 1, d.zFar), d.skyHeight, -d.zFar), sR = p(laneX(cfg, d, 1, 1, d.zFar), d.skyHeight, -d.zFar);
  const vHorizon = Math.tan(d.theta) / d.tanHalfPhi;
  const u0 = Math.min(gL.x, sL.x) - ZONE_MARGIN.side, u1 = Math.max(gR.x, sR.x) + ZONE_MARGIN.side;
  const v0 = Math.min(gL.y, sL.y) - ZONE_MARGIN.bottom, v1 = Math.min(1, Math.max(gL.y, sL.y) + ZONE_MARGIN.top);
  return {
    u0, u1, v0, v1, feather: ZONE_FEATHER, vHorizon,
    farGround: { uL: gL.x, uR: gR.x, v: gL.y }, farSky: { uL: sL.x, uR: sR.x, v: sL.y },
    // テーマ・暗幕で共有する uniform（ハーネスが描画ごとに uFragToBase / uBaseToClip を書き換える）
    uniforms: {
      uZone: { value: new THREE.Vector4(u0, v0, u1, v1) },
      uZoneFeather: { value: ZONE_FEATHER },
      uFragToBase: { value: new THREE.Vector4(1, 1, 0, 0) },
      uBaseToClip: { value: new THREE.Vector4(1, 1, 0, 0) },
    },
  };
}

// テーマのシェーダに差し込む GLSL（uniform 宣言込み）。
//   museBaseNdc()   … このフラグメントの base NDC
//   museSpawnMask(ndc) … ゾーン内 1、縁で feather 幅かけて 0 へ（角は丸い）
export const SPAWN_GLSL = /* glsl */ `
  uniform vec4 uZone; uniform float uZoneFeather; uniform vec4 uFragToBase; uniform vec4 uBaseToClip;
  vec2 museBaseNdc() { return gl_FragCoord.xy * uFragToBase.xy + uFragToBase.zw; }
  float museSpawnMask(vec2 ndc) {
    vec2 c = 0.5 * (uZone.xy + uZone.zw), h = 0.5 * (uZone.zw - uZone.xy);
    vec2 q = abs(ndc - c) - h;
    float dist = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
    return 1.0 - smoothstep(0.0, uZoneFeather, dist);
  }
`;

// 共通の暗幕: ゾーン＋feather を覆う板1枚（全画面ではない）。背景の最後・ステージの前に描く。
// Unity でも同じく「背景の上・ステージの下」にスクリーン空間の板1枚で置く想定。
export function buildShade(THREE, zone, color) {
  const f = zone.feather;
  const x0 = zone.u0 - f, x1 = zone.u1 + f, y0 = zone.v0 - f, y1 = Math.min(1, zone.v1 + f);
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute([x0, y0, 0, x1, y0, 0, x1, y1, 0, x0, y1, 0], 3));
  g.setIndex([0, 1, 2, 0, 2, 3]);
  const mat = new THREE.ShaderMaterial({
    uniforms: { ...zone.uniforms, uColor: { value: new THREE.Color(color) }, uStrength: { value: 0.85 } },
    vertexShader: /* glsl */ `
      uniform vec4 uBaseToClip;
      void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.0, 1.0); }`,
    fragmentShader: /* glsl */ `
      ${SPAWN_GLSL}
      uniform vec3 uColor; uniform float uStrength;
      void main() {
        float m = museSpawnMask(museBaseNdc()) * uStrength;
        gl_FragColor = vec4(uColor, m);
        #include <colorspace_fragment>
      }`,
    transparent: true, depthTest: false, depthWrite: false,
  });
  const mesh = new THREE.Mesh(g, mat);
  mesh.frustumCulled = false;
  mesh.renderOrder = 1e6;
  return { mesh, setStrength(s) { mat.uniforms.uStrength.value = s; mesh.visible = s > 0; } };
}

// ハーネス用: 描画先の矩形（デバイスpx、左下原点）と拡大視点 vo（null=等倍）から uniform を作る
export function setZoneView(zone, vp, vo) {
  const fw = vo ? vo.fw : 1, fh = vo ? vo.fh : 1;
  const x = vo ? vo.x : 0, y = vo ? vo.y : 0, w = vo ? vo.w : 1, h = vo ? vo.h : 1;
  // base_u = 2*((fx*w + x)/fw) - 1,  fx = (fcx - vp.x)/vp.w
  const au = (2 * w) / (fw * vp.w), bu = (2 * (x - (vp.x * w) / vp.w)) / fw - 1;
  // base_v = 1 - 2*(((1-fy)*h + y)/fh),  fy = (fcy - vp.y)/vp.h
  const av = (2 * h) / (fh * vp.h), bv = 1 - (2 * (h + y)) / fh - av * vp.y;
  zone.uniforms.uFragToBase.value.set(au, av, bu, bv);
  // clip = base*s + o（base→拡大後の NDC）
  zone.uniforms.uBaseToClip.value.set(fw / w, fh / h, (fw - 2 * x) / w - 1, 1 - (fh - 2 * y) / h);
}

// ゲート（ノーツ出現位置の奥に置く真円）の画面上の位置と半径。単位は NDC の縦（横は ÷aspect）⇒ 画面上で真円。
// 中心 = 最遠端の断面の中央、半径 = 断面の4隅までの距離 × margin（縁が断面の隅を通る）。
// 断面より下ではステージの台形が必ず円より広いので、円の下部（約1/4）はステージに隠れる＝「ステージが門へ入っていく」見え方。
// 中心を下げて円全体を画面内に収める案は、下半分以上がステージに隠れてドーム状に見えたので不採用（r2）。
// 上端は iPad 11" で v≈1.018（約2%画面外）。topMax を超える分だけ半径を縮める（隅を覆う保証より上端を優先）。
export function gateCircle(zone, aspect, { margin = 1.0, topMax = 1.02 } = {}) {
  const hw = Math.max(zone.farGround.uR, zone.farSky.uR) * aspect; // 断面の半幅（縦単位）
  const cy = (zone.farGround.v + zone.farSky.v) / 2;
  const hh = Math.abs(zone.farSky.v - zone.farGround.v) / 2;
  const r = Math.min(Math.hypot(hw, hh) * margin, topMax - cy);
  return { cx: 0, cy, r };
}
// GLSL: 画面上の真円までの符号付き距離（縦単位、内側が負）。uGate = (cx, cy, r, aspect)
export const GATE_GLSL = /* glsl */ `
  uniform vec4 uGate;
  float museGateDist(vec2 ndc) { vec2 q = (ndc - uGate.xy) * vec2(uGate.w, 1.0); return length(q) - uGate.z; }
`;
