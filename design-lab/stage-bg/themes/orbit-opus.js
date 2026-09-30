// orbit-opus「神話の軌道都市 r2（Opus 版）」
// 高空の橋（ステージ）から見下ろす夜の都市。ステージ両脇の眼下に、長手をステージと平行にした神殿が奥のゲートへ
// 向かって並ぶ「天空の参道」。ゲートは画面上の真円（shared/zone.js の gateCircle）。
//
// draw call: 都市板1 + ゲート板1 + 神殿(線)1 + 軌道リング(線)1 + 光の粒1 (+ 共通暗幕1) = 6
// 動き（すべて実時間 t、点滅なし）:
//   - 神殿の列柱を下から上へなぞる光が、手前の神殿から奥へ順に伝わる（行進。周期 12 秒）
//   - 都市の大通り（ステージと平行）を光の車列がゆっくり流れる（奥へ＝琥珀、手前へ＝白青）
//   - 都市を囲む軌道リングの上を光点が周回、リング自体もゆっくり回る
//   - ゲートの内側を同心円が奥へ吸い込まれていく（ごく暗い）、縁の目盛りがゆっくり回る
//   - 光の粒が眼下から昇る
import { buildLines, lineKit } from '../shared/lines.js';
import { gateCircle, GATE_GLSL } from '../shared/zone.js';

const CITY_Y = -34;
const CYAN = [0.40, 0.62, 0.86], GOLD = [0.80, 0.66, 0.40], PALE = [0.62, 0.74, 0.92];

export default {
  id: 'orbit-opus', name: '神話の軌道都市 r2（Opus）', model: 'Opus 5.5',
  concept: '高空の橋から見下ろす夜の未来都市。ステージ両脇の眼下に神殿が奥のゲートへ向かって並ぶ「天空の参道」。ゲートは画面上の真円で、縁だけが淡く光る虚空。',
  palette: '地は深い紺〜群青（#03050d〜#0b1226）。構造線は冷たい青(#66a0dc系)と淡い金(#cca866系)、都市の灯りは低彩度のシアンと琥珀。ノーツ色より暗く低彩度。',
  motion: '列柱をなぞる光が手前から奥の神殿へ伝わる（12秒周期）／大通りを流れる車列の光／軌道リング上を周回する光点とリングの回転／ゲート内へ吸い込まれる同心円（ごく暗い）と縁の目盛りの回転／昇る光の粒。点滅なし。',
  perf: 'draw 6（都市の全画面板1、ゲート板1、線2、粒子1、暗幕1）。都市シェーダーはノイズなし（hash とリング計算のみ）。線は帯メッシュで約1900線分、粒子は300個の板。',
  unityCost: '都市板・ゲート板は Unlit シェーダー各1本（都市はカメラの逆射影で y=-34 平面へのレイ）。線は shared/lines.js の帯メッシュ＋頂点シェーダをそのまま移植。粒子は4頂点の板を頂点シェーダで上昇させるので Points 不要。ゲートの円は gateCircle を C# に移植（最遠端の投影から算出）。',
  clearColor: '#03050d',
  shade: { color: '#02030a', strength: 0.2 },
  stage: {
    groundFill: '#0b1226', groundFillAlpha: 1, groundLine: '#7fa4cc', groundLineAlpha: 0.28, groundJudge: '#9fc4e8',
    skyFill: '#2c4f80', skyFillAlpha: 0.2, skyLine: '#7fa4cc', skyLineAlpha: 0.28, skyJudge: '#9fc4e8',
  },
  create(ctx) {
    const { THREE, zone, glsl, cfg, d, aspect } = ctx;
    const root = new THREE.Group();
    const disposables = [];
    const gate = gateCircle(zone, aspect);
    const uGate = { value: new THREE.Vector4(gate.cx, gate.cy, gate.r, aspect) };
    const quad = (x0, y0, x1, y1) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute([x0, y0, 0, x1, y0, 0, x1, y1, 0, x0, y1, 0], 3));
      g.setIndex([0, 1, 2, 0, 2, 3]);
      disposables.push(g);
      return g;
    };
    // ラボの拡大表示に依らない、素のゲームカメラ（都市のレイ計算用）
    const baseCam = new THREE.PerspectiveCamera(cfg.phiDeg, aspect, 0.05, 4000);
    baseCam.position.set(0, cfg.yCam, 0); baseCam.rotation.x = -d.theta;
    baseCam.updateMatrixWorld(); baseCam.updateProjectionMatrix();

    // ================= 都市（全画面板1枚） =================
    const cityMat = new THREE.ShaderMaterial({
      uniforms: {
        ...zone.uniforms, uGate, uTime: { value: 0 },
        uInvProj: { value: baseCam.projectionMatrixInverse.clone() },
        uCamRot: { value: new THREE.Matrix3().setFromMatrix4(baseCam.matrixWorld) },
        uCamPos: { value: baseCam.position.clone() },
      },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        ${GATE_GLSL}
        uniform float uTime; uniform mat4 uInvProj; uniform mat3 uCamRot; uniform vec3 uCamPos;
        float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        // 街区の窓明かり（セルごとに1灯。画素より小さくなったら平均輝度へ）
        float windows(vec2 p, float cs, float px, float dens, float t) {
          vec2 q = p / cs, id = floor(q), f = fract(q) - 0.5;
          float h = h21(id), h2 = h21(id + 7.3), h3 = h21(id + 19.1);
          float lit = step(1.0 - dens, h);
          float r = 0.05 + 0.06 * h3;
          float aa = px / cs;
          float dotv = (1.0 - smoothstep(r - max(aa, 0.03), r + max(aa, 0.03), length(f - (vec2(h2, h3) - 0.5) * 0.4))) * lit;
          float avg = lit * 3.14159 * r * r;
          float sway = 0.75 + 0.25 * sin(t * (0.2 + 0.2 * h2) + h * 40.0);
          return mix(dotv, avg, smoothstep(0.25, 0.7, aa)) * sway;
        }
        void main() {
          vec2 ndc = museBaseNdc();
          vec4 v = uInvProj * vec4(ndc, 1.0, 1.0);
          vec3 dir = normalize(uCamRot * (v.xyz / v.w));
          vec3 col = vec3(0.012, 0.022, 0.060);
          if (dir.y < -0.004) {
            float tt = (${CITY_Y.toFixed(1)} - uCamPos.y) / dir.y;
            vec2 p = (uCamPos + dir * tt).xz;
            vec2 dp = fwidth(p); float px = max(dp.x, dp.y);
            float fine = 1.0 - smoothstep(0.4, 1.6, px); // 遠方で細部を消す
            vec3 L = vec3(0.0);
            // 窓明かり（2スケール）
            float w1 = windows(p, 4.0, px, 0.55, uTime), w2 = windows(p + 31.7, 1.6, px, 0.35, uTime + 5.0);
            float warm = step(0.8, h21(floor(p / 4.0) + 3.1));
            L += mix(vec3(0.26, 0.48, 0.64), vec3(0.66, 0.55, 0.34), warm) * w1 * 0.5;
            L += vec3(0.26, 0.44, 0.60) * w2 * 0.28;
            // 大通り（ステージと平行 = z 方向、間隔 24）と横道（間隔 30、淡い）
            float ax = p.x / 24.0 + 0.5, aid = floor(ax);
            float dx = (fract(ax) - 0.5) * 24.0;               // 大通り中心からの距離（符号付き）
            float roadW = max(px, 0.15) * 1.2;
            float avenue = 1.0 - smoothstep(1.0, 1.0 + roadW, abs(dx));
            float cz = abs(fract(p.y / 30.0 + 0.5) - 0.5) * 30.0;
            float street = 1.0 - smoothstep(0.35, 0.35 + roadW, cz);
            L += vec3(0.04, 0.07, 0.12) * (avenue * 0.5 + street * 0.3) * fine;
            // 車列の光: 大通りの左右の車線を逆向きに流れる（奥へ=琥珀、手前へ=白青）
            float lane = 1.0 - smoothstep(0.18, 0.18 + roadW, abs(abs(dx) - 0.7));
            float sideS = sign(dx);
            float ph = fract(p.y / 7.0 * (sideS) + uTime * (0.16 + 0.05 * h21(vec2(aid, sideS))) + h21(vec2(aid, 3.0)));
            float car = smoothstep(0.0, 0.08, ph) * (1.0 - smoothstep(0.12, 0.3, ph));
            float gap = step(0.35, h21(vec2(aid, floor(p.y / 7.0 * sideS + uTime * 0.2))));
            L += mix(vec3(0.75, 0.52, 0.28), vec3(0.55, 0.72, 0.95), step(0.0, sideS)) * lane * car * gap * 0.55 * fine;
            // 遠方は霞へ
            float haze = smoothstep(120.0, 520.0, tt);
            col += L * (1.0 - 0.85 * haze);
            col = mix(col, vec3(0.020, 0.040, 0.095), haze * 0.85);
          }
          // ゲートのまわりは円く沈める（暗い形を「円」として読ませる）
          float gd = museGateDist(ndc);
          col *= smoothstep(0.0, uGate.z * 0.45, gd) * 0.9 + 0.1;
          gl_FragColor = vec4(col, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    });
    disposables.push(cityMat);
    const city = new THREE.Mesh(quad(-1, -1, 1, 1), cityMat);
    city.frustumCulled = false; city.renderOrder = -100;
    root.add(city);

    // ================= ゲート（画面上の真円。スクリーン空間の板） =================
    const R = gate.r, pad = R * 0.35;
    const gateMat = new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uGate, uTime: { value: 0 } },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.998, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        ${GATE_GLSL}
        uniform float uTime;
        void main() {
          vec2 ndc = museBaseNdc();
          float gd = museGateDist(ndc), r = uGate.z;
          vec2 q = (ndc - uGate.xy) * vec2(uGate.w, 1.0);
          float ang = atan(q.y, q.x), rad = length(q);
          float spawn = museSpawnMask(ndc);
          float px = fwidth(gd);
          // 内側: ほぼ黒。縁へ向かってわずかに群青
          float inside = 1.0 - smoothstep(-px, px, gd);
          vec3 inner = mix(vec3(0.002, 0.003, 0.010), vec3(0.012, 0.022, 0.055), smoothstep(-r * 0.5, 0.0, gd));
          // 奥へ吸い込まれる同心円（3本、18秒で縁→中心）。ごく暗く、出現位置ではさらに弱める
          float rings = 0.0;
          for (int i = 0; i < 3; i++) {
            float k = fract(uTime / 18.0 + float(i) / 3.0);
            float rr = r * (1.0 - k) * (1.0 - k);
            rings += (1.0 - smoothstep(0.0, max(px, 0.0015) * 1.5, abs(rad - rr))) * sin(k * 3.14159);
          }
          inner += vec3(0.10, 0.17, 0.26) * rings * 0.22 * (1.0 - 0.7 * spawn);
          // 縁: 細い光の輪＋外側への淡い光彩
          float rim = 1.0 - smoothstep(0.0, max(px, 0.0012) * 1.6 + 0.0015, abs(gd));
          float glow = exp(-max(gd, 0.0) / (r * 0.10)) * (1.0 - inside);
          // 縁のすぐ外の目盛り（60本、ゆっくり回る）
          float band = step(r * 0.035, gd) * (1.0 - step(r * 0.075, gd));
          float tick = 1.0 - smoothstep(0.08, 0.16, abs(fract(ang / 6.28318 * 60.0 + uTime * 0.004) - 0.5));
          float major = step(0.5, fract((floor(ang / 6.28318 * 60.0 + uTime * 0.004) + 0.5) / 5.0) * 5.0 - 3.5);
          vec3 rimC = vec3(0.55, 0.75, 0.95);
          float spawnDim = 1.0 - 0.85 * spawn; // 縁は断面の隅（ノーツの出現位置）を通るので、そこでは弱める
          vec3 add = rimC * rim * 0.8 * spawnDim + rimC * glow * 0.10 * spawnDim
                   + vec3(0.62, 0.70, 0.85) * band * tick * (0.18 + 0.2 * major) * spawnDim;
          // 内側は不透明（背後の都市を隠す）、外側は加算（premultiplied）
          vec3 c = inner * inside + add;
          gl_FragColor = vec4(c, inside);
        }`,
      transparent: true, depthTest: false, depthWrite: false,
      blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    });
    disposables.push(gateMat);
    const gateMesh = new THREE.Mesh(quad(-(R + pad) / aspect, gate.cy - R - pad, (R + pad) / aspect, gate.cy + R + pad), gateMat);
    gateMesh.frustumCulled = false; gateMesh.renderOrder = -90;
    root.add(gateMesh);

    // ================= 天空の参道: 神殿（長手 = z、ステージ側に列柱） =================
    // ステージは x ∈ ±laneX(1)（ほぼ一定幅）。神殿はその外側の眼下に、奥へ等間隔で並べる。
    const T = { y: -6, gapX: 9, len: 16, wid: 9, colH: 7, step: 26, count: 5, z0: -30 };
    const kit = lineKit();
    const halfStage = Math.abs(ctx.laneX(1, 0, d.zFar * 0.5));
    for (let i = 0; i < T.count; i++) {
      const zc = T.z0 - i * T.step;                     // 神殿の中心 z
      const p = i / (T.count - 1);                      // 手前0 → 奥1（行進の光の位相）
      for (const sgn of [-1, 1]) {
        const xin = sgn * (halfStage + T.gapX);          // ステージ側の列柱の x
        const xout = xin + sgn * T.wid;                  // 外側の列柱の x
        const xc = (xin + xout) / 2;
        const zf = zc + T.len / 2, zb = zc - T.len / 2;  // 正面（手前）/ 背面（奥）
        const y0 = T.y, y1 = T.y + T.colH;
        // 基壇（2段）
        kit.box(xc, y0 - 0.4, zc, T.wid + 2.4, 0.8, T.len + 2.4, CYAN, 0.75, p);
                // 列柱: 長辺（ステージ側）に7本、外側は淡く7本、正面に中間2本。1本 = 縦線1本（見下ろしで線が重ならないよう間引く）
        const column = (x, z, k) => kit.seg([x, y0, z], [x, y1, z], CYAN, k, p);
        for (let c = 0; c < 7; c++) {
          const z = zb + (T.len * c) / 6;
          column(xin, z, 1.0); column(xout, z, 0.35);
        }
        for (let c = 1; c < 3; c++) column(xin + ((xout - xin) * c) / 3, zf, 0.8);
        // 梁（エンタブラチュア）
        kit.box(xc, y1 + 0.5, zc, T.wid + 1.2, 1.0, T.len + 1.2, GOLD, 0.8, p);
        // 破風（正面・背面の三角）と棟
        const ridge = y1 + 1.0 + T.wid * 0.28;
        for (const [z, k] of [[zf + 0.6, 0.9], [zb - 0.6, 0.45]]) {
          const a = [xin - sgn * 0.6, y1 + 1.0, z], b = [xout + sgn * 0.6, y1 + 1.0, z], t = [xc, ridge, z];
          kit.seg(a, t, GOLD, k, p); kit.seg(b, t, GOLD, k, p);
        }
        kit.seg([xc, ridge, zf + 0.6], [xc, ridge, zb - 0.6], GOLD, 0.6, p);
        kit.seg([xin - sgn * 0.6, y1 + 1.0, zf + 0.6], [xin - sgn * 0.6, y1 + 1.0, zb - 0.6], GOLD, 0.35, p);
        kit.seg([xout + sgn * 0.6, y1 + 1.0, zf + 0.6], [xout + sgn * 0.6, y1 + 1.0, zb - 0.6], GOLD, 0.35, p);
      }
    }
    // 行進の光: 列柱を下から上へなぞる帯が、手前(p=0)から奥(p=1)へ順に伝わる。周期 12 秒
    const temples = buildLines(THREE, ctx, kit.segs, {
      widthPx: 2.4, opacity: 0.9,
      uniforms: { uY0: { value: T.y - 1.8 }, uH: { value: T.colH + 1.0 + T.wid * 0.28 + 2.0 } },
      frag: /* glsl */ `
        float ph = fract(uTime / 12.0 - vP * 0.45);          // 奥ほど遅れて届く
        float h = clamp((vW.y - uY0) / uH, 0.0, 1.0);        // 神殿の中での高さ 0..1
        float sweep = exp(-pow((h - ph * 1.6 + 0.3) / 0.12, 2.0));
        col = col * (0.75 + 1.1 * sweep) + vec3(0.25, 0.35, 0.45) * sweep * 0.5;
        a *= 1.0 - 0.45 * vP;                                 // 奥は控えめ（霞）
      `,
    });
    temples.renderOrder = -70;
    root.add(temples);
    disposables.push(temples.geometry, temples.material);

    // ================= 軌道リング（都市を囲む水平の輪、光点が周回） =================
    const ringKit = lineKit();
    const RC = [0, -20, -96], RR = 64;
    const ringSeg = (r, n, c, k, tag) => {
      for (let i = 0; i < n; i++) {
        const a0 = (i / n) * Math.PI * 2, a1 = ((i + 1) / n) * Math.PI * 2;
        ringKit.seg([Math.cos(a0) * r, 0, Math.sin(a0) * r], [Math.cos(a1) * r, 0, Math.sin(a1) * r], c, k, tag + i / n);
      }
    };
    ringSeg(RR, 180, PALE, 0.55, 0);        // p = 0..1 : 周回位置（光点用）
    ringSeg(RR + 2.2, 180, CYAN, 0.25, 2);  // p = 2..3 : 外側の細い輪（光点なし）
    for (let i = 0; i < 72; i++) {          // 目盛り
      const a = (i / 72) * Math.PI * 2, r1 = i % 6 === 0 ? RR + 5 : RR + 3.4;
      ringKit.seg([Math.cos(a) * (RR + 2.2), 0, Math.sin(a) * (RR + 2.2)], [Math.cos(a) * r1, 0, Math.sin(a) * r1], GOLD, i % 6 === 0 ? 0.6 : 0.3, 2);
    }
    const ring = buildLines(THREE, ctx, ringKit.segs, {
      widthPx: 2.0, opacity: 0.7,
      frag: /* glsl */ `
        if (vP < 1.5) {
          // 光点6個が周回（リングの回転とは逆向きにゆっくり）
          float s = fract(vP - uTime * 0.012);
          float bead = 0.0;
          for (int i = 0; i < 6; i++) { float dd = abs(fract(s - float(i) / 6.0 + 0.5) - 0.5); bead += exp(-dd * dd / 0.00006); }
          col += vec3(0.55, 0.75, 1.0) * bead * 1.3;
        }
      `,
    });
    ring.renderOrder = -75;
    const ringG = new THREE.Group();
    ringG.position.set(...RC); ringG.rotation.z = 0.0;
    ringG.add(ring);
    root.add(ringG);
    disposables.push(ring.geometry, ring.material);

    // ================= 光の粒（300個の板、頂点シェーダで上昇） =================
    const N = 300;
    const pos = new Float32Array(N * 4 * 3), seed = new Float32Array(N * 4 * 4), crn = new Float32Array(N * 4 * 2);
    const idx = new Uint32Array(N * 6);
    let s = 20260930;
    const rnd = () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
    for (let i = 0; i < N; i++) {
      const side = rnd() < 0.5 ? -1 : 1;
      const sd = [side * (halfStage + 4 + rnd() * 50), rnd(), -(10 + rnd() * 100), 0.6 + rnd() * 0.8];
      for (let j = 0; j < 4; j++) {
        seed.set(sd, (i * 4 + j) * 4);
        crn.set([j % 2 ? 1 : -1, j < 2 ? -1 : 1], (i * 4 + j) * 2);
      }
      idx.set([i * 4, i * 4 + 1, i * 4 + 3, i * 4, i * 4 + 3, i * 4 + 2], i * 6);
    }
    const pg = new THREE.BufferGeometry();
    pg.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    pg.setAttribute('aSeed', new THREE.BufferAttribute(seed, 4));
    pg.setAttribute('aCorner', new THREE.BufferAttribute(crn, 2));
    pg.setIndex(new THREE.BufferAttribute(idx, 1));
    const pm = new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime: { value: 0 }, uAspect: { value: aspect } },
      vertexShader: /* glsl */ `
        attribute vec4 aSeed; attribute vec2 aCorner;
        uniform float uTime, uAspect; uniform vec4 uBaseToClip;
        varying float vA, vG; varying vec2 vC;
        void main() {
          float ph = fract(aSeed.y + uTime * 0.016 * aSeed.w);
          vec3 p = vec3(aSeed.x + 1.5 * sin(uTime * 0.2 + aSeed.y * 30.0), ${CITY_Y.toFixed(1)} + ph * 40.0, aSeed.z);
          vA = smoothstep(0.0, 0.15, ph) * (1.0 - smoothstep(0.65, 1.0, ph));
          vG = fract(aSeed.y * 91.7); vC = aCorner;
          vec4 c = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
          float sz = clamp(60.0 / c.w, 1.4, 3.0) * 2.0 / 1668.0;   // iPad 11" 換算の px
          c.xy += aCorner * sz * vec2(1.0 / uAspect, 1.0) * uBaseToClip.xy * c.w;
          gl_Position = c;
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        varying float vA, vG; varying vec2 vC;
        void main() {
          float a = (1.0 - smoothstep(0.3, 1.0, length(vC))) * vA * 0.6;
          a *= 1.0 - museSpawnMask(museBaseNdc());
          vec3 col = mix(vec3(0.32, 0.55, 0.72), vec3(0.75, 0.64, 0.42), step(0.8, vG));
          gl_FragColor = vec4(col * a, a);
        }`,
      transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false,
    });
    const motes = new THREE.Mesh(pg, pm);
    motes.frustumCulled = false; motes.renderOrder = -60;
    root.add(motes);
    disposables.push(pg, pm);

    const timed = [cityMat, gateMat, temples.material, ring.material, pm];
    return {
      object: root,
      update({ t }) {
        for (const m of timed) m.uniforms.uTime.value = t;
        ringG.rotation.y = t * 0.01;
      },
      dispose() { disposables.forEach((o) => o.dispose && o.dispose()); },
    };
  },
};
