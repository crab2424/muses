// 案B派生1「ネオンドーム・ソフト芯」: 元案のドームはそのまま、中央の発光芯を帯ではなくなだらかなグラデーションで溶け込ませる。

// ---- スタジアム形（カプセル平面）押し出しソリッドのビルダ ----
// rings: 下→上の断面リング [{ e:内側への縮み量(半奥行bに対する比), y:高さ(hに対する比), nh:法線の水平成分, ny:法線のY成分, c:縁からの距離(0=縁,1=中心) }]
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
  id: 'sonnet-b2',
  name: 'ネオンドーム 派生1（ソフト芯）',
  model: 'Sonnet 5.5',
  concept: '元のネオンドームから、中央の発光芯の立ち上がりを緩やかにした派生。芯が帯として縁取られず、青から淡い水色へ滑らかに明るくなるので、ガラス玉の内側が光っているような柔らかい質感になる。形・リム光・白線は元案のまま。',
  unityCost: '元案と同一（頂点約110・法線とc属性・ZTest有効パス）。変更はフラグメントの芯の smoothstep 範囲と混合率のみで、追加コストなし。',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: {
        uColor: { value: new THREE.Color(colors.tap) },
        uDeep: { value: new THREE.Color('#0d3a8f') },
        uCore: { value: new THREE.Color('#d6f0ff') },
      },
      vertexShader: VERT,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor, uDeep, uCore;
        varying float vC; varying vec3 vN; varying vec3 vV;
        void main() {
          vec3 N = normalize(vN), V = normalize(vV);
          vec3 L = normalize(vec3(-0.4, 0.8, 0.45));
          float ndv = clamp(dot(N, V), 0.0, 1.0);
          float fres = pow(1.0 - ndv, 2.2);
          // 体: 縁ほど深い青、中央ほどタップ色
          float body = smoothstep(0.0, 0.7, vC);
          vec3 rgb = mix(uDeep, uColor, body);
          rgb *= 0.8 + 0.35 * max(dot(N, L), 0.0);
          // 発光芯（中心線に沿う帯）
          float core = smoothstep(0.15, 1.0, vC);
          rgb = mix(rgb, uCore, core * core * 0.8);
          // リム光（縁の輪郭を明るく）
          rgb += vec3(0.55, 0.8, 1.0) * fres * 0.55;
          // ハイライト（ガラスの照り）
          vec3 H = normalize(L + V);
          rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 60.0) * 0.6;
          // 底縁のスクリーン空間白線
          float fw = max(fwidth(vC), 1e-5);
          float dPx = vC / fw, bPx = 1.0 / fw;
          float w = min(1.3, max(0.0, bPx - 0.75) * 0.5);
          float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w);
          rgb = mix(rgb, vec3(0.95, 0.98, 1.0), ol);
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
          // 楕円断面: 角度 al=0(底縁) → 82°(頂点近く)
          const rings = [];
          for (const deg of [0, 22, 45, 66, 82]) {
            const al = (deg * Math.PI) / 180, ca = Math.cos(al), sa = Math.sin(al);
            const nh = 1.5 * ca, ny = sa; // 高さ/半奥行の比で法線を傾ける
            const l = Math.hypot(nh, ny);
            rings.push({ e: 1 - ca, y: sa, nh: nh / l, ny: ny / l, c: 1 - ca });
          }
          g = buildSolid(THREE, wWorld / 2, halfT, halfT * 1.5, rings, 10);
          geoCache.set(widthCells, g);
        }
        const m = new THREE.Mesh(g, mat);
        m.renderOrder = 10;
        return m;
      },
    };
  },
};
