// スキン「ネオン」(既定)。Tap / Ex Tap / Flick = ネオンドームのインポスター（板1枚）、
// Slide = 発光チューブ帯（刻みなし・パルス切替）、Riser / Diver = 分厚い∧を1枚だけ流す発光壁。
// 土台: note-tap/variants/sonnet-b-imp.js と note-long/variants/sonnet-b.js（コピーして改変）。

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
    float body = smoothstep(0.0, 0.7, vC);
    vec3 rgb = mix(uDeep, uColor, body);
    rgb *= 0.8 + 0.35 * max(dot(N, L), 0.0);
    float core = smoothstep(0.62, 1.0, vC);
    rgb = mix(rgb, uCore, core * 0.85);
    rgb += uRim * fres * 0.55;
    vec3 H = normalize(L + V);
    rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 60.0) * 0.6;
    float fw = max(fwidth(vC), 1e-5);
    float dPx = vC / fw, bPx = 1.0 / fw;
    float w = min(1.3, max(0.0, bPx - 0.75) * 0.5);
    float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w);
    rgb = mix(rgb, vec3(0.95, 0.98, 1.0), ol);
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
  varying float vDepth, vLayer, vTag, vSide, vT; varying float vLocalX; varying vec4 vColor;
  void main() {
    float depth, sc;
    vec3 wp = musePlace(position.x, position.y, position.z, aExtra.x, aExtra.y, depth, sc);
    vDepth = depth; vLayer = position.y; vT = position.z; vTag = aExtra.w; vLocalX = aExtra.z; vColor = aColor; vSide = aSide;
    gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
  }`;

// Riser の∧: 厚み TH・傾き SL（層単位）。1周期 = 壁の高さ(span) + ∧の高さ(TH+SL) + 余白 なので同時に2枚は見えない。
const CHEV_TH = 0.16, CHEV_SL = 0.30, CHEV_PAD = 0.05;
const CHEV_SPEED = 0.46; // 層/秒（現行 sonnet-b 全移動の 2 倍）
const CHEV_TILE_CELLS = 3.0; // 横に並べる∧ 1つ分のセル幅の目安（広い Riser は ∧∧∧ と並ぶ）

export default {
  id: 'neon',
  name: 'ネオン',
  model: 'Sonnet 5.5',
  concept: '加算発光のネオン一式。Tap / Ex Tap / Flick は板1枚のインポスターで描くネオンドーム（Flick は端を尖らせた < >）。Slide は白い芯＋色ハローの縁を持つ発光帯（横線の刻みなし、光のパルスは切替）、始点・Visible 中継点は Tap と同じドーム素材。Riser / Diver は分厚い∧を壁の上に1枚だけ流す発光壁（層速度一定・幅が広い時は∧を横に並べる）。Diver は紫で流れが下向き。',
  unityCost: '頂点の積み方は現行と同じ（Tap 系は1ノーツ4頂点の板、Slide は帯＋マーカー、Riser は壁12分割）で、全ノーツ1メッシュ・ZWrite Off / ZTest Always・半透明のまま移植可。差し替えるのはフラグメントのみ。Tap 系はレイ vs 楕円柱+楕円体2個（Flick は楕円柱+楕円錐2個）の二次方程式を毎画素解くので現行より重く、板は奥へ伸ばすため密な連打で画素が重なる（discard のため MSAA 頼み）。Riser の∧は fract/mod だけで安価。パルス・流れは uSongTime のみ。Tap 系は「ノーツはワールドで平行移動+スケールのみ」の前提でカメラをローカル化している。',
  options: [
    { key: 'pulse', label: 'パルス', choices: [['on', 'あり'], ['off', 'なし']], default: 'on' },
  ],
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
          uA: { value: 1 }, uB: { value: 1 }, uH: { value: 1 }, uFlick: { value: s.flick }, uT: { value: 1 },
        },
        vertexShader: TAP_VERT, fragmentShader: TAP_FRAG,
        transparent: true, depthWrite: false, depthTest: false, side: THREE.DoubleSide,
      });
    }
    const tapCache = new Map(); // kind|widthCells -> { g, m }

    // ---------- Slide / Riser ----------
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky);
    const cR = new THREE.Color(colors.slide.riser), cD = new THREE.Color(colors.diver);
    const pulseOn = (opts && opts.pulse) !== 'off';
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS }, uRiser: { value: cR }, uDiver: { value: cD }, uPulse: { value: pulseOn ? 1 : 0 } },
      vertexShader: `${glsl.place}\n${LONG_VERT}`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform float uSongTime, uPulse;
        uniform vec3 uGround, uSky, uRiser, uDiver;
        varying float vDepth, vLayer, vTag, vSide, vT; varying float vLocalX; varying vec4 vColor;
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
            float haloW = min(6.0 * du, 0.3);
            float halo = exp(-xe / max(haloW, 1e-4) * 2.2);
            float core = 1.0 - smoothstep(0.0, 1.4 * du, xe);
            float xc = abs(vLocalX - 0.5);
            float cCore = 1.0 - smoothstep(0.0, du, xc);
            float cHalo = exp(-xc / max(min(3.0 * du, 0.1), 1e-4) * 2.0) * 0.35;
            // パルス: 中央の芯線の明るさだけが判定線へ向けて流れる（帯の面や縁は変えない）。遠方では消えて一定の明るさへ。
            float pulse = 0.5 + 0.5 * sin((vT - uSongTime) * 7.85);
            float pw = 0.35 * uPulse * smoothstep(0.0, 1.0, 1.0 / max(fwidth(vT) * 40.0, 1.0));
            pulse = mix(1.0, pulse, pw);
            float glow = 0.10 + halo * 0.55 + cHalo * pulse;
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
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
            float depthPx = clamp(-dist / max(bPx.y, 1.0), 0.0, 1.0);
            float dome = sqrt(1.0 - (1.0 - depthPx) * (1.0 - depthPx));
            float rim = pow(1.0 - dome, 2.5);
            vec3 rgb = base * (0.55 + 0.7 * dome);
            rgb = mix(rgb, vec3(1.0), smoothstep(0.75, 1.0, dome) * 0.55);
            rgb += base * rim * 0.6;
            rgb = mix(rgb, vec3(1.0), outline);
            gl_FragColor = museOut(rgb, 0.95 * shapeA, 0.25 * shapeA);
          } else {
            // ---- Riser / Diver 壁 ----
            float k = vColor.x, span = max(vColor.y, 0.05), wc = max(vColor.z, 1.0);
            vec3 col = mix(uRiser, uDiver, vColor.w);
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            float haloW = min(7.0 * du, 0.3);
            float halo = exp(-xe / max(haloW, 1e-4) * 2.2);
            float core = 1.0 - smoothstep(0.0, 1.5 * du, xe);
            float dk = max(fwidth(k), 1e-5);
            float goal = 1.0 - smoothstep(0.0, 1.6 * dk, 1.0 - k);
            float goalHalo = exp(-(1.0 - k) / max(min(8.0 * dk, 0.25), 1e-4) * 2.2) * 0.5;

            // 流れる∧（進行方向 = k が増える向き。Riser は上、Diver は下。∧/∨の先端は常に進行方向）
            float m = k * span;                         // 起点からの移動量（層）
            float CH = ${CHEV_TH.toFixed(3)} + ${CHEV_SL.toFixed(3)};
            float P = span + CH + ${CHEV_PAD.toFixed(3)};     // 1周期 = 壁の高さ + ∧の高さ + 余白 → 同時に2枚は見えない
            // 位相はノーツ時刻基準（リード r3）: 判定時刻に ∧ の先端が到達点へ届く。どの Riser でも同じ見え方になり、両スキンで揃う
            float pos = mod((uSongTime - vT) * ${CHEV_SPEED.toFixed(3)} + span + ${CHEV_PAD.toFixed(3)}, P) - ${(CHEV_TH * 0.5 + CHEV_PAD).toFixed(4)}; // 先端中心の位置
            float nT = max(1.0, floor(wc / ${CHEV_TILE_CELLS.toFixed(2)} + 0.5));
            float xl = abs(fract(vLocalX * nT) - 0.5) * 2.0; // タイル内 0=中央 1=端
            float c = pos - ${CHEV_SL.toFixed(3)} * xl;
            float fm = max(fwidth(m), 1e-5);
            float hw = max(${(CHEV_TH * 0.5).toFixed(4)}, fm * 1.3); // 遠方でも最低 約2.6px の太さ
            float line = 1.0 - smoothstep(hw - fm * 0.6, hw + fm * 0.6, abs(m - c));
            // 遠方でピクセルより∧が細かい時は平均の明るさへ寄せてチラつきを防ぐ
            line = mix(line, 0.45, smoothstep(0.5, 1.5, fm / ${CHEV_TH.toFixed(3)}));
            float grad = mix(0.55, 1.0, k);
            float wall = 0.10 * grad + line * 0.75 * grad;
            vec3 rgb = mix(col, vec3(1.0), clamp(core * 0.9 + goal * 0.9 + line * 0.25, 0.0, 1.0));
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
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
