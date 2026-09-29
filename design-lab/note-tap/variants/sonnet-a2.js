// 派生1「キーキャップ+中心グラデ」: sonnet-a の天面に、カプセル中心軸へ向かう柔らかい明るさのグラデを足しただけ。

// ---- スタジアム形（カプセル平面）押し出しソリッドのビルダ ----
// rings: 下→上の断面リング [{ e:内側への縮み量(半奥行bに対する比), y:高さ(hに対する比), nh:法線の水平成分, ny:法線のY成分, c:縁からの距離(0=縁,1=中心, <0=壁扱い) }]
// 最後のリングの内側は中心点（c=1, 法線+Y）へ扇形で塞ぐ。
function buildSolid(THREE, a, b, h, rings, seg) {
  const r = b, cx = Math.max(a - r, 0);
  const dirs = [];
  for (let i = 0; i <= seg; i++) { const p = -Math.PI / 2 + (Math.PI * i) / seg; dirs.push([cx, Math.cos(p), Math.sin(p)]); }
  for (let i = 0; i <= seg; i++) { const p = Math.PI / 2 + (Math.PI * i) / seg; dirs.push([-cx, Math.cos(p), Math.sin(p)]); }
  const P = dirs.length;
  const pos = [], nor = [], cc = [], idx = [];
  for (const rg of rings) {
    const rr = Math.max(r - rg.e * b, 0);
    for (const [ox, dx, dz] of dirs) {
      pos.push(ox + rr * dx, rg.y * h, rr * dz);
      nor.push(rg.nh * dx, rg.ny, rg.nh * dz);
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
  const top = rings[rings.length - 1];
  const ci = pos.length / 3;
  pos.push(0, top.y * h, 0); nor.push(0, 1, 0); cc.push(1);
  const tb = (rings.length - 1) * P;
  for (let i = 0; i < P; i++) idx.push(tb + i, ci, tb + ((i + 1) % P));
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
  g.setAttribute('aC', new THREE.Float32BufferAttribute(cc, 1));
  g.setIndex(idx);
  return g;
}
const VERT = /* glsl */ `
  attribute float aC;
  varying float vC; varying vec3 vN; varying vec3 vV; varying float vZ;
  void main() {
    vC = aC; vZ = position.z;
    vec4 wp = modelMatrix * vec4(position, 1.0);
    vec3 s = vec3(length(modelMatrix[0].xyz), length(modelMatrix[1].xyz), length(modelMatrix[2].xyz));
    vN = normalize(mat3(modelMatrix) * (normal / (s * s)));
    vV = cameraPosition - wp.xyz;
    gl_Position = projectionMatrix * viewMatrix * wp;
  }`;

export default {
  id: 'sonnet-a2',
  name: 'キーキャップ 派生1（天面中心グラデ）',
  model: 'Sonnet 5.5',
  concept: '元のキーキャップ（面取りソリッド+白リング）に、天面の中心軸へ向かって少し明るくなる柔らかいグラデを足しただけの派生。押し面のふくらみ・光沢感が出て、青が平板に見えるのを抑える。形・リング・厚みは元案と同じ。',
  unityCost: '元案と同じ（約110頂点、ZTest/描画順の課題も同じ）。追加は天面のローカルz（頂点位置のvarying）から作る明るさ係数1つだけで、追加の頂点属性なし。',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: new THREE.Color(colors.tap) }, uB: { value: 0.39 } },
      vertexShader: VERT,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor; uniform float uB;
        varying float vC; varying vec3 vN; varying vec3 vV; varying float vZ;
        void main() {
          vec3 N = normalize(vN), V = normalize(vV);
          vec3 L = normalize(vec3(-0.35, 0.85, 0.5));
          float hemi = mix(0.42, 0.78, N.y * 0.5 + 0.5);
          float diff = max(dot(N, L), 0.0) * 0.32;
          vec3 rgb = uColor * (hemi + diff);
          // 面取り・側面のスペキュラ（天面はマット）
          vec3 H = normalize(L + V);
          rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 36.0) * 0.18 * (1.0 - step(0.98, N.y));
          // 側面は少し暗く
          rgb *= mix(0.78, 1.0, smoothstep(0.0, 0.9, N.y));
          // 天面のみ: 軸(z=0)から縁へ向かって暗くなる → 中心が少し明るい
          float top = step(0.98, N.y);
          float k = 1.0 - smoothstep(0.0, 1.0, clamp(abs(vZ) / (uB * 0.86), 0.0, 1.0));
          rgb = mix(rgb, vec3(1.0), 0.20 * k * top);
          if (vC >= 0.0) {
            float fw = max(fwidth(vC), 1e-5);
            float dPx = vC / fw, bPx = 1.0 / fw;
            float w = min(1.5, max(0.0, bPx - 0.75) * 0.5);
            float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w);
            rgb = mix(rgb, vec3(1.0), ol);
          }
          gl_FragColor = vec4(rgb, 1.0);
          #include <colorspace_fragment>
        }`,
      transparent: true, depthWrite: true, depthTest: true,
    });
    const geoCache = new Map();
    return {
      makeNote({ widthCells, wWorld, halfT }) {
        mat.uniforms.uB.value = halfT;
        let g = geoCache.get(widthCells);
        if (!g) {
          // 壁→面取り→天面（天面リングは複製して c=0 から始める）
          const rings = [
            { e: 0.00, y: 0.00, nh: 1.0, ny: 0.0, c: -1 },
            { e: 0.00, y: 0.72, nh: 1.0, ny: 0.0, c: -1 },
            { e: 0.05, y: 0.90, nh: 0.85, ny: 0.55, c: -1 },
            { e: 0.14, y: 1.00, nh: 0.5, ny: 0.87, c: -1 },
            { e: 0.14, y: 1.00, nh: 0.0, ny: 1.0, c: 0.0 },
          ];
          g = buildSolid(THREE, wWorld / 2, halfT, halfT * 1.3, rings, 10);
          geoCache.set(widthCells, g);
        }
        const m = new THREE.Mesh(g, mat);
        m.renderOrder = 10;
        return m;
      },
    };
  },
};
