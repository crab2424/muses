// 天上の聖域: 白・淡い空色。雲海（全画面板1枚の光線追跡シェーダー）、浮かぶ大理石の破片と柱の残骸（Instanced）、
// 光の輪（Instanced）、光芒（加算の板をまとめた1メッシュ）、消失点の黒いモノリス（逆三角＋縁の細い光の枠、1メッシュ）。
// 暗部は「消失点のモノリス＋その周囲の暗い霞」で作る。物体はゾーン内で霞に沈める。
export default {
  id: 'sanctuary', name: '天上の聖域', model: 'Sonnet 5.5',
  concept: '白い雲海の上に浮かぶ大理石の破片・柱の残骸・光の輪。奥（消失点）だけに黒い逆三角のモノリスが縁を光らせて抜ける。',
  palette: '雲 #f4f8ff〜#c6d8f0 / 空 #bcd6f4 / 影 #6f86b0 / 光の輪 #fff4d6 / モノリス #020308＋縁 #cfe6ff / ステージ脇は青灰に沈める',
  motion: '雲は実時間でゆっくり右へ流れる（ノイズのスクロール）。破片・柱は周期10〜20秒の上下と自転、光の輪は微小な揺れ、光芒は明るさが±10%ゆらぐだけ。点滅なし。',
  perf: 'draw 7（雲海の全画面板1 / 破片1 / 柱1 / 光の輪1 / 光芒1 / モノリス1 / 共通暗幕1）。粒子なし。雲は4オクターブ。動きは頂点シェーダー（CPU更新なし）。',
  unityCost: '中〜低。雲海シェーダー1枚（カメラ行列から光線→平面交差）と、Instanced 3メッシュ＋加算板＋モノリスのメッシュ。全部 Unlit。テクスチャ不要。',
  clearColor: '#8fb0d8',
  shade: { color: '#0a1020', strength: 0.2 },
  stage: {
    groundFill: '#232b44', groundFillAlpha: 1, groundLine: '#e2ecff', groundLineAlpha: 0.38, groundJudge: '#ffffff',
    skyFill: '#3b4b74', skyFillAlpha: 0.22, skyLine: '#e2ecff', skyLineAlpha: 0.34, skyJudge: '#ffffff',
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
          vec3 haze = vec3(0.74, 0.85, 0.97);
          vec3 c;
          if (dir.y > -0.02) {
            c = mix(haze, vec3(0.55, 0.72, 0.93), clamp(dir.y * 6.0 + 0.2, 0.0, 1.0));
          } else {
            float t = (${CLOUD_Y.toFixed(1)} - ${CAM_Y.toFixed(1)}) / dir.y;
            vec2 wp = dir.xz * t;                        // 雲面のワールド xz（カメラ x=z=0）
            vec2 q = wp * 0.028 + vec2(uTime * 0.010, uTime * 0.004);
            float n = fbm(q + fbm(q * 0.6 + 3.1) * 0.9);
            float dens = smoothstep(0.38, 0.68, n);
            // 上面の陰影: 光は奥上から。少し先の密度との差で凹凸
            float n2 = fbm(q + vec2(0.0, -0.05) + fbm(q * 0.6 + 3.1) * 0.9);
            float lit = clamp(0.5 + (n - n2) * 14.0, 0.0, 1.0);
            vec3 cWhite = vec3(0.97, 0.985, 1.0), cShade = vec3(0.52, 0.63, 0.82), cGap = vec3(0.38, 0.55, 0.82);
            c = mix(cGap, mix(cShade, cWhite, lit * 0.75 + 0.25), dens);
            float fade = 1.0 - exp(-t * 0.007);             // 遠方は霞へ
            c = mix(c, haze, clamp(fade * 1.15, 0.0, 1.0));
            // ステージの左右すぐ脇を青灰に沈める（地上高さ y=0 での横距離）
            float t0 = -${CAM_Y.toFixed(1)} / dir.y;
            vec2 p0 = dir.xz * t0;
            float hw0 = uStageHalf.x + uStageHalf.y * (-p0.y);
            float dx = max(abs(p0.x) - hw0, 0.0);
            float sink = exp(-dx / (3.5 + 0.07 * t0));
            c = mix(c, vec3(0.30, 0.38, 0.56), 0.78 * sink);
          }
          // 消失点まわりを暗い霞に（モノリスが立つ奥）
          vec2 e = (ndc - vec2(0.0, 0.93)) / vec2(0.42, 0.34);
          float hole = 1.0 - smoothstep(0.0, 1.0, length(e));
          c = mix(c, vec3(0.05, 0.075, 0.13), 0.55 * hole);
          c *= 1.0 - 0.4 * museSpawnMask(ndc);
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    }));
    cloud.frustumCulled = false; cloud.renderOrder = -100;
    root.add(cloud);

    // ---------- 共通: 実時間ユニフォーム ----------
    const uTime = { value: 0 };

    // ---------- 2) 破片・柱・光の輪（Instanced、頂点シェーダーで浮遊） ----------
    // aSeed: x=位相 y=周期係数 z=自転係数 w=色の明るさ
    const vs = /* glsl */ `
      attribute vec4 aSeed; uniform float uTime;
      varying vec3 vN; varying float vDist; varying float vB; varying float vHeight;
      void main() {
        float ph = aSeed.x * 6.2831 + uTime * (0.35 + aSeed.y * 0.25);
        float a = uTime * 0.10 * aSeed.z + aSeed.x * 6.2831;
        float ca = cos(a), sa = sin(a);
        vec3 p = position;
        vec3 n = normal;
        p = vec3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z);
        n = vec3(ca * n.x + sa * n.z, n.y, -sa * n.x + ca * n.z);
        vec4 wp = modelMatrix * instanceMatrix * vec4(p, 1.0);
        wp.y += sin(ph) * 0.7;
        wp.x += cos(ph * 0.7) * 0.3;
        vN = normalize(mat3(modelMatrix * instanceMatrix) * n);
        vDist = length(wp.xyz - cameraPosition);
        vB = aSeed.w; vHeight = position.y;
        gl_Position = projectionMatrix * viewMatrix * wp;
      }`;
    const mkMat = (fragBody) => new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime },
      vertexShader: vs,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        varying vec3 vN; varying float vDist; varying float vB; varying float vHeight;
        void main() {
          ${fragBody}
          c *= 1.0 - 0.9 * museSpawnMask(museBaseNdc());
          gl_FragColor = vec4(c, al);
          #include <colorspace_fragment>
        }`,
    });
    const marbleFrag = /* glsl */ `
      vec3 n = normalize(vN);
      float l = clamp(dot(n, normalize(vec3(-0.3, 0.8, -0.5))) * 0.5 + 0.5, 0.0, 1.0);
      vec3 c = mix(vec3(0.36, 0.46, 0.68), vec3(1.0, 0.99, 0.96), l) * (0.88 + 0.12 * vB);
      c = mix(c, vec3(0.74, 0.85, 0.97), clamp(1.0 - exp(-vDist * 0.010), 0.0, 0.85));
      float al = 1.0;`;
    const ringFrag = /* glsl */ `
      vec3 c = mix(vec3(1.0, 0.95, 0.82), vec3(0.85, 0.93, 1.0), clamp(vDist * 0.008, 0.0, 1.0)) * 0.95;
      float al = 0.85;`;

    // 決定的な擬似乱数（実行ごとに同じ配置）
    let rs = 12345; const rnd = () => (rs = (rs * 1664525 + 1013904223) >>> 0) / 4294967296;
    const m4 = new THREE.Matrix4(), qt = new THREE.Quaternion(), eu = new THREE.Euler(), v3 = new THREE.Vector3(), sc = new THREE.Vector3();
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
    // 配置: 左右対称寄りに、ステージの脇・眼下に。x は符号で振り分け、z は奥へ。
    const shardItems = [];
    for (let i = 0; i < 30; i++) {
      const side = i % 2 ? 1 : -1;
      const z = -(8 + rnd() * 62);
      const x = side * (12 + rnd() * 16 + (-z) * 0.30);
      const s = 0.8 + rnd() * 2.6;
      shardItems.push({ x, y: -6 + rnd() * 9, z, sx: s * (0.7 + rnd() * 0.8), sy: s * (0.5 + rnd() * 0.6), sz: s * (0.7 + rnd() * 0.8),
        rx: rnd() * 3, ry: rnd() * 3, rz: rnd() * 3 });
    }
    const shardGeom = new THREE.OctahedronGeometry(1, 0);
    root.add(inst(shardGeom, mkMat(marbleFrag), shardItems));

    const colItems = [];
    for (let i = 0; i < 12; i++) {
      const side = i % 2 ? 1 : -1;
      const z = -(14 + rnd() * 52);
      const h = 6 + rnd() * 9;
      colItems.push({ x: side * (16 + rnd() * 14 + (-z) * 0.34), y: CLOUD_Y + h * 0.5 + 1 + rnd() * 4, z,
        sx: 1.1 + rnd() * 0.6, sy: h, sz: 1.1 + rnd() * 0.6, rx: (rnd() - 0.5) * 0.25, rz: (rnd() - 0.5) * 0.25 });
    }
    const colGeom = new THREE.CylinderGeometry(0.85, 1.0, 1, 10, 1);
    root.add(inst(colGeom, mkMat(marbleFrag), colItems));

    const ringItems = [];
    for (let i = 0; i < 6; i++) {
      const side = i % 2 ? 1 : -1;
      const z = -(20 + (i >> 1) * 17 + rnd() * 6);
      const r = 5 + (i >> 1) * 2.5 + rnd() * 2;
      ringItems.push({ x: side * (22 + (i >> 1) * 8 + (-z) * 0.30), y: -2 + rnd() * 5, z, sx: r, sy: r, sz: r,
        ry: side * (0.7 + rnd() * 0.4), rx: (rnd() - 0.5) * 0.4 });
    }
    const ringGeom = new THREE.TorusGeometry(1, 0.018, 6, 96);
    const ringMat = mkMat(ringFrag); ringMat.transparent = true; ringMat.depthWrite = false;
    root.add(inst(ringGeom, ringMat, ringItems));

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
            float a = across * across * along * 0.14 * breathe;
            a *= 1.0 - museSpawnMask(museBaseNdc());
            gl_FragColor = vec4(vec3(1.0, 0.97, 0.88) * a, a);
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
      const glow = [0.82, 0.92, 1.0], black = [0.008, 0.012, 0.03];
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
      dispose() { qg.dispose(); shardGeom.dispose(); colGeom.dispose(); ringGeom.dispose(); },
    };
  },
};
