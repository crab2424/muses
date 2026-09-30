// 天上の聖域 r2: 共通の暗幕なし。雲海に朝焼け/薄暮の色の層（桃・薄紫・琥珀）、影側は青紫。
// 選定: 浮島（大理石の遺構つき）を主役、ガラスの破片（虹色の反射）を添える。剣は上から見下ろすと細くて読めないので外した。
// 既存の大理石の八面体破片・自立柱は浮島の遺構と役割が重なるので削除（柱は浮島に含める）。光の輪は金色で太く大きく。
// draw: 雲海1 / 浮島1 / ガラス1 / 金の輪1 / 光芒1 / モノリス1 = 6
export default {
  id: 'sanctuary-r2', name: '天上の聖域 r2', model: 'Sonnet 5.5',
  concept: '朝焼けと薄暮の色が層になった雲海の上に、大理石の遺構をのせた浮島が浮かぶ。ガラスの破片が虹色を返し、金の光輪が空を巡る。奥（消失点）だけに黒い逆三角のモノリスが抜ける。',
  palette: '雲 白〜淡い桃 #ffd0bd / 薄紫 #cdbdf2 / 琥珀 #ffdca0、影は青紫 #6c6eb4、ステージ脇は沈めた青紫 #4a5490。光の輪 金 #ffd27a。ガラスは淡い虹色（彩度低）。ノーツ色の飽和色は使わない。',
  motion: '雲は右へゆっくり流れ、色の層もゆっくり移ろう。浮島・ガラスは周期10〜20秒で上下・ゆっくり自転。金の輪は微小な揺れと自転。光芒は明るさが±10%ゆらぐ。点滅なし。',
  perf: 'draw 6（雲海の全画面板1 / 浮島1 / ガラス1 / 金の輪1 / 光芒1 / モノリス1）。暗幕なし。粒子なし。雲は4オクターブ。動きは頂点シェーダー（CPU更新なし）。',
  unityCost: '中〜低。雲海シェーダー1枚＋Instanced 3メッシュ（浮島・ガラス・輪）＋加算板＋モノリスのメッシュ。全部 Unlit、テクスチャ不要。',
  clearColor: '#8fb0d8',
  shade: { color: '#0a1020', strength: 0 },
  stage: {
    groundFill: '#232848', groundFillAlpha: 1, groundLine: '#e8e4ff', groundLineAlpha: 0.38, groundJudge: '#ffffff',
    skyFill: '#3f4878', skyFillAlpha: 0.22, skyLine: '#e8e4ff', skyLineAlpha: 0.34, skyJudge: '#ffffff',
  },
  create({ THREE, zone, glsl, d, camera, laneX }) {
    const root = new THREE.Group();
    const CAM_Y = 8, CLOUD_Y = -9;

    // ステージ半幅の線形近似（地上、ワールド）: half(z) = a + b*z
    const zA = d.zJudge, zB = d.zFar;
    const hA = laneX(1, 0, zA), hB = laneX(1, 0, zB);
    const hb = (hB - hA) / (zB - zA), ha = hA - hb * zA;

    // ---------- 1) 雲海（全画面の板1枚） ----------
    const qg = new THREE.BufferGeometry();
    qg.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3));
    qg.setIndex([0, 1, 2, 0, 2, 3]);
    const cloud = new THREE.Mesh(qg, new THREE.ShaderMaterial({
      uniforms: {
        ...zone.uniforms, uTime: { value: 0 },
        uInvProj: { value: camera.projectionMatrixInverse.clone() },
        uCamRot: { value: new THREE.Matrix3().setFromMatrix4(camera.matrixWorld) },
        uStageHalf: { value: new THREE.Vector2(ha, hb) },
      },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime; uniform mat4 uInvProj; uniform mat3 uCamRot; uniform vec2 uStageHalf;
        float h21(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
        float vn(vec2 p) {
          vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
          return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
        }
        float fbm(vec2 p) {
          float a = 0.5, s = 0.0;
          for (int i = 0; i < 4; i++) { s += a * vn(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; }
          return s;
        }
        void main() {
          vec2 ndc = museBaseNdc();
          vec4 vp = uInvProj * vec4(ndc, 1.0, 1.0);
          vec3 dir = normalize(uCamRot * (vp.xyz / vp.w));
          vec3 hazeB = vec3(0.70, 0.78, 0.96), hazeW = vec3(0.98, 0.76, 0.74);
          vec3 c;
          // 色の層: 大きな尺度の低周波ノイズがゆっくり移ろう（桃・薄紫・琥珀）
          float band = vn(dir.xz * (-8.0 / min(dir.y, -0.02)) * 0.022 + vec2(uTime * 0.0025, 3.0 - uTime * 0.0015));
          float band2 = vn(dir.xz * (-8.0 / min(dir.y, -0.02)) * 0.05 + vec2(9.0, uTime * 0.003));
          vec3 haze = mix(hazeB, hazeW, smoothstep(0.35, 0.75, band));
          if (dir.y > -0.02) {
            c = mix(haze, vec3(0.55, 0.72, 0.93), clamp(dir.y * 6.0 + 0.2, 0.0, 1.0));
          } else {
            float t = (${CLOUD_Y.toFixed(1)} - ${CAM_Y.toFixed(1)}) / dir.y;
            vec2 wp = dir.xz * t;
            vec2 q = wp * 0.028 + vec2(uTime * 0.010, uTime * 0.004);
            float n = fbm(q + fbm(q * 0.6 + 3.1) * 0.9);
            float dens = smoothstep(0.38, 0.68, n);
            float n2 = fbm(q + vec2(0.0, -0.05) + fbm(q * 0.6 + 3.1) * 0.9);
            float lit = clamp(0.5 + (n - n2) * 14.0, 0.0, 1.0);
            // 淡い色の層: 遠いほど暖色（桃）、近いほど薄紫。琥珀は明るい縁だけ
            float far = smoothstep(60.0, 300.0, t);
            vec3 peach = vec3(1.0, 0.72, 0.66), lav = vec3(0.74, 0.68, 0.96), amber = vec3(1.0, 0.82, 0.52);
            vec3 tint = mix(lav, peach, clamp(band * 1.5 + far * 0.6 - 0.45, 0.0, 1.0));
            tint = mix(tint, amber, smoothstep(0.55, 0.9, band2) * 0.7);
            vec3 cWhite = mix(vec3(0.98, 0.98, 1.0), tint, 0.9);
            vec3 cShade = mix(vec3(0.50, 0.60, 0.84), vec3(0.47, 0.44, 0.76), band);      // 影は青紫寄り
            vec3 cGap = mix(vec3(0.36, 0.50, 0.82), vec3(0.44, 0.38, 0.72), smoothstep(0.3, 0.8, band2));
            c = mix(cGap, mix(cShade, cWhite, lit * 0.75 + 0.25), dens);
            float fade = 1.0 - exp(-t * 0.0045);
            c = mix(c, haze, clamp(fade * 1.15, 0.0, 1.0));
            // ステージの左右すぐ脇を青紫に沈める
            float t0 = -${CAM_Y.toFixed(1)} / dir.y;
            vec2 p0 = dir.xz * t0;
            float hw0 = uStageHalf.x + uStageHalf.y * (-p0.y);
            float dx = max(abs(p0.x) - hw0, 0.0);
            float sink = exp(-dx / (3.5 + 0.07 * t0));
            c = mix(c, vec3(0.26, 0.30, 0.52), 0.62 * sink);
          }
          // 消失点まわりを暗い霞に（モノリスが立つ奥）
          vec2 e = (ndc - vec2(0.0, 0.93)) / vec2(0.42, 0.34);
          float hole = 1.0 - smoothstep(0.0, 1.0, length(e));
          c = mix(c, vec3(0.045, 0.06, 0.12), 0.7 * hole);
          c *= 1.0 - 0.6 * museSpawnMask(ndc);
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    }));
    cloud.frustumCulled = false; cloud.renderOrder = -100;
    root.add(cloud);

    // ---------- 共通: 実時間ユニフォーム ----------
    const uTime = { value: 0 };

    // 決定的な擬似乱数
    let rs = 12345; const rnd = () => (rs = (rs * 1664525 + 1013904223) >>> 0) / 4294967296;
    const m4 = new THREE.Matrix4(), qt = new THREE.Quaternion(), eu = new THREE.Euler(), v3 = new THREE.Vector3(), sc = new THREE.Vector3();
    // aSeed: x=位相 y=周期係数 z=自転係数 w=明るさ
    function inst(geom, mat, items) {
      const mesh = new THREE.InstancedMesh(geom, mat, items.length);
      const seed = new Float32Array(items.length * 4);
      items.forEach((it, i) => {
        eu.set(it.rx || 0, it.ry || 0, it.rz || 0); qt.setFromEuler(eu);
        v3.set(it.x, it.y, it.z); sc.set(it.sx, it.sy, it.sz);
        m4.compose(v3, qt, sc); mesh.setMatrixAt(i, m4);
        seed.set([rnd(), rnd(), (rnd() - 0.5) * 2, rnd()], i * 4);
      });
      geom.setAttribute('aSeed', new THREE.InstancedBufferAttribute(seed, 4));
      mesh.frustumCulled = false;
      return mesh;
    }
    // 浮遊の頂点シェーダー（uSpin=自転の速さ、uBob=上下の振幅）
    const mkVs = (extra) => /* glsl */ `
      attribute vec4 aSeed; uniform float uTime, uSpin, uBob;
      ${extra.attr || ''}
      varying vec3 vN; varying float vDist; varying float vB; varying vec3 vView; varying vec3 vLocal; ${extra.vary || ''}
      void main() {
        float ph = aSeed.x * 6.2831 + uTime * (0.30 + aSeed.y * 0.22);
        float a = uTime * uSpin * aSeed.z + aSeed.x * 6.2831;
        float ca = cos(a), sa = sin(a);
        vec3 p = position, n = normal;
        p = vec3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z);
        n = vec3(ca * n.x + sa * n.z, n.y, -sa * n.x + ca * n.z);
        vec4 wp = modelMatrix * instanceMatrix * vec4(p, 1.0);
        wp.y += sin(ph) * uBob;
        wp.x += cos(ph * 0.7) * 0.25;
        vN = normalize(mat3(modelMatrix * instanceMatrix) * n);
        vDist = length(wp.xyz - cameraPosition);
        vView = normalize(cameraPosition - wp.xyz);
        vB = aSeed.w; vLocal = position;
        ${extra.body || ''}
        gl_Position = projectionMatrix * viewMatrix * wp;
      }`;
    const mkMat = (vs, fragBody, uni, blendOpts) => new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime, ...uni },
      vertexShader: vs,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        varying vec3 vN; varying float vDist; varying float vB; varying vec3 vView; varying vec3 vLocal; uniform float uTime; ${blendOpts.fragDecl || ''}
        void main() {
          ${fragBody}
          c *= 1.0 - 0.9 * museSpawnMask(museBaseNdc());
          gl_FragColor = vec4(c, al);
          #include <colorspace_fragment>
        }`,
      transparent: !!blendOpts.transparent, depthWrite: !blendOpts.transparent,
      blending: blendOpts.additive ? THREE.AdditiveBlending : THREE.NormalBlending,
    });

    // --- 浮島の形: 平たい上面＋逆円錐の岩＋大理石の遺構（柱3本・欠けた柱・まぐさ）を1ジオメトリに結合。aKind: 0=岩 1=上面 2=大理石
    function mergeKinds(parts) {
      const P = [], N = [], K = [];
      for (const { g, kind, m } of parts) {
        const ng = g.index ? g.toNonIndexed() : g.clone();
        ng.applyMatrix4(m);
        ng.computeVertexNormals();
        const pa = ng.attributes.position.array, na = ng.attributes.normal.array;
        for (let i = 0; i < pa.length; i++) { P.push(pa[i]); N.push(na[i]); }
        for (let i = 0; i < pa.length / 3; i++) K.push(kind);
      }
      const out = new THREE.BufferGeometry();
      out.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      out.setAttribute('normal', new THREE.Float32BufferAttribute(N, 3));
      out.setAttribute('aKind', new THREE.Float32BufferAttribute(K, 1));
      return out;
    }
    const T = (x, y, z, sx = 1, sy = 1, sz = 1, ry = 0) => new THREE.Matrix4().compose(new THREE.Vector3(x, y, z), new THREE.Quaternion().setFromEuler(new THREE.Euler(0, ry, 0)), new THREE.Vector3(sx, sy, sz));
    const islandGeom = (() => {
      const parts = [];
      // 上面（少し盛り上がった円盤）
      parts.push({ g: new THREE.CylinderGeometry(1.0, 0.94, 0.16, 14, 1), kind: 1, m: T(0, 0, 0) });
      // 岩: 逆円錐を不規則にゆがめる（下向きの先端）
      const cone = new THREE.ConeGeometry(0.94, 1.5, 10, 3, true);
      const cp = cone.attributes.position;
      for (let i = 0; i < cp.count; i++) {
        const x = cp.getX(i), y = cp.getY(i), z = cp.getZ(i);
        const h = Math.sin(x * 9.1 + z * 5.3 + y * 3.7) * 0.10 + Math.sin(z * 7.7 - y * 6.1) * 0.06;
        const k = 1 + h;
        cp.setXYZ(i, x * k, y + h * 0.3, z * k);
      }
      cone.rotateX(Math.PI);
      parts.push({ g: cone, kind: 0, m: T(0, -0.08 - 0.75, 0) });
      parts.push({ g: new THREE.ConeGeometry(0.28, 0.9, 6, 1, true).rotateX(Math.PI), kind: 0, m: T(0.4, -1.0, 0.25) });
      // 遺構
      const col = (x, z, h, r = 0.055) => parts.push({ g: new THREE.CylinderGeometry(r * 0.9, r, h, 8, 1), kind: 2, m: T(x, 0.08 + h / 2, z) });
      col(-0.42, 0.05, 0.62); col(-0.12, 0.05, 0.62); col(0.18, 0.05, 0.62); col(0.55, -0.35, 0.28);
      parts.push({ g: new THREE.BoxGeometry(0.85, 0.07, 0.2), kind: 2, m: T(-0.12, 0.08 + 0.62 + 0.035, 0.05) });
      parts.push({ g: new THREE.BoxGeometry(0.92, 0.04, 0.3), kind: 2, m: T(-0.12, 0.10, 0.05) });
      return mergeKinds(parts);
    })();
    // 浮島の配置: y<0 は視線が地上面(|x|≈7.3)の内側を通らないよう x を十分外に
    const islandItems = [
      { x: -17, y: -1, z: -18, s: 4.6 }, { x: 19, y: -3, z: -26, s: 5.4 },
      { x: -30, y: 1.5, z: -40, s: 7.0 }, { x: 27, y: 2, z: -54, s: 6.4 },
      { x: -13.5, y: 4, z: -13, s: 2.4 }, { x: 34, y: -2, z: -14, s: 3.2 },
    ].map((it) => ({ x: it.x, y: it.y, z: it.z, sx: it.s, sy: it.s * 0.85, sz: it.s, ry: rnd() * 6 }));
    const islandMat = mkMat(mkVs({ attr: 'attribute float aKind;', vary: 'varying float vK;', body: 'vK = aKind;' }), /* glsl */ `
      vec3 n = normalize(vN);
      float l = clamp(dot(n, normalize(vec3(-0.35, 0.8, -0.45))) * 0.5 + 0.5, 0.0, 1.0);
      vec3 marble = mix(vec3(0.46, 0.44, 0.72), vec3(1.0, 0.97, 0.93), l);
      vec3 rock = mix(vec3(0.22, 0.22, 0.44), vec3(0.66, 0.52, 0.62), l * l);
      vec3 top = mix(vec3(0.52, 0.66, 0.62), vec3(0.86, 0.90, 0.72), l);
      float kk = vK;
      vec3 c = kk < 0.5 ? rock : (kk < 1.5 ? top : marble);
      c *= 0.9 + 0.1 * vB;
      // 底は深い青紫へ落とし、遠方は霞へ
      c = mix(c, vec3(0.26, 0.28, 0.5), (kk < 0.5 ? 0.4 : 0.0) * clamp(0.5 - vLocal.y * 0.3, 0.0, 1.0));
      c = mix(c, vec3(0.80, 0.82, 0.96), clamp(1.0 - exp(-vDist * 0.005), 0.0, 0.6));
      float al = 1.0;`, { uSpin: { value: 0.03 }, uBob: { value: 0.55 } }, { fragDecl: 'varying float vK;' });
    const islandMesh = inst(islandGeom, islandMat, islandItems);
    root.add(islandMesh);

    // --- ガラスの破片: 細長い双錐（平面ごとに虹色の薄膜反射）。浮島の周りに散らす
    const glassItems = [];
    for (let i = 0; i < 22; i++) {
      const side = i % 2 ? 1 : -1, z = -(9 + rnd() * 55);
      const s = 0.7 + rnd() * 1.3;
      glassItems.push({ x: side * (12 + rnd() * 12 + (-z) * 0.28), y: -3 + rnd() * 9, z, sx: s * 0.55, sy: s * 2.2, sz: s * 0.4,
        rx: (rnd() - 0.5) * 1.2, rz: (rnd() - 0.5) * 1.2, ry: rnd() * 6 });
    }
    const glassGeom = new THREE.OctahedronGeometry(1, 0);
    const glassMat = mkMat(mkVs({}), /* glsl */ `
      vec3 n = normalize(vN);
      float f = 1.0 - abs(dot(n, normalize(vView)));
      float ph = f * 1.6 + vB * 2.0 + uTime * 0.02;
      vec3 iri = 0.5 + 0.5 * cos(6.2831 * (ph + vec3(0.0, 0.33, 0.67)));
      vec3 base = mix(vec3(0.82, 0.90, 1.0), vec3(1.0), clamp(dot(n, normalize(vec3(-0.3, 0.8, -0.5))), 0.0, 1.0));
      vec3 c = mix(base, iri, 0.32 + 0.25 * f) * 0.95;
      c = mix(c, vec3(0.80, 0.82, 0.96), clamp(1.0 - exp(-vDist * 0.008), 0.0, 0.7));
      float al = 0.55 + 0.35 * f;`, { uSpin: { value: 0.12 }, uBob: { value: 0.7 } }, { transparent: true });
    const glassMesh = inst(glassGeom, glassMat, glassItems);
    glassMesh.renderOrder = -40;
    root.add(glassMesh);

    // --- 光の輪（金）: 太めの環。上に浮く光輪として島の脇の高い位置に。少しカメラ側へ向ける
    const ringItems = [];
    const ringSpec = [[-26, 4.5, -22, 8.5], [27, 6, -34, 9.5], [-33, 8, -50, 12], [15.5, 9, -14, 4.5]];
    ringSpec.forEach(([x, y, z, r], i) => ringItems.push({ x, y, z, sx: r, sy: r, sz: r, ry: (x < 0 ? 1 : -1) * 0.5, rx: -0.5 + i * 0.05 }));
    const ringGeom = new THREE.TorusGeometry(1, 0.05, 8, 128);
    const ringMat = mkMat(mkVs({}), /* glsl */ `
      float f = 1.0 - abs(dot(normalize(vN), normalize(vView)));
      vec3 c = mix(vec3(1.0, 0.80, 0.42), vec3(1.0, 0.92, 0.66), f) * (0.85 + 0.15 * vB);
      c = mix(c, vec3(0.98, 0.84, 0.66), clamp(1.0 - exp(-vDist * 0.006), 0.0, 0.5));
      float al = 0.95;`, { uSpin: { value: 0.05 }, uBob: { value: 0.3 } }, { transparent: true });
    const ringMesh = inst(ringGeom, ringMat, ringItems);
    ringMesh.renderOrder = -45;
    root.add(ringMesh);

    // ---------- 3) 光芒（加算の板をまとめた1メッシュ） ----------
    {
      const pos = [], uv = [], sd = [], idx = [];
      const rays = 8;
      for (let i = 0; i < rays; i++) {
        const side = i % 2 ? 1 : -1, k = i >> 1;
        const sx = side * (6 + k * 7), ex = side * (26 + k * 13);
        const S = [sx, 34, -96], E = [ex, -8, -22 - k * 4];
        const w0 = 1.5, w1 = 4.5 + k * 1.2;
        const base = pos.length / 3;
        pos.push(S[0] - w0, S[1], S[2], S[0] + w0, S[1], S[2], E[0] + w1, E[1], E[2], E[0] - w1, E[1], E[2]);
        uv.push(-1, 0, 1, 0, 1, 1, -1, 1);
        const s = rnd(); sd.push(s, s, s, s);
        idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
      g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
      g.setAttribute('aSeed', new THREE.Float32BufferAttribute(sd, 1));
      g.setIndex(idx);
      const m = new THREE.Mesh(g, new THREE.ShaderMaterial({
        uniforms: { ...zone.uniforms, uTime },
        vertexShader: /* glsl */ `
          attribute float aSeed; uniform float uTime; varying vec2 vUv; varying float vS;
          void main() {
            vUv = uv; vS = aSeed;
            vec3 p = position; p.x += sin(uTime * 0.18 + aSeed * 40.0) * 1.2 * uv.y;
            gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(p, 1.0);
          }`,
        fragmentShader: /* glsl */ `
          ${glsl.spawn}
          uniform float uTime; varying vec2 vUv; varying float vS;
          void main() {
            float across = 1.0 - smoothstep(0.0, 1.0, abs(vUv.x));
            float along = smoothstep(0.0, 0.25, vUv.y) * (1.0 - smoothstep(0.55, 1.0, vUv.y));
            float breathe = 0.9 + 0.1 * sin(uTime * 0.25 + vS * 30.0);
            float a = across * across * along * 0.16 * breathe;
            a *= 1.0 - museSpawnMask(museBaseNdc());
            vec3 rc = mix(vec3(1.0, 0.86, 0.62), vec3(1.0, 0.80, 0.86), step(0.5, fract(vS * 7.0)));
            gl_FragColor = vec4(rc * a, a);
            #include <colorspace_fragment>
          }`,
        transparent: true, depthWrite: false, depthTest: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
      }));
      m.frustumCulled = false; m.renderOrder = -50;
      root.add(m);
    }

    // ---------- 4) モノリス: 消失点の黒い逆三角（画面座標で形を決め、カメラから一定距離のワールドに置く） ----------
    {
      const D = 150;
      const at = (x, y) => {
        const p = new THREE.Vector3(x, y, 0.5).unproject(camera);
        return p.sub(camera.position).normalize().multiplyScalar(D).add(camera.position);
      };
      // 半幅 = k*(v - vApex)。ゾーン下端(v=0.79)で u=±0.17、頂点は地上面に隠れる位置
      const vApex = 0.5, vTop = 1.1, k = 0.19 / (0.79 - vApex);
      const hw = (v) => k * (v - vApex);
      const edge = 0.012; // 縁の光の枠の太さ（NDC）
      const P = [];
      const col = [];
      const tri = (a, b, c, cc) => { P.push(...a, ...b, ...c); for (let i = 0; i < 3; i++) col.push(...cc); };
      const quad = (a, b, c, e, cc) => { tri(a, b, c, cc); tri(a, c, e, cc); };
      const outer = [[-hw(vTop), vTop], [hw(vTop), vTop], [0, vApex]];
      const ihw = (v) => hw(v) - edge * 1.6, ivT = vTop, ivA = vApex + edge * 2.4;
      const inner = [[-ihw(ivT), ivT], [ihw(ivT), ivT], [0, ivA]];
      const W = (p) => at(p[0], p[1]).toArray();
      const glow = [1.0, 0.90, 0.68], black = [0.008, 0.012, 0.03];
      // 枠（外三角 − 内三角）: 左右の辺の帯と、頂点側の帯（上辺は画面外）
      quad(W(outer[0]), W(inner[0]), W(inner[2]), W(outer[2]), glow);
      quad(W(inner[1]), W(outer[1]), W(outer[2]), W(inner[2]), glow);
      tri(W(inner[0]), W(inner[1]), W(inner[2]), black);
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
      const m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.DoubleSide, depthWrite: false, depthTest: false }));
      m.frustumCulled = false; m.renderOrder = -60;
      root.add(m);
    }

    return {
      object: root,
      update({ t }) { cloud.material.uniforms.uTime.value = t; uTime.value = t; },
      dispose() { qg.dispose(); islandGeom.dispose(); glassGeom.dispose(); ringGeom.dispose(); },
    };
  },
};
