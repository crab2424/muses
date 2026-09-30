// 黄昏の回廊: 暗いプラム〜ワイン色の神殿の回廊。側面の高窓から薔薇〜琥珀の光の筋が床に落ち、塵が舞う。
// 見下ろし視点なので、柱は眼下の床(y=FLOOR)から立ち上げ、ステージの左右に等間隔で奥へ並べる。
// 出現位置(画面上端中央)は「回廊の奥の垂れ幕と闇」: 全画面板の奥景(幕のひだ)＋距離で闇に溶ける霧で沈める。
// 描画: 奥景(全画面1) / 床(1) / 柱(Instanced 1、床への映り込み込み) / 光の筋(Instanced 1、加算) / 塵(Points 1) = 5 draw + 暗幕1
const FLOOR = -9;       // 床の高さ（ステージ面 y=0 の眼下）
const W = 15;           // 回廊の半幅（柱の中心 x = ±W）
const SPACING = 9;      // 柱の間隔（z）
const NCOL = 13;        // 片側の柱数
const Z0 = -5;          // 最初の柱の z

export default {
  id: 'corridor', name: '黄昏の回廊', model: 'Sonnet 5.5',
  concept: '暖色・暗めの神殿の回廊。ステージの左右に柱が奥へ等間隔に並び、高窓から薔薇〜琥珀の光の筋が床へ落ちる。奥は垂れ幕と闇に溶ける。',
  palette: 'プラム #1a0f1c / ワイン #3a1526 / 薔薇 #d9768a / 琥珀 #f0a860。地上面は暗いプラム、線は淡い薔薇',
  motion: '光の筋がゆっくり呼吸（周期 約12〜20秒、振幅±20%）、塵が光の中を漂う（≤400粒）。点滅なし、音楽反応なし',
  perf: 'draw 5（奥景板1 / 床1 / 柱Instanced1 / 光の筋Instanced1 / 塵Points1）＋共通暗幕1。全画面板は奥景の1枚のみ、フラグメントは軽量（ノイズなし）',
  unityCost: '低: 奥景・床・柱・光の筋・塵のシェーダ5本（すべてunlit）。柱は円柱メッシュ1つのGPUインスタンシング。テクスチャ不要',
  clearColor: '#0a050c',
  shade: { color: '#07030a', strength: 0.35 },
  stage: {
    groundFill: '#1c1220', groundFillAlpha: 1, groundLine: '#f0b9a8', groundLineAlpha: 0.32, groundJudge: '#ffd9b8',
    skyFill: '#6a3040', skyFillAlpha: 0.2, skyLine: '#ffc8a0', skyLineAlpha: 0.32, skyJudge: '#ffe0c0',
  },
  create({ THREE, zone, glsl, d, renderer }) {
    const root = new THREE.Group();
    const disposables = [];
    const uTime = { value: 0 };
    const FOG = 'vec3(0.045, 0.02, 0.055)';

    // ---- 1) 奥景（全画面板1枚）: プラムのグラデーションと、奥に垂れる幕のひだ。中央上ほど闇 ----
    const qg = new THREE.BufferGeometry();
    qg.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3));
    qg.setIndex([0, 1, 2, 0, 2, 3]);
    const back = new THREE.Mesh(qg, new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime;
        void main() {
          vec2 p = museBaseNdc();
          float ax = abs(p.x);
          // 左右対称。中央（消失点）ほど深い闇、外側と下は少し暖かいワイン
          float glowV = smoothstep(0.55, -0.9, p.y);
          vec3 wine = vec3(0.115, 0.05, 0.085), plum = vec3(0.05, 0.022, 0.06);
          vec3 c = mix(plum, wine, glowV * (0.4 + 0.6 * smoothstep(0.0, 1.0, ax)));
          // 奥の幕のひだ（縦縞の緩い波。ゆっくり揺れるだけ）
          float fold = 0.5 + 0.5 * sin(ax * 22.0 + sin(p.y * 3.0) * 0.6 + uTime * 0.05);
          float veil = smoothstep(0.62, 0.1, ax) * smoothstep(-0.25, 0.75, p.y);
          c *= 1.0 - 0.35 * veil;
          c += vec3(0.05, 0.016, 0.03) * fold * veil * 0.5;
          // 中央上を闇に
          c *= 1.0 - 0.75 * smoothstep(0.65, 0.0, ax) * smoothstep(0.1, 0.9, p.y);
          c *= 1.0 - 0.75 * museSpawnMask(p);
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    }));
    back.frustumCulled = false; back.renderOrder = -100;
    root.add(back);

    // ---- 2) 床（暗いワイン。光の筋が落ちる所だけ明るい）。柱は深度を使うので床は書かない ----
    const floorG = new THREE.PlaneGeometry(2 * W + 200, 260);
    floorG.rotateX(-Math.PI / 2);
    floorG.translate(0, FLOOR, -120);
    const floor = new THREE.Mesh(floorG, new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime },
      vertexShader: /* glsl */ `
        varying vec3 vW;
        void main() { vec4 w = modelMatrix * vec4(position, 1.0); vW = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime; varying vec3 vW;
        const float W = ${W.toFixed(1)}, SP = ${SPACING.toFixed(1)}, Z0 = ${Z0.toFixed(1)};
        void main() {
          vec3 fog = ${FOG};
          float dist = length(vW - cameraPosition);
          float ax = abs(vW.x);
          // 床の目地（薄い格子。奥で消える）
          vec2 g = vW.xz / 3.0; vec2 gf = abs(fract(g - 0.5) - 0.5) / fwidth(g);
          float grid = 1.0 - min(min(gf.x, gf.y), 1.0);
          vec3 c = vec3(0.075, 0.03, 0.05) + vec3(0.06, 0.03, 0.035) * grid * 0.25;
          // 光の筋が落ちるだまり（柱の間。左右対称）。ゆっくり呼吸
          float bay = floor((vW.z - Z0 + SP * 0.5) / SP);
          float bz = (vW.z - Z0 + SP * 0.5) - bay * SP - SP * 0.5;
          float ph = 6.28318 * fract(bay * 0.37);
          float br = 0.85 + 0.2 * sin(uTime * 0.42 + ph);
          float pool = smoothstep(2.6, 0.4, abs(bz)) * smoothstep(W + 1.0, W - 12.0, ax) * smoothstep(1.5, 5.0, ax);
          vec3 lc = mix(vec3(0.95, 0.42, 0.5), vec3(1.0, 0.68, 0.36), 0.5 + 0.5 * sin(bay * 1.3));
          c += lc * pool * br * 0.3;
          // 中央の細い暗い帯（奥へ向かう線を作る）
          c *= 0.75 + 0.25 * smoothstep(0.0, 8.0, ax);
          float f = 1.0 - exp(-dist * 0.017); f = f * f;
          c = mix(c, fog, clamp(f * 1.25, 0.0, 1.0));
          c *= 1.0 - 0.75 * museSpawnMask(museBaseNdc());
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    }));
    floor.frustumCulled = false; floor.renderOrder = -90;
    root.add(floor);

    // ---- 3) 柱（Instanced 1つ）。前半 = 実体、後半 = 床への映り込み（y 反転）----
    const colG = new THREE.CylinderGeometry(1, 1, 1, 14, 1, true);
    colG.translate(0, 0.5, 0); // 底が 0、高さ 1
    const N = NCOL * 2;
    const H = 46; // 床から上へ（画面外まで）
    const cols = new THREE.InstancedMesh(colG, new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime },
      vertexShader: /* glsl */ `
        varying vec3 vW; varying vec3 vN; varying float vRef; varying float vHt; varying float vSide;
        void main() {
          mat4 im = modelMatrix * instanceMatrix;
          vec4 w = im * vec4(position, 1.0);
          vW = w.xyz;
          vN = normalize(mat3(im) * normal);
          vSide = sign(im[3].x);
          vRef = im[1][1] < 0.0 ? 1.0 : 0.0;
          vHt = position.y; // 0..1
          gl_Position = projectionMatrix * viewMatrix * w;
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime; varying vec3 vW; varying vec3 vN; varying float vRef; varying float vHt; varying float vSide;
        void main() {
          vec3 fog = ${FOG};
          vec3 n = vN;
          float hh = (vRef > 0.5 ? -(vW.y - (${FLOOR.toFixed(1)})) : (vW.y - (${FLOOR.toFixed(1)}))); // 床からの高さ
          float dist = length(vW - cameraPosition);
          // 逆光: 外側（窓側）を向いた縁が琥珀に光り、内側は暗い柱の肌。床のだまりの照り返しで下が少し明るい
          float rim = pow(clamp(dot(n, vec3(vSide, 0.0, 0.0)), 0.0, 1.0), 1.5);
          float facing = clamp(dot(n, vec3(-vSide, 0.0, 0.0)), 0.0, 1.0);
          vec3 skin = vec3(0.2, 0.085, 0.12) * (0.4 + 0.6 * facing);
          skin += vec3(0.0) + vec3(0.7, 0.3, 0.18) * rim;
          skin += vec3(0.5, 0.2, 0.18) * exp(-hh * 0.18) * facing; // 床の照り返し
          // 柱身の縦溝（フルーティング）は法線のうねりで
          float fl = 0.85 + 0.15 * sin(atan(n.z, n.x) * 14.0);
          skin *= fl;
          // 台座（下端）と柱頭（上）の帯
          skin *= 1.0 + 0.5 * smoothstep(1.8, 1.4, hh) * step(0.0, hh);
          float f = 1.0 - exp(-dist * 0.017); f = f * f;
          vec3 c = mix(skin, fog, clamp(f * 1.25, 0.0, 1.0));
          if (vRef > 0.5) c *= 0.4 * exp(-hh * 0.12); // 映り込み: 暗く、上（=床から遠い像）ほど消える
          c *= 1.0 - 0.75 * museSpawnMask(museBaseNdc());
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      side: THREE.DoubleSide,
    }), N * 2);
    cols.frustumCulled = false; cols.renderOrder = -80;
    const m = new THREE.Matrix4(), sc = new THREE.Vector3(), pos = new THREE.Vector3(), q = new THREE.Quaternion();
    let k = 0;
    for (let side = -1; side <= 1; side += 2) {
      for (let i = 0; i < NCOL; i++) {
        const z = Z0 - i * SPACING, r = 1.0;
        pos.set(side * W, FLOOR, z);
        m.compose(pos, q, sc.set(r, H, r)); cols.setMatrixAt(k++, m);
        m.compose(pos, q, sc.set(r, -H * 0.5, r)); cols.setMatrixAt(k++, m); // 映り込み
      }
    }
    cols.instanceMatrix.needsUpdate = true;
    root.add(cols);

    // ---- 4) 光の筋（Instanced 加算）: 高窓から床へ斜めに落ちる帯。ゆっくり呼吸 ----
    const NB = NCOL * 2;
    const bg = new THREE.InstancedBufferGeometry();
    bg.setAttribute('position', new THREE.Float32BufferAttribute([-1, 0, 0, 1, 0, 0, 1, 1, 0, -1, 1, 0], 3));
    bg.setIndex([0, 1, 2, 0, 2, 3]);
    const top = new Float32Array(NB * 3), bot = new Float32Array(NB * 3), par = new Float32Array(NB * 3);
    let b = 0;
    for (let side = -1; side <= 1; side += 2) {
      for (let i = 0; i < NCOL; i++) {
        const z = Z0 - (i + 0.5) * SPACING;
        top.set([side * (W + 4), 20, z], b * 3);
        bot.set([side * (W - 12 - (i % 3) * 1.5), FLOOR, z - 1.5], b * 3);
        par.set([2.0 + (i % 2) * 0.6, 6.28318 * ((i * 0.37 + (side > 0 ? 0.13 : 0)) % 1), (i * 0.5) % 1], b * 3);
        b++;
      }
    }
    bg.setAttribute('aTop', new THREE.InstancedBufferAttribute(top, 3));
    bg.setAttribute('aBot', new THREE.InstancedBufferAttribute(bot, 3));
    bg.setAttribute('aPar', new THREE.InstancedBufferAttribute(par, 3)); // 幅, 位相, 色
    bg.instanceCount = NB;
    const beams = new THREE.Mesh(bg, new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime },
      vertexShader: /* glsl */ `
        attribute vec3 aTop; attribute vec3 aBot; attribute vec3 aPar;
        varying vec2 vUv; varying vec3 vPar; varying float vDist;
        void main() {
          vUv = position.xy; vPar = aPar;
          vec3 c = mix(aTop, aBot, position.y);
          c.z += position.x * aPar.x * (1.0 + position.y * 0.8);   // z 方向に幅（床側ほど広がる）
          vec4 w = modelMatrix * vec4(c, 1.0);
          vDist = length(w.xyz - cameraPosition);
          gl_Position = projectionMatrix * viewMatrix * w;
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime; varying vec2 vUv; varying vec3 vPar; varying float vDist;
        void main() {
          float across = smoothstep(1.0, 0.15, abs(vUv.x));
          float along = smoothstep(0.0, 0.25, vUv.y) * (1.0 - 0.6 * vUv.y);
          float br = 0.8 + 0.2 * sin(uTime * (0.33 + 0.12 * vPar.z) + vPar.y);
          vec3 lc = mix(vec3(0.95, 0.4, 0.52), vec3(1.0, 0.66, 0.34), vPar.z);
          float f = exp(-vDist * 0.022);
          float a = across * along * br * f * 0.085;
          a *= 1.0 - museSpawnMask(museBaseNdc());
          gl_FragColor = vec4(lc * a, 1.0);
          #include <colorspace_fragment>
        }`,
      transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false, side: THREE.DoubleSide,
    }));
    beams.frustumCulled = false; beams.renderOrder = -70;
    root.add(beams);

    // ---- 5) 塵（Points 1つ）: 光の帯の周りを漂う。ゾーン付近は出さない ----
    const ND = 360;
    const dg = new THREE.BufferGeometry();
    const seed = new Float32Array(ND * 4), dp = new Float32Array(ND * 3);
    let s = 12345; const rnd = () => (s = (s * 1664525 + 1013904223) >>> 0) / 4294967296;
    for (let i = 0; i < ND; i++) {
      const side = i % 2 ? 1 : -1;
      seed.set([side * (2 + rnd() * (W - 3)), FLOOR + 1 + rnd() * 14, -4 - rnd() * 80, rnd()], i * 4);
    }
    dg.setAttribute('position', new THREE.BufferAttribute(dp, 3));
    dg.setAttribute('aSeed', new THREE.BufferAttribute(seed, 4));
    const dust = new THREE.Points(dg, new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime, uPx: { value: 800 } },
      vertexShader: /* glsl */ `
        attribute vec4 aSeed; uniform float uTime; uniform float uPx; varying float vA; varying float vT;
        void main() {
          float t = uTime * 0.06;
          vec3 p = aSeed.xyz;
          p.x += sin(t * 5.0 + aSeed.w * 40.0) * 1.2;
          p.y += mod(aSeed.w * 30.0 + t * 8.0 * (0.5 + aSeed.w), 16.0) - 8.0 * 0.0;
          p.y = ${FLOOR.toFixed(1)} + 1.0 + mod(aSeed.y - ${FLOOR.toFixed(1)} + uTime * (0.12 + 0.2 * aSeed.w), 16.0);
          p.z += cos(t * 4.0 + aSeed.w * 30.0) * 1.2;
          vec4 mv = viewMatrix * vec4(p, 1.0);
          gl_Position = projectionMatrix * mv;
          gl_PointSize = max(1.5, uPx * 0.012 * (0.6 + aSeed.w) / -mv.z * 10.0);
          vA = 0.35 + 0.65 * aSeed.w; vT = aSeed.w;
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        varying float vA; varying float vT;
        void main() {
          vec2 c = gl_PointCoord - 0.5; float r = length(c);
          float a = smoothstep(0.5, 0.0, r) * vA * 0.5;
          a *= 1.0 - museSpawnMask(museBaseNdc());
          vec3 col = mix(vec3(1.0, 0.6, 0.45), vec3(1.0, 0.8, 0.5), vT);
          gl_FragColor = vec4(col * a, 1.0);
          #include <colorspace_fragment>
        }`,
      transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false,
    }));
    dust.frustumCulled = false; dust.renderOrder = -60;
    root.add(dust);

    disposables.push(qg, floorG, colG, bg, dg, back.material, floor.material, cols.material, beams.material, dust.material);
    return {
      object: root,
      update({ t }) {
        uTime.value = t;
        try { dust.material.uniforms.uPx.value = renderer.getDrawingBufferSize(new THREE.Vector2()).y; } catch (e) { /* noop */ }
      },
      dispose() { disposables.forEach((o) => o.dispose()); cols.dispose(); },
    };
  },
};
