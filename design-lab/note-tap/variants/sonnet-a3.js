// 派生2「キーキャップ+インナーバンド」: 天面をやや深い青にし、白リングのすぐ内側に淡い青の帯を足す。高さは低め(1.1×halfT)。

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
  varying float vC; varying vec3 vN; varying vec3 vV;
  void main() {
    vC = aC;
    vec4 wp = modelMatrix * vec4(position, 1.0);
    vec3 s = vec3(length(modelMatrix[0].xyz), length(modelMatrix[1].xyz), length(modelMatrix[2].xyz));
    vN = normalize(mat3(modelMatrix) * (normal / (s * s)));
    vV = cameraPosition - wp.xyz;
    gl_Position = projectionMatrix * viewMatrix * wp;
  }`;

export default {
  id: 'sonnet-a3',
  name: 'キーキャップ 派生2（インナーバンド・低め）',
  model: 'Sonnet 5.5',
  concept: '元のキーキャップの高さを1.3→1.1×halfTに下げ、天面をやや深い青にして、白リング内側に淡い青の細帯を足した派生。リング+帯の二重縁で「はめ込まれたキー」感を出しつつ、遠方では帯が消えて元案と同じ線に戻る。',
  unityCost: '元案と同じ頂点数（約110）。追加は fwidth(c) から作る帯1本の計算のみ。高さが低い分、手前の壁の見え幅が減り描画順の破綻はやや目立ちにくい。',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: new THREE.Color(colors.tap) } },
      vertexShader: VERT,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor;
        varying float vC; varying vec3 vN; varying vec3 vV;
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
          if (vC >= 0.0) {
            float fw = max(fwidth(vC), 1e-5);
            float dPx = vC / fw, bPx = 1.0 / fw;
            // 天面はやや深く、リング内側に淡い青の帯（リングから 2〜5px 内側。細い遠方では消える）
            rgb *= 0.92;
            float band = smoothstep(2.0, 3.0, dPx) * (1.0 - smoothstep(4.5, 6.0, dPx)) * smoothstep(4.0, 8.0, bPx);
            rgb = mix(rgb, mix(uColor, vec3(1.0), 0.55), 0.6 * band);
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
          g = buildSolid(THREE, wWorld / 2, halfT, halfT * 1.1, rings, 10);
          geoCache.set(widthCells, g);
        }
        const m = new THREE.Mesh(g, mat);
        m.renderOrder = 10;
        return m;
      },
    };
  },
};
