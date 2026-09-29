// スキン「キーキャップ」（r3）: 全ノーツを不透明マットのキーキャップ素材（面取りの明暗＋白リング＋塗り）で統一。
//  Tap/ExTap/Flick = 立体メッシュ（地上は薄型、空中は本物の立体＋遠方で高さ→0）
//  Slide = マット板＋縁レール＋中央芯線（枕木なし）、始点/Visible中継点は Tap と同じキーキャップのマーカー
//  Riser/Diver = 白縁＋塗りの分厚い ∧ 1枚が 0.46層/秒 で層方向に流れる壁

import { buildChevrons, chevronGLSL, CHEVRON_DEFAULTS } from './shared/chevron.js';

// ================= Tap 系: 断面リングを積んだ押し出しソリッド =================
// loop = 平面の輪郭 [{ p:[x,z], m:[mx,mz](内側への縮み方向・単位はミター補正込み), n:[nx,nz](外向き法線) }]
function loopStadium(a, r, seg) {
  const cx = Math.max(a - r, 0), L = [];
  for (let i = 0; i <= seg; i++) { const f = -Math.PI / 2 + (Math.PI * i) / seg, d = [Math.cos(f), Math.sin(f)]; L.push({ p: [cx + r * d[0], r * d[1]], m: d, n: d }); }
  for (let i = 0; i <= seg; i++) { const f = Math.PI / 2 + (Math.PI * i) / seg, d = [Math.cos(f), Math.sin(f)]; L.push({ p: [-cx + r * d[0], r * d[1]], m: d, n: d }); }
  return L;
}
// Flick: 端を尖らせた < > 。両端の三角の先端が外へ突き出す
function loopFlick(a, r, tipK) {
  const tipL = Math.min(r * tipK, a * 0.9), cx = Math.max(a - tipL, 0);
  const mit = (n1, n2) => { const k = 1 + n1[0] * n2[0] + n1[1] * n2[1]; return [(n1[0] + n2[0]) / k, (n1[1] + n2[1]) / k]; };
  const len = Math.hypot(r, tipL);
  const nwf = [r / len, -tipL / len], nwn = [r / len, tipL / len];           // 右側の斜辺（奥・手前）の外向き法線
  const nFar = [0, -1], nNear = [0, 1];
  const L = [];
  const push = (p, n, m) => L.push({ p, n, m });
  // 右: 奥角 → 先端 → 手前角
  let m = mit(nFar, nwf); push([cx, -r], nFar, m); push([cx, -r], nwf, m);
  m = mit(nwf, nwn); push([cx + tipL, 0], nwf, m); push([cx + tipL, 0], nwn, m);
  m = mit(nwn, nNear); push([cx, r], nwn, m); push([cx, r], nNear, m);
  // 左: 手前角 → 先端 → 奥角（x 反転）
  const fx = (n) => [-n[0], n[1]];
  m = mit(nNear, fx(nwn)); push([-cx, r], nNear, m); push([-cx, r], fx(nwn), m);
  m = mit(fx(nwn), fx(nwf)); push([-cx - tipL, 0], fx(nwn), m); push([-cx - tipL, 0], fx(nwf), m);
  m = mit(fx(nwf), nFar); push([-cx, -r], fx(nwf), m); push([-cx, -r], nFar, m);
  return L;
}
// rings: 下→上 [{ e:内側への縮み(b比), y:高さ(h比), nh, ny, c:縁距離(0=縁,<0=白リング無し) }]
function buildSolid(THREE, loop, b, h, rings) {
  const P = loop.length, pos = [], nor = [], cc = [], idx = [];
  for (const rg of rings) {
    for (const v of loop) {
      pos.push(v.p[0] - v.m[0] * rg.e * b, rg.y * h, v.p[1] - v.m[1] * rg.e * b);
      nor.push(rg.nh * v.n[0], rg.ny, rg.nh * v.n[1]);
      cc.push(rg.c);
    }
  }
  for (let k = 0; k + 1 < rings.length; k++) {
    for (let i = 0; i < P; i++) {
      const j = (i + 1) % P;
      const lo0 = k * P + i, lo1 = k * P + j, hi0 = (k + 1) * P + i, hi1 = (k + 1) * P + j;
      idx.push(lo0, hi0, lo1, lo1, hi0, hi1);
    }
  }
  const top = rings[rings.length - 1], ci = pos.length / 3, tb = (rings.length - 1) * P;
  pos.push(0, top.y * h, 0); nor.push(0, 1, 0); cc.push(1);
  for (let i = 0; i < P; i++) idx.push(tb + i, ci, tb + ((i + 1) % P));
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
  g.setAttribute('aC', new THREE.Float32BufferAttribute(cc, 1));
  g.setIndex(idx);
  return g;
}
// 壁 → 面取り → 天面（天面リングは複製して c=0 から始める）
const RINGS = [
  { e: 0.00, y: 0.00, nh: 1.0, ny: 0.0, c: -1 },
  { e: 0.00, y: 0.72, nh: 1.0, ny: 0.0, c: -1 },
  { e: 0.05, y: 0.90, nh: 0.85, ny: 0.55, c: -1 },
  { e: 0.14, y: 1.00, nh: 0.5, ny: 0.87, c: -1 },
  { e: 0.14, y: 1.00, nh: 0.0, ny: 1.0, c: 0.0 },
];

// キーキャップの陰影（Tap のメッシュとマーカーで共通）
const KEY_SHADE = /* glsl */ `
vec3 keycapShade(vec3 col, vec3 N, vec3 V) {
  vec3 L = normalize(vec3(-0.35, 0.85, 0.5));
  float hemi = mix(0.42, 0.78, N.y * 0.5 + 0.5);
  float diff = max(dot(N, L), 0.0) * 0.32;
  vec3 rgb = col * (hemi + diff);
  vec3 H = normalize(L + V);
  rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 36.0) * 0.18 * (1.0 - step(0.98, N.y));
  rgb *= mix(0.78, 1.0, smoothstep(0.0, 0.9, N.y));
  return rgb;
}`;

const TAP_VERT = /* glsl */ `
  attribute float aC;
  varying float vC; varying vec3 vN; varying vec3 vV;
  void main() {
    vC = aC;
    vec4 wp = modelMatrix * vec4(position, 1.0);
    vec3 s = vec3(length(modelMatrix[0].xyz), length(modelMatrix[1].xyz), length(modelMatrix[2].xyz));
    vN = normalize(mat3(modelMatrix) * (normal / (s * s)));
    vV = cameraPosition - wp.xyz;
    gl_Position = projectionMatrix * viewMatrix * wp;
  }`;
const TAP_FRAG = /* glsl */ `
  uniform vec3 uColor;
  varying float vC; varying vec3 vN; varying vec3 vV;
  ${KEY_SHADE}
  void main() {
    vec3 rgb = keycapShade(uColor, normalize(vN), normalize(vV));
    if (vC >= 0.0) {
      float fw = max(fwidth(vC), 1e-5);
      float dPx = vC / fw, bPx = 1.0 / fw;
      float w = min(1.5, max(0.0, bPx - 0.75) * 0.5);
      float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w);
      rgb = mix(rgb, vec3(1.0), ol);
    }
    gl_FragColor = vec4(rgb, 1.0);
    #include <colorspace_fragment>
  }`;

// ================= Slide / Riser: 頂点シェーダ配置 =================
const LONG_VERT = /* glsl */ `
  attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag(0=帯,1=マーカー,3=Riser壁,4=∧の腕)
  attribute vec4 aColor;  // 壁: x=s(流れ方向の層距離), y=span, z=Diver(0/1) / 腕: shared/chevron.js の仕様
  attribute float aSide;
  varying float vDepth, vLayer, vTag, vSide, vM; varying float vLocalX; varying vec4 vColor;
  void main() {
    float depth, sc;
    float layer = position.y, m = 0.0;
    if (aExtra.w > 3.5) layer = chevronLayer(position.y, aColor.w, position.z, aColor.x, aColor.y, m);
    vec3 wp = musePlace(position.x, layer, position.z, aExtra.x, aExtra.y, depth, sc);
    vM = m;
    vDepth = depth; vLayer = layer; vTag = aExtra.w; vLocalX = aExtra.z; vColor = aColor; vSide = aSide;
    gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
  }`;

// ∧ の寸法（shared/chevron.js。速さ 0.46 層/秒・ノーツ時刻基準の位相は既定のまま）
const CHEV = { ...CHEVRON_DEFAULTS, th: 0.14, sl: 0.26, tileCells: 2.5 };

export default {
  id: 'keycap', name: 'キーキャップ', model: 'Sonnet 5.5',
  concept: '全ノーツを「不透明でマットなキーキャップ」に統一。Tap/Ex Tap/Flick は面取り付きの立体（白リング付き）で、地上は薄型（高さ0.7×halfT・奥行き1.25倍）、空中は本物の立体メッシュ＋遠方で高さを0へ縮めて二重見えを防ぐ。Flick は端を尖らせた < >。Slide は枕木なしのマット板＋縁レール＋中央芯線、始点/中継点は Tap と同素材で帯の色のキーキャップ。Riser/Diver は白縁＋塗りの分厚い ∧（Diver は ∨）が 1 枚だけ 0.46 層/秒で流れ、左右レールと到達点バーで範囲を示す。加算発光は使わない。',
  unityCost: 'Tap 系: メッシュ約 110 頂点/個（現行 4 頂点。法線・縁距離 c の頂点属性が増える）。凸形なので背面カリング（Cull Back）だけで ZTest Always のまま自己重なりが崩れない＝現行の「全ノーツ1メッシュ・ZWrite Off・ZTest Always」を保てるが、頂点シェーダで「遠方ほど高さ→0」を掛ける（深度→高さ係数 1 行）必要あり。Slide は現行と同頂点数で枕木を削ったぶん軽い。マーカーはフラグメントで面取り陰影を計算（現行＋数十命令）。Riser は壁 12 分割×2 三角形＋∧の腕ごとのクアッド（1腕6頂点。shared/chevron.js でネオンと共通、層位置は頂点シェーダが uSongTime から計算し腕が画面上で直線）。fragment だけスキンごとに差し替え。半透明（Slide 帯・Riser 壁の薄い塗り）以外は不透明色で出せる。',
  options: [],
  create(ctx) {
    const { THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt } = ctx;
    const SKY_H = d.skyHeight;

    // ---------- Tap ----------
    const tapMats = {};
    for (const [kind, hex] of [['tap', colors.tap], ['extap', colors.exTap], ['flick', colors.flick]]) {
      tapMats[kind] = new THREE.ShaderMaterial({
        uniforms: { uColor: { value: new THREE.Color(hex) } },
        vertexShader: TAP_VERT, fragmentShader: TAP_FRAG,
        // 凸形 + 背面カリング = 画面上の各ピクセルに前向きの面は 1 枚だけ → ZTest Always / ZWrite Off でも重なりが崩れない
        transparent: true, depthTest: false, depthWrite: false, side: THREE.FrontSide,
      });
    }
    const geoCache = new Map();
    const HEIGHT = { thin: 0.7, air: 1.3 };
    const Z_FAT = 1.25;   // 奥行き方向を太らせる（見た目のみ。ネオンの縦の大きさに合わせる）   // halfT 比
    function tapGeo(kind, widthCells, mode, wWorld, halfT) {
      const key = `${kind}|${widthCells}|${mode}`;
      let g = geoCache.get(key);
      if (!g) {
        const loop = kind === 'flick' ? loopFlick(wWorld / 2, halfT, 1.25) : loopStadium(wWorld / 2, halfT, 10);
        g = buildSolid(THREE, loop, halfT, halfT * HEIGHT[mode], RINGS);
        g.scale(1, 1, Z_FAT);
        geoCache.set(key, g);
      }
      return g;
    }
    // 遠方で高さを 0 に近づける係数（progress: 0=判定線 1=最遠端）
    const FAR_H0 = 0.12, FAR_H1 = 0.6, FAR_MIN = 0.06;
    const farFactor = (p) => {
      const t = Math.min(Math.max((p - FAR_H0) / (FAR_H1 - FAR_H0), 0), 1);
      return 1 - (1 - FAR_MIN) * t * t * (3 - 2 * t);
    };

    // ---------- Slide / Riser ----------
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky);
    const cR = new THREE.Color(colors.slide.riser), cD = new THREE.Color(colors.diver);
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS }, uRiser: { value: cR }, uDiver: { value: cD } },
      vertexShader: `${glsl.place}\n${chevronGLSL(CHEV)}\n${LONG_VERT}`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        ${KEY_SHADE}
        uniform float uSongTime;
        uniform vec3 uGround, uSky, uRiser, uDiver;
        varying float vDepth, vLayer, vTag, vSide, vM; varying float vLocalX; varying vec4 vColor;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        // 面取りプロファイル: e = 縁からの深さ(奥行き半幅比)。Tap のメッシュのリング補間と同じ (nh, ny)
        vec2 bevel(float e) {
          if (e < 0.05) return mix(vec2(1.0, 0.0), vec2(0.85, 0.55), e / 0.05);
          if (e < 0.14) return mix(vec2(0.85, 0.55), vec2(0.5, 0.87), (e - 0.05) / 0.09);
          return vec2(0.0, 1.0);
        }
        void main() {
          museClip(vDepth, vLayer, 1.0);
          float L = clamp(vLayer, 0.0, 1.0);
          vec3 base = mix(uGround, uSky, L);
          if (vTag < 0.5) {
            // ---- Slide 帯: マット板 + 縁レール + 中央の芯線（横線・枕木なし）----
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            float railW = min(3.0 * du, 0.25);
            float rail = 1.0 - smoothstep(railW - du, railW, xe);
            float railHi = 1.0 - smoothstep(0.0, 1.2 * du, xe);
            float center = 1.0 - smoothstep(0.0, du, abs(vLocalX - 0.5));
            vec3 rgb = base * 0.55;
            rgb = mix(rgb, base, rail);
            rgb = mix(rgb, vec3(1.0), clamp(railHi * 0.85 + center * 0.9, 0.0, 1.0));
            float a = mix(0.52, 0.45, L);
            a = max(a, rail * 0.95);
            a = max(a, max(center, railHi) * 0.95);
            gl_FragColor = museOut(rgb, a, 0.0);
          } else if (vTag < 1.5) {
            // ---- マーカー: Tap と同系統の陰影のキーキャップ板 ----
            vec2 uv = vec2(vLocalX, vSide * 0.5 + 0.5);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float rPx = bPx.y * 0.999;                                  // ( ) カプセル
            float dist = roundedBox(pPx, bPx, rPx);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float inset = max(-dist, 0.0) / max(bPx.y, 1.0);            // 縁からの深さ（奥行き半幅比）
            vec2 q = pPx - vec2(clamp(pPx.x, -(bPx.x - rPx), bPx.x - rPx), 0.0);
            vec2 dir = normalize(q + vec2(1e-4, 0.0));
            vec2 pf = bevel(inset);
            vec3 N = normalize(vec3(pf.x * dir.x, pf.y, -pf.x * dir.y));
            vec3 rgb = keycapShade(base, N, normalize(vec3(0.0, 0.8, 0.6)));
            // 白リング: 天面の縁（inset=0.14）から内側へ
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float dPx = max(-dist - 0.14 * bPx.y, 0.0);
            float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w) * step(0.14 * bPx.y - 0.5, -dist);
            rgb = mix(rgb, vec3(1.0), ol);
            gl_FragColor = museOut(rgb, shapeA, 0.0);
          } else if (vTag > 3.5) {
            // ---- ∧ の腕（独立した板）: 範囲外（根元より下・到達点より上）は捨てる。白縁＋マット塗り ----
            float span = max(vColor.y, 0.05);
            float fm = max(fwidth(vM), 1e-5);
            float inR = smoothstep(0.0, fm, vM) * smoothstep(0.0, fm, span - vM);
            if (inR <= 0.003) discard;
            vec3 col = mix(uRiser, uDiver, vColor.z);
            float fs = max(fwidth(vSide), 1e-5);
            float edgeW = max(0.22, 1.8 * fs);                       // 白縁の幅（断面比。細いときは約1.8px）
            float edge = smoothstep(1.0 - edgeW - fs, 1.0 - edgeW + fs, abs(vSide));
            float lit = 0.86 + 0.24 * (vSide * 0.5 + 0.5);            // 進行側ほど少し明るいマット
            vec3 rgb = mix(col * lit, vec3(1.0), edge);
            gl_FragColor = museOut(rgb, inR, 0.0);
          } else {
            // ---- Riser / Diver 壁（∧ は描かない）----
            float s = vColor.x, span = max(vColor.y, 0.05);
            vec3 col = mix(uRiser, uDiver, vColor.z);
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            float ps = max(fwidth(s), 1e-5);
            vec3 rgb = col * 0.55; float a = 0.16;
            float dG = (span - s) / ps, dB = s / ps;
            float goalC = 1.0 - smoothstep(3.5, 4.5, dG), goalW = 1.0 - smoothstep(1.5, 2.5, dG);
            float baseC = 1.0 - smoothstep(1.5, 2.5, dB);
            rgb = mix(rgb, col, max(goalC, baseC)); a = max(a, max(goalC, baseC));
            rgb = mix(rgb, vec3(1.0), goalW * 0.95);
            float railW = min(3.4 * du, 0.2);
            float rail = 1.0 - smoothstep(railW - du, railW, xe);
            float railHi = 1.0 - smoothstep(0.0, 1.3 * du, xe);
            rgb = mix(rgb, col, rail); a = max(a, rail);
            rgb = mix(rgb, vec3(1.0), railHi * 0.95); a = max(a, railHi);
            gl_FragColor = museOut(rgb, a, 0.0);
          }
        }`,
      transparent: true, depthTest: false, depthWrite: false, side: THREE.DoubleSide,
      blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    });

    function builder() {
      const pos = [], ext = [], col = [], side = [];
      return {
        v(u, L, t, dz, yUp, lx, tag, c = [1, 1, 1, 1], s = 0) { pos.push(u, L, t); ext.push(dz, yUp, lx, tag); col.push(...c); side.push(s); },
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
        const { kind, widthCells, layer, wWorld, halfT: hT } = spec;
        const mode = layer ? 'air' : 'thin';
        const mesh = new THREE.Mesh(tapGeo(kind, widthCells, mode, wWorld, hT), tapMats[kind]);
        mesh.renderOrder = 0;
        const root = new THREE.Group();
        root.add(mesh);
        root.userData.body = mesh; 
        return root;
      },
      updateTap(obj, info) {
        obj.userData.body.scale.y = info.layer ? farFactor(info.progress) : 1;   // 遠方で高さを縮めるのは空中だけ
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
        const dive = note.layerTo < note.layerF ? 1 : 0;
        for (let i = 0; i < steps; i++) {
          const ka = i / steps, kb = (i + 1) / steps;
          const pts = [[u0, ka, 0], [u1, ka, 1], [u1, kb, 1], [u0, ka, 0], [u1, kb, 1], [u0, kb, 0]];
          for (const [u, k, lx] of pts) B.v(u, L(k), note.t, 0, y, lx, 3, [k * span, span, dive, 0]);
        }
        buildChevrons(B, note, { ...CHEV, uAt, yUp: y });   // ∧ は腕ごとの板（壁より後に描く）
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
