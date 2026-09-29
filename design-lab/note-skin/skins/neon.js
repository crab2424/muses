// スキン「ネオン」(既定)。Tap / Ex Tap / Flick = ネオンドームのインポスター（板1枚）、
// Slide = 発光チューブ帯（刻みなし・パルス切替）、Riser / Diver = 分厚い∧を1枚だけ流す発光壁。
// 土台: note-tap/variants/sonnet-b-imp.js と note-long/variants/sonnet-b.js（コピーして改変）。

import { EDGE_GLSL } from './shared/edge.js';
import { buildChevrons, chevronGLSL, CHEVRON_DEFAULTS } from './shared/chevron.js';

// ================= Tap 系（インポスター）=================
const HR = 1.5;        // 高さ/半奥行の比
const EXT_NEAR = 0.15; // 手前側へ伸ばす量

const RINGS = [0, 22, 45, 66, 82].map((deg) => {
  const al = (deg * Math.PI) / 180, ca = Math.cos(al), sa = Math.sin(al);
  const nh = HR * ca, l = Math.hypot(nh, sa);
  return { d: ca, nh: nh / l, ny: sa / l };
}).concat([{ d: 0, nh: 0, ny: 1 }]);

const TAP_VERT = /* glsl */ `
  uniform float uB, uH;
  varying vec2 vQ; varying vec3 vRo; varying vec3 vS;
  void main() {
    vec3 s = vec3(length(modelMatrix[0].xyz), length(modelMatrix[1].xyz), length(modelMatrix[2].xyz));
    vS = s;
    vRo = (cameraPosition - modelMatrix[3].xyz) / s;
    vec3 pos = position;
    if (pos.z < 0.0) {
      float ext = max(vRo.z, 0.0) * uH / max(vRo.y - uH, 0.05 * uB);
      pos.z = -min(max(ext * 1.08 + 0.1 * uB, uB * 1.05), uB * 6.0);
      pos.x = vRo.x + (pos.x - vRo.x) * (1.0 + 1.1 * uH / max(vRo.y - uH, 0.05 * uB));
    }
    vQ = pos.xz;
    gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(pos, 1.0);
  }`;

const TAP_FRAG = /* glsl */ `
  ${EDGE_GLSL}
  uniform vec3 uColor, uDeep, uCore, uRim;
  uniform float uA, uB, uH, uFlick, uT; // 半長 / 半奥行 / 高さ / Flick(0,1) / Flick の尖り長
  varying vec2 vQ; varying vec3 vRo; varying vec3 vS;

  float hitEllipsoid(vec3 ro, vec3 rd, vec3 c, vec3 r) {
    vec3 o = (ro - c) / r, d = rd / r;
    float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
    float D = B * B - A * C;
    if (D < 0.0) return -1.0;
    return (-B - sqrt(D)) / A;
  }
  // 端の尖り: 楕円錐 (z/b)^2+(y/H)^2 = ((T-xs)/T)^2, xs=端の始まりからの距離(0..T), y>=0。先端は地面の1点。
  float hitCone(vec3 o, vec3 d, float T) {
    float b2 = uB * uB, h2 = uH * uH, t2 = T * T;
    float A = d.z * d.z / b2 + d.y * d.y / h2 - d.x * d.x / t2;
    float B = o.z * d.z / b2 + o.y * d.y / h2 + (T - o.x) * d.x / t2;
    float C = o.z * o.z / b2 + o.y * o.y / h2 - (T - o.x) * (T - o.x) / t2;
    if (abs(A) < 1e-9) return -1.0;
    float D = B * B - A * C;
    if (D < 0.0) return -1.0;
    float s = sqrt(D);
    float t1 = (-B - s) / A, t2r = (-B + s) / A;
    if (t1 > t2r) { float tmp = t1; t1 = t2r; t2r = tmp; }
    vec3 p1 = o + d * t1, p2 = o + d * t2r;
    if (t1 > 0.0 && p1.x >= 0.0 && p1.x <= T && p1.y >= 0.0) return t1;
    if (t2r > 0.0 && p2.x >= 0.0 && p2.x <= T && p2.y >= 0.0) return t2r;
    return -1.0;
  }

  void main() {
    bool fl = uFlick > 0.5;
    float T = min(uT, uA);
    float cx = fl ? max(uA - T, 0.0) : max(uA - uB, 0.0);
    vec3 ro = vRo;
    vec3 q = vec3(vQ.x, 0.0, vQ.y);
    vec3 rd = normalize(q - ro);

    float tBest = 1e9;
    {
      vec2 o = vec2(ro.z / uB, ro.y / uH), d = vec2(rd.z / uB, rd.y / uH);
      float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
      float D = B * B - A * C;
      if (D >= 0.0) {
        float t = (-B - sqrt(D)) / A;
        vec3 p = ro + rd * t;
        if (abs(p.x) <= cx && p.y >= 0.0 && t > 0.0) tBest = t;
      }
    }
    for (int i = 0; i < 2; i++) {
      float sg = i == 0 ? 1.0 : -1.0;
      float t;
      if (fl) t = hitCone(vec3(ro.x * sg - cx, ro.y, ro.z), vec3(rd.x * sg, rd.y, rd.z), T);
      else t = hitEllipsoid(ro, rd, vec3(sg * cx, 0.0, 0.0), vec3(uB, uH, uB));
      if (t > 0.0) {
        vec3 p = ro + rd * t;
        if (p.x * sg >= cx - 1e-5 && p.y >= 0.0 && t < tBest) tBest = t;
      }
    }
    bool hit = tBest < 1e8;
    vec3 p = ro + rd * (hit ? tBest : 0.0);

    float vC; vec3 nl;
    if (fl) {
      float xs = max(abs(p.x) - cx, 0.0);
      float g = clamp((T - xs) / T, 0.02, 1.0);
      vC = hit ? clamp(1.0 - abs(p.z) / (uB * g), 0.0, 1.0) : 0.0;
      if (xs > 0.0) nl = vec3(sign(p.x) * 2.0 * (T - xs) / (T * T), 2.0 * p.y / (uH * uH), 2.0 * p.z / (uB * uB));
      else nl = vec3(0.0, p.y / (uH * uH), p.z / (uB * uB));
      nl = normalize(nl);
    } else {
      float ex = p.x - clamp(p.x, -cx, cx);
      vec2 hz = vec2(ex, p.z);
      float dist = length(hz);
      vC = hit ? clamp(1.0 - dist / uB, 0.0, 1.0) : 0.0;
      vec2 hdir = dist > 1e-5 ? hz / dist : vec2(1.0, 0.0);
      float dn = clamp(dist / uB, 0.0, 1.0);
      const float RD[6] = float[6](RING_D);
      const float RH[6] = float[6](RING_NH);
      const float RY[6] = float[6](RING_NY);
      float nh = RH[5], ny = RY[5];
      for (int k = 0; k < 5; k++) {
        if (dn <= RD[k] && dn >= RD[k + 1]) {
          float f = (dn - RD[k + 1]) / (RD[k] - RD[k + 1]);
          nh = mix(RH[k + 1], RH[k], f); ny = mix(RY[k + 1], RY[k], f);
        }
      }
      nl = normalize(vec3(nh * hdir.x, ny, nh * hdir.y));
    }
    vec3 N = normalize(nl / (vS * vS));
    vec3 V = normalize((ro - p) * vS);

    vec3 L = normalize(vec3(-0.4, 0.8, 0.45));
    float ndv = clamp(dot(N, V), 0.0, 1.0);
    float fres = pow(1.0 - ndv, 2.2);
    // 遠方（ノーツの画面上の高さが小さい）ほど、白い縁・芯・リム・ハイライトを弱めて本体色を主にする
    float fw = max(fwidth(vC), 1e-5);
    float dPx = vC / fw, bPx = 1.0 / fw;   // bPx = 半奥行の画面上ピクセル数
    float nearF = smoothstep(4.0, 14.0, bPx);
    float body = smoothstep(0.0, 0.7, vC);
    vec3 rgb = mix(uDeep, uColor, body);
    rgb *= 0.8 + 0.35 * max(dot(N, L), 0.0);
    float core = smoothstep(0.62, 1.0, vC);
    rgb = mix(rgb, uCore, core * 0.85 * mix(0.25, 1.0, nearF));
    rgb += uRim * fres * 0.55 * mix(0.3, 1.0, nearF);
    vec3 H = normalize(L + V);
    rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 60.0) * 0.6 * nearF;
    // 遠方はドームの陰影をやめ、本体色（やや明るめ）でほぼ均一に塗る
    rgb = mix(min(uColor * 1.12 + 0.03, vec3(1.0)), rgb, nearF);
    vec2 e = museEdge(1.3, vS.x);   // 輪郭: 判定線上 1.3px × 見かけの倍率（下限は uEdgeMinPx）
    float ol = (1.0 - smoothstep(e.x - 0.5, e.x + 0.5, dPx)) * e.y;
    // 縁は近くでは白、遠くでは本体色の明るい版へ寄せる（細く・弱く）
    vec3 olC = mix(min(uColor * 1.25 + 0.08, vec3(1.0)), vec3(0.95, 0.98, 1.0), nearF);
    rgb = mix(rgb, olC, ol * mix(0.5, 1.0, nearF));
    if (!hit) discard;
    gl_FragColor = vec4(rgb, 1.0);
    #include <colorspace_fragment>
  }`
  .replace('RING_D', RINGS.map((r) => r.d.toFixed(5)).join(','))
  .replace('RING_NH', RINGS.map((r) => r.nh.toFixed(5)).join(','))
  .replace('RING_NY', RINGS.map((r) => r.ny.toFixed(5)).join(','));

// ================= Slide / Riser =================
const LONG_VERT = /* glsl */ `
  attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag(0=帯,1=マーカー,3=Riser壁)
  attribute vec4 aColor;  // 壁: x=k(0..1: 根元→到達点), y=span(層の移動量), z=幅(セル), w=Diver(0/1)
  attribute float aSide;
  varying float vDepth, vLayer, vTag, vSide, vT, vM, vScale; varying float vLocalX; varying vec4 vColor;
  void main() {
    float depth, sc;
    float layer = position.y, m = 0.0;
    // ∧ の板（tag 4）: 層位置を頂点シェーダで計算（腕の両端だけ musePlace で置くので画面上で直線になる）
    if (aExtra.w > 3.5) layer = chevronLayer(position.y, aColor.w, position.z, aColor.x, aColor.y, m);
    vec3 wp = musePlace(position.x, layer, position.z, aExtra.x, aExtra.y, depth, sc);
    vM = m; vScale = sc;
    vDepth = depth; vLayer = layer; vT = position.z; vTag = aExtra.w; vLocalX = aExtra.z; vColor = aColor; vSide = aSide;
    gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
  }`;

// Riser の∧の寸法は shared/chevron.js の既定値（厚み0.16層・傾き0.30層・0.46層/秒・3セルごと）
const CHEV = { ...CHEVRON_DEFAULTS };

export default {
  id: 'neon',
  name: 'ネオン',
  model: 'Sonnet 5.5',
  concept: '加算発光のネオン一式。Tap / Ex Tap / Flick は板1枚のインポスターで描くネオンドーム（Flick は端を尖らせた < >）。Slide は白い芯＋色ハローの縁を持つ発光帯（横線の刻みなし）、始点・Visible 中継点は Tap と同じドーム素材。Riser / Diver は分厚い∧を壁の上に1枚だけ流す発光壁（層速度一定・幅が広い時は∧を横に並べる）。Diver は紫で流れが下向き。',
  unityCost: '頂点の積み方は現行と同じ（Tap 系は1ノーツ4頂点の板、Slide は帯＋マーカー、Riser は壁12分割）で、全ノーツ1メッシュ・ZWrite Off / ZTest Always・半透明のまま移植可。差し替えるのはフラグメントのみ。Tap 系はレイ vs 楕円柱+楕円体2個（Flick は楕円柱+楕円錐2個）の二次方程式を毎画素解くので現行より重く、板は奥へ伸ばすため密な連打で画素が重なる（discard のため MSAA 頼み）。Riser の∧は腕ごとのクアッド（1腕6頂点）で、層位置は頂点シェーダが uSongTime から計算（shared/chevron.js。腕が画面上で直線）。Tap 系は「ノーツはワールドで平行移動+スケールのみ」の前提でカメラをローカル化している。',
  options: [],
  create({ THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt, opts }) {
    // ---------- Tap 系 ----------
    const tapStyle = {
      tap: { color: colors.tap, deep: '#0d3a8f', core: '#d6f0ff', rim: [0.55, 0.8, 1.0], flick: 0 },
      extap: { color: colors.exTap, deep: '#8a5d05', core: '#fff3c2', rim: [1.0, 0.88, 0.5], flick: 0 },
      flick: { color: colors.flick, deep: '#8f1424', core: '#ffd6d6', rim: [1.0, 0.62, 0.66], flick: 1 },
    };
    const tapMats = {};
    for (const [k, s] of Object.entries(tapStyle)) {
      tapMats[k] = new THREE.ShaderMaterial({
        uniforms: {
          uColor: { value: new THREE.Color(s.color) }, uDeep: { value: new THREE.Color(s.deep) },
          uCore: { value: new THREE.Color(s.core) }, uRim: { value: new THREE.Vector3(...s.rim) },
          uA: { value: 1 }, uB: { value: 1 }, uH: { value: 1 }, uFlick: { value: s.flick }, uT: { value: 1 }, uEdgeMinPx: uniforms.uEdgeMinPx,
        },
        vertexShader: TAP_VERT, fragmentShader: TAP_FRAG,
        transparent: true, depthWrite: false, depthTest: false, side: THREE.DoubleSide,
      });
    }
    const tapCache = new Map(); // kind|widthCells -> { g, m }

    // ---------- Slide / Riser ----------
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky);
    const cR = new THREE.Color(colors.slide.riser), cD = new THREE.Color(colors.diver);
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS }, uRiser: { value: cR }, uDiver: { value: cD } },
      vertexShader: `${glsl.place}\n${chevronGLSL(CHEV)}\n${LONG_VERT}`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform float uSongTime;
        uniform vec3 uGround, uSky, uRiser, uDiver;
        varying float vDepth, vLayer, vTag, vSide, vT, vM, vScale; varying float vLocalX; varying vec4 vColor;
        ${EDGE_GLSL}
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          museClip(vDepth, vLayer, 1.0);
          float L = clamp(vLayer, 0.0, 1.0);
          vec3 base = mix(uGround, uSky, L);
          if (vTag < 0.5) {
            // ---- 帯（横線の刻みなし）----
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            // 白線・ハローは「判定線上の幅 × 見かけの倍率」で細くなる（下限 uEdgeMinPx）
            float haloPx = 6.0 * vScale;
            float haloW = min(haloPx * du, 0.3);
            float halo = exp(-xe / max(haloW, 1e-4) * 2.2) * clamp(haloPx, 0.0, 1.0);
            vec2 eE = museEdge(1.4, vScale);
            float core = (1.0 - smoothstep(eE.x - 0.5, eE.x + 0.5, xe / du)) * eE.y;
            float xc = abs(vLocalX - 0.5);
            vec2 eC = museEdge(1.0, vScale);
            float cCore = (1.0 - smoothstep(eC.x - 0.5, eC.x + 0.5, xc / du)) * eC.y;
            float cHaloPx = 3.0 * vScale;
            float cHalo = exp(-xc / max(min(cHaloPx * du, 0.1), 1e-4) * 2.0) * 0.35 * clamp(cHaloPx, 0.0, 1.0);
            float glow = 0.10 + halo * 0.55 + cHalo;
            vec3 rgb = mix(base, vec3(1.0), clamp(core * 0.9 + cCore * 0.85, 0.0, 1.0));
            float add = clamp(glow + core * 0.6 + cCore * 0.6, 0.0, 1.6);
            gl_FragColor = museOut(rgb, 0.10, add * 0.9);
          } else if (vTag < 1.5) {
            // ---- マーカー: ドーム状の発光（Tap と同素材・帯の色）----
            vec2 uv = vec2(vLocalX, vSide * 0.5 + 0.5);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float rPx = min(0.5 / max(duv.x, duv.y) * 0.5, min(bPx.x, bPx.y) * 0.98);
            float dist = roundedBox(pPx, bPx, rPx);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            vec2 eM = museEdge(1.5, vScale);
            float w = min(eM.x, max(0.0, bPx.y - 0.75) * 0.5);
            float outline = smoothstep(-w - 0.5, -w + 0.5, dist) * eM.y;
            float depthPx = clamp(-dist / max(bPx.y, 1.0), 0.0, 1.0);
            float dome = sqrt(1.0 - (1.0 - depthPx) * (1.0 - depthPx));
            float rim = pow(1.0 - dome, 2.5);
            vec3 rgb = base * (0.55 + 0.7 * dome);
            rgb = mix(rgb, vec3(1.0), smoothstep(0.75, 1.0, dome) * 0.55);
            rgb += base * rim * 0.6;
            rgb = mix(rgb, vec3(1.0), outline);
            gl_FragColor = museOut(rgb, 0.95 * shapeA, 0.25 * shapeA);
          } else if (vTag > 3.5) {
            // ---- ∧ の板（腕）: 範囲外（根元より下・到達点より上）は捨てる ----
            float span = max(vColor.y, 0.05);
            float fm = max(fwidth(vM), 1e-5);
            float inR = smoothstep(0.0, fm, vM) * smoothstep(0.0, fm, span - vM);
            if (inR <= 0.003) discard;
            vec3 col = mix(uRiser, uDiver, vColor.z);
            float fs = max(fwidth(vSide), 1e-5);
            vec2 eA = museEdge(2.0, vScale);
            float edge = smoothstep(1.0 - eA.x * fs, 1.0, abs(vSide)) * eA.y; // 腕の上下の縁だけ白く（遠方で細く・消える）
            vec3 rgb = mix(col, vec3(1.0), 0.12 + 0.6 * edge);
            gl_FragColor = museOut(rgb, 0.75 * inR, 0.30 * inR);
          } else {
            // ---- Riser / Diver 壁 ----
            float k = vColor.x, span = max(vColor.y, 0.05), wc = max(vColor.z, 1.0);
            vec3 col = mix(uRiser, uDiver, vColor.w);
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            float haloPx = 7.0 * vScale;
            float haloW = min(haloPx * du, 0.3);
            float halo = exp(-xe / max(haloW, 1e-4) * 2.2) * clamp(haloPx, 0.0, 1.0);
            vec2 eE = museEdge(1.5, vScale);
            float core = (1.0 - smoothstep(eE.x - 0.5, eE.x + 0.5, xe / du)) * eE.y;
            float dk = max(fwidth(k), 1e-5);
            vec2 eG = museEdge(1.6, vScale);
            float goal = (1.0 - smoothstep(eG.x - 0.5, eG.x + 0.5, (1.0 - k) / dk)) * eG.y;
            float gHaloPx = 8.0 * vScale;
            float goalHalo = exp(-(1.0 - k) / max(min(gHaloPx * dk, 0.25), 1e-4) * 2.2) * 0.5 * clamp(gHaloPx, 0.0, 1.0);

            float grad = mix(0.55, 1.0, k);
            float wall = 0.10 * grad;
            vec3 rgb = mix(col, vec3(1.0), clamp(core * 0.9 + goal * 0.9, 0.0, 1.0));
            float add = wall + halo * 0.55 + goalHalo + core * 0.6 + goal * 0.8;
            gl_FragColor = museOut(rgb, 0.05, clamp(add, 0.0, 1.6));
          }
        }`,
      transparent: true, depthTest: false, depthWrite: false, side: THREE.DoubleSide,
      blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    });

    function builder() {
      const pos = [], ext = [], col = [], side = [];
      return {
        v(u, L, t, dz, yUp, lx, tag, c = [1, 1, 1, 0], s = 0) { pos.push(u, L, t); ext.push(dz, yUp, lx, tag); col.push(...c); side.push(s); },
        build() {
          const g = new THREE.BufferGeometry();
          g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
          g.setAttribute('aExtra', new THREE.Float32BufferAttribute(ext, 4));
          g.setAttribute('aColor', new THREE.Float32BufferAttribute(col, 4));
          g.setAttribute('aSide', new THREE.Float32BufferAttribute(side, 1));
          return g;
        },
      };
    }
    const halfT = d.zJudge * cfg.thicknessFrac;
    const gap = cfg.gapCells;

    function marker(B, p) {
      const u0 = uAt(p.cellF + gap), u1 = uAt(p.cellF + p.width - gap);
      const ht = halfT * (1 + (cfg.skyThicknessMul - 1) * p.layerF), y = d.zJudge * 0.012;
      const q = [[u0, -ht, 0, -1], [u1, -ht, 1, -1], [u1, ht, 1, 1], [u0, -ht, 0, -1], [u1, ht, 1, 1], [u0, ht, 0, 1]];
      for (const [u, dz, lx, s] of q) B.v(u, p.layerF, p.t, dz, y, lx, 1, undefined, s);
    }

    return {
      makeTap(spec) {
        const kind = tapStyle[spec.kind] ? spec.kind : 'tap';
        const key = kind + '|' + spec.widthCells;
        let e = tapCache.get(key);
        if (!e) {
          const a = spec.wWorld / 2, b = spec.halfT;
          const z0 = -b, z1 = b * (1 + EXT_NEAR);
          const g = new THREE.BufferGeometry();
          g.setAttribute('position', new THREE.Float32BufferAttribute([-a, 0, z0, a, 0, z0, a, 0, z1, -a, 0, z1], 3));
          g.setIndex([0, 2, 1, 0, 3, 2]);
          const src = tapMats[kind];
          const m = src.clone();
          m.uniforms = { ...src.uniforms, uA: { value: a }, uB: { value: b }, uH: { value: b * HR }, uT: { value: Math.min(b * 2.4, a) } };
          e = { g, m };
          tapCache.set(key, e);
        }
        const mesh = new THREE.Mesh(e.g, e.m);
        mesh.renderOrder = kind === 'tap' ? 10 : kind === 'flick' ? 11 : 12;
        mesh.frustumCulled = false;
        return mesh;
      },
      makeSlide(note) {
        const B = builder();
        const S = sampleSlide(note);
        const y = d.zJudge * 0.01;
        for (let i = 0; i < S.length - 1; i++) {
          const a = S[i], b = S[i + 1];
          const a0 = uAt(a.cellF), a1 = uAt(a.cellF + a.width), b0 = uAt(b.cellF), b1 = uAt(b.cellF + b.width);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0); B.v(a1, a.layerF, a.t, 0, y, 1, 0); B.v(b1, b.layerF, b.t, 0, y, 1, 0);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0); B.v(b1, b.layerF, b.t, 0, y, 1, 0); B.v(b0, b.layerF, b.t, 0, y, 0, 0);
        }
        note.points.forEach((p, i) => { if (i === 0 || p.marker === 'visible') marker(B, p); });
        return new THREE.Mesh(B.build(), mat);
      },
      makeRiser(note) {
        const B = builder();
        const steps = 12, u0 = uAt(note.cellF), u1 = uAt(note.cellF + note.width);
        const L = (k) => note.layerF + (note.layerTo - note.layerF) * k;
        const y = d.zJudge * 0.01;
        const span = Math.abs(note.layerTo - note.layerF);
        const diver = note.dir === -1 || note.layerTo < note.layerF ? 1 : 0;
        for (let i = 0; i < steps; i++) {
          const ka = i / steps, kb = (i + 1) / steps;
          const pts = [[u0, ka, 0], [u1, ka, 1], [u1, kb, 1], [u0, ka, 0], [u1, kb, 1], [u0, kb, 0]];
          for (const [u, k, lx] of pts) B.v(u, L(k), note.t, 0, y, lx, 3, [k, span, note.width, diver]);
        }
        buildChevrons(B, note, { ...CHEV, uAt, yUp: y }); // ∧ は腕ごとの板（壁より後に描く）
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
