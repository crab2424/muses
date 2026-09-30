// orbit「神話の軌道都市」: 高空の橋から見る眼下の夜景 + 左右の幾何学神殿/リング + 奥の虚空のゲート
// draw call: 都市板1 + ゲート円盤1 + ゲート縁1 + 神殿2 + リング2 + 粒子1 = 8
export default {
  id: 'orbit', name: '神話の軌道都市', model: 'Sonnet 5.5',
  concept: '高空に架かる橋。眼下に夜の未来都市の灯り、左右に浮遊する神殿（列柱・破風）とリングをワイヤーで抽象化。奥に縁だけ淡く光る虚空のゲート。',
  palette: '深い紺〜群青の地。灯りは彩度を抑えた冷たいシアン(#5b93b0)と淡い金(#b39a64)、構造線は #6f93b8。ノーツ色より暗く低彩度。',
  motion: '都市の灯りが位相ずれでゆっくり揺らぐ（周期15〜25秒、点滅なし）／光の粒が昇る（0.5〜1 m/s）／リングと神殿がゆっくり回る（周期1〜2分）。ゲート付近は動かさない。',
  perf: '全画面板1（都市シェーダー: 2スケールのセルhash＋リング道路、ノイズなし）、線メッシュ5、粒子1（400点、頂点シェーダーで上昇）。draw 8。',
  unityCost: '都市板=Unlit シェーダー1本（逆射影でレイ→平面）。線は Unity では LineRenderer 非推奨のためメッシュ化（細い板）が必要。粒子は Points 相当をクアッド粒子に置換。',
  clearColor: '#03050d',
  shade: { color: '#02030a', strength: 0.2 },
  stage: {
    groundFill: '#0b1226', groundFillAlpha: 1, groundLine: '#7fa4cc', groundLineAlpha: 0.28, groundJudge: '#9fc4e8',
    skyFill: '#2c4f80', skyFillAlpha: 0.2, skyLine: '#7fa4cc', skyLineAlpha: 0.28, skyJudge: '#9fc4e8',
  },
  create({ THREE, zone, glsl, d, camera, aspect }) {
    const root = new THREE.Group();
    const disposables = [];
    const CITY_Y = -34;

    // ---------- 都市（全画面板1枚） ----------
    const qg = new THREE.BufferGeometry();
    qg.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3));
    qg.setIndex([0, 1, 2, 0, 2, 3]);
    const cityMat = new THREE.ShaderMaterial({
      uniforms: {
        ...zone.uniforms, uTime: { value: 0 },
        uInvProj: { value: camera.projectionMatrixInverse.clone() },
        uCamRot: { value: new THREE.Matrix3().setFromMatrix4(camera.matrixWorld) },
        uCamPos: { value: camera.position.clone() },
        uCityY: { value: CITY_Y },
      },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime, uCityY; uniform mat4 uInvProj; uniform mat3 uCamRot; uniform vec3 uCamPos;
        float h11(float n) { return fract(sin(n * 127.1) * 43758.5453); }
        float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        // 1スケール分の灯り。cs=セル寸法。aa=1ピクセルがセル何個分か
        vec3 lights(vec2 p, float cs, float aa, float dens, float t) {
          vec2 q = p / cs, id = floor(q), f = fract(q) - 0.5;
          float h = h21(id), h2 = h21(id + 7.3), h3 = h21(id + 19.1);
          float lit = step(1.0 - dens, h);
          vec2 off = (vec2(h2, h3) - 0.5) * 0.4;
          float r = 0.05 + 0.09 * h3;
          float soft = max(aa, 0.04);
          float dot_ = (1.0 - smoothstep(r - soft, r + soft, length(f - off))) * lit;
          float sway = 0.72 + 0.28 * sin(t * (0.25 + 0.2 * h2) + h * 40.0); // ゆっくり揺らぐ
          // 画素より小さくなったら平均輝度へ移行（モアレ防止）
          float avg = lit * 3.14159 * r * r;
          float fade = smoothstep(0.25, 0.7, aa);
          float a = mix(dot_, avg, fade) * sway;
          vec3 cool = vec3(0.30, 0.55, 0.70), gold = vec3(0.72, 0.60, 0.36);
          return a * mix(cool, gold, step(0.78, h3 * 0.6 + h2 * 0.5));
        }
        void main() {
          vec2 ndc = museBaseNdc();
          vec4 v = uInvProj * vec4(ndc, 1.0, 1.0);
          vec3 dir = normalize(uCamRot * (v.xyz / v.w));
          vec3 col = vec3(0.012, 0.022, 0.060);
          float mask = museSpawnMask(ndc);
          if (dir.y < -0.004) {
            float tt = (uCityY - uCamPos.y) / dir.y;
            vec3 hit = uCamPos + dir * tt;
            vec2 p = hit.xz;
            vec2 dp = fwidth(p);
            float px = max(dp.x, dp.y);
            // 碁盤（近い街区）と密な細かい灯り
            vec3 L = lights(p, 4.0, px / 4.0, 0.6, uTime) * 0.8;
            L += lights(p + 31.7, 1.5, px / 1.5, 0.4, uTime + 5.0) * 0.45;
            // 街路の碁盤（ごく淡い）
            vec2 gq = abs(fract(p / 12.0 + 0.5) - 0.5) * 12.0;
            float gl = 1.0 - smoothstep(0.0, max(px, 0.12) * 1.5, min(gq.x, gq.y));
            L += vec3(0.05, 0.10, 0.16) * gl * (1.0 - smoothstep(0.15, 0.6, px));
            // 放射状の大通り: 都市中心(0,-140)から
            vec2 c = p - vec2(0.0, -140.0);
            float rr = length(c), ang = atan(c.y, c.x);
            float steps = 36.0;
            float da = abs(fract(ang / 6.28318 * steps + 0.5) - 0.5) * rr * 6.28318 / steps; // 通りまでの距離
            float aaR = max(px, 0.2);
            float road = 1.0 - smoothstep(0.0, aaR * 1.6 + 0.1, da);
            float beads = 0.5 + 0.5 * sin(rr * 0.9 - 0.0);
            road *= (0.35 + 0.65 * smoothstep(0.55, 1.0, beads)) * smoothstep(20.0, 45.0, rr);
            float ringD = abs(fract(rr / 42.0) - 0.5) * 42.0;
            float ringLine = 1.0 - smoothstep(0.0, aaR * 1.6 + 0.08, abs(fract(rr / 42.0 + 0.5) - 0.5) * 42.0);
            L += vec3(0.16, 0.30, 0.42) * (road * 0.22 + ringLine * 0.14) * (1.0 - smoothstep(0.3, 1.2, px));
            // 遠方は霞へ
            float haze = smoothstep(120.0, 520.0, tt);
            col += L * (1.0 - 0.8 * haze);
            col = mix(col, vec3(0.020, 0.040, 0.095), haze * 0.85);
            // 眼下の深さ感: 画面下ほどわずかに明るい紺
            col += vec3(0.004, 0.010, 0.026) * smoothstep(0.2, -1.0, ndc.y);
          }
          col *= 1.0 - 0.92 * mask; // ゾーン内は世界側でも沈める（ゲートの外側の霞）
          gl_FragColor = vec4(col, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    });
    const city = new THREE.Mesh(qg, cityMat);
    city.frustumCulled = false; city.renderOrder = -100;
    root.add(city);
    disposables.push(qg, cityMat);

    // ---------- 虚空のゲート（奥。縁だけ淡く光る） ----------
    const GATE_Z = -95, GATE_V = 0.9;
    const ndcRay = (x, y) => new THREE.Vector3(x, y, 0.5).unproject(camera).sub(camera.position).normalize();
    const onZ = (x, y, z) => { const r = ndcRay(x, y); return camera.position.clone().addScaledVector(r, (z - camera.position.z) / r.z); };
    const gc = onZ(0, GATE_V, GATE_Z);
    const gateR = onZ(0.23, GATE_V, GATE_Z).x; // 円の半径（横幅がゾーン±0.156より広い）
    const gate = new THREE.Group();
    gate.position.copy(gc);
    const disc = new THREE.Mesh(new THREE.CircleGeometry(gateR, 64), new THREE.MeshBasicMaterial({ color: 0x010208, depthTest: false, depthWrite: false }));
    disc.renderOrder = -90; disc.frustumCulled = false;
    gate.add(disc);
    const rimPts = [];
    const ringSeg = (r, n, z, col, k) => {
      for (let i = 0; i < n; i++) {
        const a0 = (i / n) * Math.PI * 2, a1 = ((i + 1) / n) * Math.PI * 2;
        rimPts.push(Math.cos(a0) * r, Math.sin(a0) * r, z, ...col.map((c) => c * k), Math.cos(a1) * r, Math.sin(a1) * r, z, ...col.map((c) => c * k));
      }
    };
    const rimCol = [0.42, 0.62, 0.80];
    ringSeg(gateR, 96, 0.1, rimCol, 0.9);
    ringSeg(gateR * 1.06, 96, 0.1, rimCol, 0.35);
    // 奥へ続く同心リング（暗く）
    for (let k = 1; k <= 3; k++) ringSeg(gateR * (1 - 0.22 * k), 72, 0.1, rimCol, 0.28 / k);
    // 上端の小さな破風（三角）
    const tri = (w, h, y0, k) => {
      const pts = [[-w, y0], [w, y0], [0, y0 + h]];
      for (let i = 0; i < 3; i++) { const a = pts[i], b = pts[(i + 1) % 3]; rimPts.push(a[0], a[1], 0.1, ...rimCol.map((c) => c * k), b[0], b[1], 0.1, ...rimCol.map((c) => c * k)); }
    };
    tri(gateR * 0.32, gateR * 0.2, gateR * 1.1, 0.5);
    const rg = new THREE.BufferGeometry();
    const pos = [], colr = [];
    for (let i = 0; i < rimPts.length; i += 6) { pos.push(rimPts[i], rimPts[i + 1], rimPts[i + 2]); colr.push(rimPts[i + 3], rimPts[i + 4], rimPts[i + 5]); }
    rg.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    rg.setAttribute('color', new THREE.Float32BufferAttribute(colr, 3));
    const lineMat = (op) => new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: op, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false });
    const rim = new THREE.LineSegments(rg, lineMat(0.6));
    rim.renderOrder = -80; rim.frustumCulled = false;
    gate.add(rim);
    root.add(gate);
    disposables.push(disc.geometry, disc.material, rg, rim.material);

    // ---------- 構造体ヘルパ（線分に貯める） ----------
    const cyan = [0.36, 0.52, 0.72], gold = [0.70, 0.60, 0.38];
    const mk = () => ({ pos: [], col: [] });
    const seg = (b, a, c, col, k) => { b.pos.push(...a, ...c); b.col.push(...col.map((x) => x * k), ...col.map((x) => x * k)); };
    const box = (b, cx, cy, cz, w, h, dd, col, k) => {
      const x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2, z0 = cz - dd / 2, z1 = cz + dd / 2;
      const P = [[x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0], [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]];
      [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]].forEach(([i, j]) => seg(b, P[i], P[j], col, k));
    };
    const finish = (b, op) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(b.pos, 3));
      g.setAttribute('color', new THREE.Float32BufferAttribute(b.col, 3));
      const m = lineMat(op); const l = new THREE.LineSegments(g, m);
      l.frustumCulled = false; l.renderOrder = -70;
      disposables.push(g, m);
      return l;
    };
    // 神殿: 台座 + 列柱 + 梁 + 破風。原点中心、+z が正面
    const temple = (b, S) => {
      box(b, 0, 0, 0, 22 * S, 1.0 * S, 12 * S, cyan, 1.0);          // 浮遊台座
      box(b, 0, -1.1 * S, 0, 17 * S, 0.8 * S, 8 * S, cyan, 0.5);
      const n = 6;
      for (let i = 0; i < n; i++) {
        const x = (-8 + (16 * i) / (n - 1)) * S;
        box(b, x, 4.2 * S, 4.2 * S, 0.9 * S, 7 * S, 0.9 * S, cyan, 0.9);
        box(b, x, 4.2 * S, -4.2 * S, 0.9 * S, 7 * S, 0.9 * S, cyan, 0.5);
      }
      box(b, 0, 8.2 * S, 0, 19 * S, 1.0 * S, 10.6 * S, gold, 0.85);   // 梁
      // 破風（正面と背面の三角）
      [4.8, -4.8].forEach((z) => {
        const a = [-9.5 * S, 8.7 * S, z * S], c = [9.5 * S, 8.7 * S, z * S], t = [0, 12.6 * S, z * S];
        seg(b, a, c, gold, 0.85); seg(b, a, t, gold, 0.85); seg(b, c, t, gold, 0.85);
      });
      seg(b, [-9.5 * S, 8.7 * S, 4.8 * S], [-9.5 * S, 8.7 * S, -4.8 * S], gold, 0.5);
      seg(b, [9.5 * S, 8.7 * S, 4.8 * S], [9.5 * S, 8.7 * S, -4.8 * S], gold, 0.5);
      seg(b, [0, 12.6 * S, 4.8 * S], [0, 12.6 * S, -4.8 * S], gold, 0.6);
    };
    const structs = [];
    for (const sgn of [-1, 1]) {
      const b = mk();
      temple(b, 1.0);
      // 周囲の小さな浮遊足場
      box(b, -sgn * 20, -2, 10, 8, 0.6, 6, cyan, 0.6);
      box(b, sgn * 16, 5, -22, 6, 0.5, 5, cyan, 0.4);
      const l = finish(b, 0.55);
      const g = new THREE.Group();
      g.add(l);
      g.position.set(sgn * 34, -7, -46);
      g.rotation.y = sgn * -0.35; // 正面をやや中央へ
      root.add(g);
      structs.push({ g, sgn, baseY: g.rotation.y, py: -7 });
    }
    // リング（傾けて回転）
    const rings = [];
    for (const sgn of [-1, 1]) {
      const b = mk();
      const ringLines = (r, n, k, col) => {
        for (let i = 0; i < n; i++) {
          const a0 = (i / n) * Math.PI * 2, a1 = ((i + 1) / n) * Math.PI * 2;
          seg(b, [Math.cos(a0) * r, 0, Math.sin(a0) * r], [Math.cos(a1) * r, 0, Math.sin(a1) * r], col, k);
        }
      };
      ringLines(15, 96, 0.8, cyan); ringLines(13.6, 96, 0.4, cyan); ringLines(19, 96, 0.35, gold);
      for (let i = 0; i < 36; i++) { // 目盛り
        const a = (i / 36) * Math.PI * 2, r0 = 15, r1 = i % 3 === 0 ? 17.4 : 16;
        seg(b, [Math.cos(a) * r0, 0, Math.sin(a) * r0], [Math.cos(a) * r1, 0, Math.sin(a) * r1], gold, i % 3 === 0 ? 0.7 : 0.4);
      }
      const l = finish(b, 0.5);
      const g = new THREE.Group(); const spin = new THREE.Group();
      spin.add(l); g.add(spin);
      g.position.set(sgn * 46, 6, -62);
      g.rotation.set(0.5, 0, sgn * 0.35);
      root.add(g);
      rings.push({ spin, sgn });
    }

    // ---------- 粒子（400点、頂点シェーダーで上昇） ----------
    const N = 400;
    const pg = new THREE.BufferGeometry();
    const seedA = new Float32Array(N * 4), posA = new Float32Array(N * 3);
    let s = 12345;
    const rnd = () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
    for (let i = 0; i < N; i++) {
      const side = rnd() < 0.5 ? -1 : 1;
      seedA[i * 4] = side * (8 + rnd() * 62);     // x
      seedA[i * 4 + 1] = rnd();                  // 位相
      seedA[i * 4 + 2] = -(12 + rnd() * 88);     // z
      seedA[i * 4 + 3] = 0.6 + rnd() * 0.8;      // 速さ係数
    }
    pg.setAttribute('position', new THREE.BufferAttribute(posA, 3));
    pg.setAttribute('aSeed', new THREE.BufferAttribute(seedA, 4));
    const pm = new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime: { value: 0 }, uPx: { value: 1 } },
      vertexShader: /* glsl */ `
        attribute vec4 aSeed; uniform float uTime, uPx; varying float vA; varying float vG;
        void main() {
          float H = 44.0;
          float ph = fract(aSeed.y + uTime * 0.018 * aSeed.w);   // 約1〜1.5分で1周（0.8 m/s前後）
          vec3 p = vec3(aSeed.x + 1.5 * sin(uTime * 0.2 + aSeed.y * 30.0), -34.0 + ph * H, aSeed.z);
          vA = smoothstep(0.0, 0.15, ph) * (1.0 - smoothstep(0.65, 1.0, ph));
          vG = fract(aSeed.y * 91.7);
          vec4 mv = modelViewMatrix * vec4(p, 1.0);
          gl_Position = projectionMatrix * mv;
          gl_PointSize = uPx * clamp(60.0 / -mv.z, 1.5, 3.2);
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        varying float vA; varying float vG;
        void main() {
          vec2 c = gl_PointCoord - 0.5;
          float a = (1.0 - smoothstep(0.15, 0.5, length(c))) * vA;
          vec3 col = mix(vec3(0.30, 0.52, 0.66), vec3(0.72, 0.62, 0.40), step(0.8, vG));
          a *= 0.55;
          a *= 1.0 - museSpawnMask(museBaseNdc()); // ノーツ出現位置には出さない
          gl_FragColor = vec4(col * a, a);
        }`,
      transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false,
    });
    const pts = new THREE.Points(pg, pm);
    pts.frustumCulled = false; pts.renderOrder = -60;
    root.add(pts);
    disposables.push(pg, pm);

    void aspect; void d;
    return {
      object: root,
      update({ t }) {
        cityMat.uniforms.uTime.value = t;
        pm.uniforms.uTime.value = t;
        pm.uniforms.uPx.value = window.devicePixelRatio || 1;
        for (const s of structs) s.g.rotation.y = s.baseY + s.sgn * 0.12 * Math.sin(t * 0.06);
        for (const r of rings) r.spin.rotation.y = t * 0.05 * r.sgn;
      },
      dispose() { disposables.forEach((o) => o.dispose && o.dispose()); },
    };
  },
};
