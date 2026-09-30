// 画面上で一定幅の線（細い帯メッシュ）。WebGL の LineSegments（1px固定）は Unity に無いので、
// 線分ごとに4頂点の帯を積み、頂点シェーダで画面上の法線方向へ広げる。Unity でも同じメッシュ＋同じ頂点シェーダで移植できる。
//
// 幅は「iPad 11"（縦1668px）での px」で指定する（= 解像度・タイル表示の大きさに依らず同じ見え方）。
// ラボの拡大表示では uBaseToClip の倍率で一緒に拡大される。
//
//   buildLines(THREE, ctx, segs, opts) → THREE.Mesh（1 draw call）
//   segs: [{ a:[x,y,z], b:[x,y,z], c:[r,g,b], k?:強さ(1), p?:任意の値(0) }]
//   opts: { widthPx=1.5, opacity=0.6, uniforms={}, frag='' }
//     frag … フラグメントに差し込む GLSL。使える変数: vec3 col（加算前の色）, float a（不透明度）,
//            float vP（線分ごとの p を線に沿って補間）, float vU（線分の始点0→終点1）, vec3 vW（ワールド座標）
//            uniform は opts.uniforms で足す（uTime は常にある）。
// 暗部ゾーン内は自動で消える（museSpawnMask）。加算合成・深度なし。
export const REF_HEIGHT_PX = 1668;

export function buildLines(THREE, ctx, segs, opts = {}) {
  const { widthPx = 1.5, opacity = 0.6, uniforms = {}, frag = '' } = opts;
  const n = segs.length;
  const A = new Float32Array(n * 4 * 3), B = new Float32Array(n * 4 * 3), C = new Float32Array(n * 4 * 3);
  const S = new Float32Array(n * 4 * 2), P = new Float32Array(n * 4);
  const idx = new Uint32Array(n * 6);
  segs.forEach((s, i) => {
    const k = s.k ?? 1, p = s.p ?? 0;
    for (let j = 0; j < 4; j++) {
      const v = i * 4 + j;
      A.set(s.a, v * 3); B.set(s.b, v * 3);
      C.set([s.c[0] * k, s.c[1] * k, s.c[2] * k], v * 3);
      S[v * 2] = j < 2 ? 0 : 1;          // 始点0 / 終点1
      S[v * 2 + 1] = j % 2 ? 1 : -1;     // 帯の左右
      P[v] = p;
    }
    idx.set([i * 4, i * 4 + 1, i * 4 + 3, i * 4, i * 4 + 3, i * 4 + 2], i * 6);
  });
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(A, 3)); // bounding 用（実位置はシェーダで決める）
  g.setAttribute('aB', new THREE.BufferAttribute(B, 3));
  g.setAttribute('color', new THREE.BufferAttribute(C, 3));
  g.setAttribute('aS', new THREE.BufferAttribute(S, 2));
  g.setAttribute('aP', new THREE.BufferAttribute(P, 1));
  g.setIndex(new THREE.BufferAttribute(idx, 1));
  const mat = new THREE.ShaderMaterial({
    uniforms: {
      ...ctx.zone.uniforms, uTime: { value: 0 }, uOpacity: { value: opacity },
      uWidth: { value: (widthPx * 2) / REF_HEIGHT_PX }, uAspect: { value: ctx.aspect }, ...uniforms,
    },
    vertexShader: /* glsl */ `
      uniform vec4 uBaseToClip; uniform float uWidth, uAspect;
      attribute vec3 aB; attribute vec2 aS; attribute float aP;
      varying vec3 vCol; varying float vP, vU, vSide; varying vec3 vW;
      void main() {
        vec4 ca = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        vec4 cb = projectionMatrix * modelViewMatrix * vec4(aB, 1.0);
        // 画面空間（縦を1とする等方座標）での線の向き。拡大表示の倍率は後で掛ける
        vec2 na = ca.xy / ca.w, nb = cb.xy / cb.w;
        vec2 dir = (nb - na) * vec2(uAspect, 1.0);
        dir = length(dir) > 1e-6 ? normalize(dir) : vec2(1.0, 0.0);
        vec2 nrm = vec2(-dir.y, dir.x);
        vec4 c = aS.x < 0.5 ? ca : cb;
        // 帯の幅（+1px 相当の余白を AA 用に足す）。clip 空間なので w を掛ける
        float w = uWidth * 0.5 + 1.0 / 1668.0;
        vec2 off = nrm * aS.y * w * vec2(1.0 / uAspect, 1.0);
        c.xy += off * uBaseToClip.xy * c.w;
        gl_Position = c;
        vCol = color; vP = aP; vU = aS.x; vSide = aS.y;
        vW = (modelMatrix * vec4(aS.x < 0.5 ? position : aB, 1.0)).xyz;
      }`,
    fragmentShader: /* glsl */ `
      ${ctx.glsl.spawn}
      uniform float uTime, uOpacity, uWidth;
      ${Object.keys(uniforms).map((k) => `uniform ${glslType(uniforms[k].value)} ${k};`).join('\n')}
      varying vec3 vCol; varying float vP, vU, vSide; varying vec3 vW;
      void main() {
        vec3 col = vCol;
        float a = uOpacity;
        // 帯の縁を柔らかく（中心1 → 外周0）
        float edge = uWidth * 0.5 / (uWidth * 0.5 + 1.0 / 1668.0);
        a *= 1.0 - smoothstep(edge * 0.6, 1.0, abs(vSide));
        ${frag}
        a *= 1.0 - museSpawnMask(museBaseNdc());
        gl_FragColor = vec4(col * a, a);
      }`,
    vertexColors: true,
    transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false,
  });
  const mesh = new THREE.Mesh(g, mat);
  mesh.frustumCulled = false;
  return mesh;
}

function glslType(v) {
  if (typeof v === 'number') return 'float';
  if (v && v.isVector2) return 'vec2';
  if (v && v.isVector3) return 'vec3';
  if (v && v.isVector4) return 'vec4';
  if (v && v.isColor) return 'vec3';
  throw new Error('buildLines: unsupported uniform type');
}

// 線分を貯める小道具（箱・多角形・円）
export function lineKit() {
  const segs = [];
  const seg = (a, b, c, k = 1, p = 0) => segs.push({ a, b, c, k, p });
  const box = (cx, cy, cz, w, h, d, c, k = 1, p = 0) => {
    const x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2, z0 = cz - d / 2, z1 = cz + d / 2;
    const V = [[x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0], [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]];
    [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]]
      .forEach(([i, j]) => seg(V[i], V[j], c, k, p));
  };
  // 軸 axis('x'|'y'|'z') まわりの円（中心 o、半径 r、分割 n）
  const circle = (o, r, n, axis, c, k = 1, p = 0) => {
    for (let i = 0; i < n; i++) {
      const a0 = (i / n) * Math.PI * 2, a1 = ((i + 1) / n) * Math.PI * 2;
      const pt = (a) => {
        const u = Math.cos(a) * r, v = Math.sin(a) * r;
        return axis === 'y' ? [o[0] + u, o[1], o[2] + v] : axis === 'x' ? [o[0], o[1] + u, o[2] + v] : [o[0] + u, o[1] + v, o[2]];
      };
      seg(pt(a0), pt(a1), c, k, p);
    }
  };
  return { segs, seg, box, circle };
}
