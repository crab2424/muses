// Opus-B: 低いカプセル柱（押し出し）。天面は現行と同じSDF輪郭、側面は暗い色で厚みを見せる。
// 狙い:
//  - 真上寄りから見下ろすカメラ(θ=38°)なので、手前側の側面が常に帯状に見える。
//    天面（明）＋側面（暗）の2トーンで、画面上の厚みが増え、遠方でも「線」ではなく「粒」に見えやすい。
//  - 空中の半透明面の上では、側面の暗いトーンが背景から浮かせる影の役割を兼ねる。
//  - 凸な柱なので「側面→天面」の順に描けば ZTest Always のままでも破綻しない（Unity移植が素直）。
export default {
  id: 'opus-b',
  name: '低いカプセル柱（2トーン）',
  model: 'Opus 5.5',
  concept: 'カプセル形を低く押し出した柱。天面は現行と同じ白輪郭SDF、手前の側面は暗い青で、見下ろし視点で常に「明るい天面＋暗い側面」の2トーンになる。遠方でも粒として読め、空中面の上では側面が影の役割をする。',
  unityCost: '中。頂点数は1ノーツ約70（現行4）。凸形なので「側面→天面」の描画順（サブメッシュ順）と背面カリングで ZTest Always のまま描ける。天面シェーダは現行流用、側面は単色＋端の暗線のみ。',
  create({ THREE, colors }) {
    const tap = new THREE.Color(colors.tap);
    const topMat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: tap }, uW: { value: 1 }, uT: { value: 1 } },
      vertexShader: /* glsl */ `
        uniform float uW, uT;
        varying vec2 vUv;
        void main() {
          // 押し出しジオメトリの天面はUVを持たないので、ローカル座標から作る
          vUv = vec2(position.x / uW + 0.5, 0.5 - position.z / (2.0 * uT));
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }`,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor;
        varying vec2 vUv;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          vec2 p = vUv - 0.5;
          vec2 duv = vec2(max(fwidth(vUv.x), 1e-5), max(fwidth(vUv.y), 1e-5));
          vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
          float dist = roundedBox(pPx, bPx, min(bPx.x, bPx.y));
          float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.6);
          float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
          float close = clamp((bPx.y - 3.0) / 4.0, 0.0, 1.0);
          vec3 rgb = uColor * (1.0 + 0.10 * (vUv.y * 2.0 - 1.0) * close);
          rgb = mix(rgb, vec3(1.0), outline);
          gl_FragColor = vec4(rgb, 1.0);
          #include <colorspace_fragment>
        }`,
      side: THREE.FrontSide,
    });
    const sideMat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: tap.clone().multiplyScalar(0.42) }, uH: { value: 1 } },
      vertexShader: /* glsl */ `
        varying vec3 vN; varying float vY;
        void main() {
          vN = normalize(mat3(modelMatrix) * normal);
          vY = position.y;
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }`,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor; uniform float uH;
        varying vec3 vN; varying float vY;
        void main() {
          // 端（横向きの面）ほど暗く → 隣接ノーツの継ぎ目に暗い谷ができる
          float facing = clamp(vN.z, 0.0, 1.0);
          vec3 rgb = uColor * mix(0.55, 1.0, facing);
          // 天面との境に細い明線（面の切り替わりを強調）
          float h = vY / uH;
          float dh = max(fwidth(h), 1e-5);
          rgb = mix(rgb, vec3(0.75, 0.88, 1.0), 1.0 - smoothstep(0.0, 1.2 * dh, 1.0 - h));
          gl_FragColor = vec4(rgb, 1.0);
          #include <colorspace_fragment>
        }`,
      side: THREE.FrontSide,
    });
    const cache = new Map();
    return {
      makeNote({ widthCells, wWorld, halfT }) {
        let entry = cache.get(widthCells);
        if (!entry) {
          const r = halfT, hw = wWorld / 2, H = halfT * 1.6;
          const s = new THREE.Shape();
          s.moveTo(-hw + r, -r);
          s.lineTo(hw - r, -r);
          s.absarc(hw - r, 0, r, -Math.PI / 2, Math.PI / 2, false);
          s.lineTo(-hw + r, r);
          s.absarc(-hw + r, 0, r, Math.PI / 2, Math.PI * 1.5, false);
          const g = new THREE.ExtrudeGeometry(s, { depth: H, bevelEnabled: false, curveSegments: 8 });
          // Shape の (x, y) → ローカル (x, z)、押し出し方向 → +y
          g.rotateX(-Math.PI / 2);
          const top = topMat.clone(); top.uniforms.uW.value = wWorld; top.uniforms.uT.value = halfT;
          top.uniforms.uColor = topMat.uniforms.uColor;
          const side = sideMat.clone(); side.uniforms.uH.value = H;
          side.uniforms.uColor = sideMat.uniforms.uColor;
          entry = { g, mats: [top, side] };
          cache.set(widthCells, entry);
        }
        const m = new THREE.Mesh(entry.g, entry.mats);
        m.renderOrder = 10;
        return m;
      },
    };
  },
};
