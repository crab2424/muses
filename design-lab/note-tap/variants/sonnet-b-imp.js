// 案Bインポスター版: ネオンドームと同じ見た目を「平らな1枚板」だけで出す。
// フラグメントでカメラからのレイを楕円断面ドーム（カプセル平面の押し出し）へ解析的に当て、法線・高さ・芯・リムを再構成する。
const HR = 1.5; // 高さ/半奥行の比（sonnet-b と同じ）
const EXT_NEAR = 0.15; // 手前側へ伸ばす量（奥側は頂点シェーダーでカメラ位置から算出）

// sonnet-b の断面リング: 縁からの距離比 d/b・頂点法線（水平成分, Y成分）。最後は頂点中心（法線+Y）
const RINGS = [0, 22, 45, 66, 82].map((deg) => {
  const al = (deg * Math.PI) / 180, ca = Math.cos(al), sa = Math.sin(al);
  const nh = HR * ca, l = Math.hypot(nh, sa);
  return { d: ca, nh: nh / l, ny: sa / l };
}).concat([{ d: 0, nh: 0, ny: 1 }]);
const VERT = /* glsl */ `
  uniform float uB, uH;
  varying vec2 vQ; varying vec3 vRo; varying vec3 vS;
  void main() {
    vec3 s = vec3(length(modelMatrix[0].xyz), length(modelMatrix[1].xyz), length(modelMatrix[2].xyz));
    vS = s;
    // ローカル空間でのカメラ位置（ノーツは平行移動+スケールのみ）
    vRo = (cameraPosition - modelMatrix[3].xyz) / s;
    vec3 pos = position;
    // 奥側の頂点は、ドーム頂点（z=0, y=h）を視線方向に y=0 面へ落とした位置まで伸ばす（画面に出るドームを板が覆うため）
    if (pos.z < 0.0) {
      float ext = max(vRo.z, 0.0) * uH / max(vRo.y - uH, 0.05 * uB);
      pos.z = -min(max(ext * 1.08 + 0.1 * uB, uB * 1.05), uB * MAX_EXT);
      // 端のキャップも同様に、高い位置ほど視線方向に外へ投影されるので横にも広げる
      pos.x = vRo.x + (pos.x - vRo.x) * (1.0 + 1.1 * uH / max(vRo.y - uH, 0.05 * uB));
    }
    vQ = pos.xz;
    gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(pos, 1.0);
  }`.replace('MAX_EXT', '6.0');

const FRAG = /* glsl */ `
  uniform vec3 uColor, uDeep, uCore;
  uniform float uA, uB, uH; // 半長 / 半奥行 / 高さ
  varying vec2 vQ; varying vec3 vRo; varying vec3 vS;

  // レイ vs 楕円体(中心c, 半径r)。最初の交点 t を返す（なければ -1）
  float hitEllipsoid(vec3 ro, vec3 rd, vec3 c, vec3 r) {
    vec3 o = (ro - c) / r, d = rd / r;
    float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
    float D = B * B - A * C;
    if (D < 0.0) return -1.0;
    return (-B - sqrt(D)) / A;
  }

  void main() {
    float cx = max(uA - uB, 0.0);
    vec3 ro = vRo;
    vec3 q = vec3(vQ.x, 0.0, vQ.y);
    vec3 rd = normalize(q - ro);

    // 中央の楕円柱（z,y のみ）
    float tBest = 1e9;
    {
      vec2 o = vec2(ro.z / uB, ro.y / uH), d = vec2(rd.z / uB, rd.y / uH);
      float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
      float D = B * B - A * C;
      if (D >= 0.0) {
        float t = (-B - sqrt(D)) / A;
        vec3 p = ro + rd * t;
        if (abs(p.x) <= cx && p.y >= 0.0 && t > 0.0) tBest = t;
      }
    }
    // 両端の楕円体キャップ
    for (int i = 0; i < 2; i++) {
      float sg = i == 0 ? 1.0 : -1.0;
      float t = hitEllipsoid(ro, rd, vec3(sg * cx, 0.0, 0.0), vec3(uB, uH, uB));
      if (t > 0.0) {
        vec3 p = ro + rd * t;
        if (p.x * sg >= cx && p.y >= 0.0 && t < tBest) tBest = t;
      }
    }
    bool hit = tBest < 1e8;
    vec3 p = ro + rd * (hit ? tBest : 0.0);

    // 縁からの距離 c（0=縁, 1=中心）と法線
    float ex = p.x - clamp(p.x, -cx, cx);
    vec2 hz = vec2(ex, p.z);
    float dist = length(hz);
    float vC = hit ? clamp(1.0 - dist / uB, 0.0, 1.0) : 0.0;
    vec2 hdir = dist > 1e-5 ? hz / dist : vec2(1.0, 0.0);
    // sonnet-b のリング分割(0/22/45/66/82度+頂点)の補間法線に合わせ、縁距離 d/b の区分線形で法線を求める
    float dn = clamp(dist / uB, 0.0, 1.0);
    const float RD[6] = float[6](RING_D);
    const float RH[6] = float[6](RING_NH);
    const float RY[6] = float[6](RING_NY);
    float nh = RH[5], ny = RY[5];
    for (int k = 0; k < 5; k++) {
      if (dn <= RD[k] && dn >= RD[k + 1]) {
        float f = (dn - RD[k + 1]) / (RD[k] - RD[k + 1]);
        nh = mix(RH[k + 1], RH[k], f); ny = mix(RY[k + 1], RY[k], f);
      }
    }
    vec3 nl = normalize(vec3(nh * hdir.x, ny, nh * hdir.y));
    vec3 N = normalize(nl / (vS * vS));
    vec3 V = normalize((ro - p) * vS);

    vec3 L = normalize(vec3(-0.4, 0.8, 0.45));
    float ndv = clamp(dot(N, V), 0.0, 1.0);
    float fres = pow(1.0 - ndv, 2.2);
    float body = smoothstep(0.0, 0.7, vC);
    vec3 rgb = mix(uDeep, uColor, body);
    rgb *= 0.8 + 0.35 * max(dot(N, L), 0.0);
    float core = smoothstep(0.62, 1.0, vC);
    rgb = mix(rgb, uCore, core * 0.85);
    rgb += vec3(0.55, 0.8, 1.0) * fres * 0.55;
    vec3 H = normalize(L + V);
    rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 60.0) * 0.6;
    // 底縁のスクリーン空間白線（sonnet-b と同式）
    float fw = max(fwidth(vC), 1e-5);
    float dPx = vC / fw, bPx = 1.0 / fw;
    float w = min(1.3, max(0.0, bPx - 0.75) * 0.5);
    float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w);
    rgb = mix(rgb, vec3(0.95, 0.98, 1.0), ol);
    if (!hit) discard;
    gl_FragColor = vec4(rgb, 1.0);
    #include <colorspace_fragment>
  }`
  .replace('RING_D', RINGS.map((r) => r.d.toFixed(5)).join(','))
  .replace('RING_NH', RINGS.map((r) => r.nh.toFixed(5)).join(','))
  .replace('RING_NY', RINGS.map((r) => r.ny.toFixed(5)).join(','));

export default {
  id: 'sonnet-b-imp',
  name: 'ネオンドーム インポスター（平板1枚）',
  model: 'Sonnet 5.5',
  concept: 'ネオンドームと同じ見た目を、y=0 の平らな4頂点の板1枚だけで再現するインポスター版。フラグメントでカメラからのレイを楕円断面ドームに解析的に当て、高さ・法線・芯・リム・ハイライト・白線を再構成する。立体版と並べて「板でも見分けがつかないか」を確かめるための比較用。',
  unityCost: '頂点は1ノーツ4個（現行と同じ）で、全ノーツ1メッシュ・ZWrite Off / ZTest Always・半透明のまま移植できる（自己遮蔽・描画順の問題なし）。代わりにフラグメントでレイ vs 楕円柱+楕円体2個の二次方程式を解く分だけ重く、板は奥側へ少し伸ばすので密な連打では画素が重なる。ノーツはワールドで平行移動+スケールのみの前提でカメラをローカル化している。輪郭は discard のため MSAA 頼み（Unity 側は要確認）。',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: {
        uColor: { value: new THREE.Color(colors.tap) },
        uDeep: { value: new THREE.Color('#0d3a8f') },
        uCore: { value: new THREE.Color('#d6f0ff') },
        uA: { value: 1 }, uB: { value: 1 }, uH: { value: 1 },
      },
      vertexShader: VERT,
      fragmentShader: FRAG,
      transparent: true, depthWrite: false, depthTest: false, side: THREE.DoubleSide,
    });
    const cache = new Map(); // widthCells -> { g, m }
    return {
      makeNote({ widthCells, wWorld, halfT }) {
        let e = cache.get(widthCells);
        if (!e) {
          const a = wWorld / 2, b = halfT;
          const z0 = -b, z1 = b * (1 + EXT_NEAR);
          const g = new THREE.BufferGeometry();
          g.setAttribute('position', new THREE.Float32BufferAttribute([-a, 0, z0, a, 0, z0, a, 0, z1, -a, 0, z1], 3));
          g.setIndex([0, 2, 1, 0, 3, 2]);
          // 幅ごとに uniform が違うのでマテリアルを複製（シェーダープログラムは共有される）
          const m = mat.clone();
          m.uniforms = { uColor: mat.uniforms.uColor, uDeep: mat.uniforms.uDeep, uCore: mat.uniforms.uCore,
            uA: { value: a }, uB: { value: b }, uH: { value: b * HR } };
          e = { g, m };
          cache.set(widthCells, e);
        }
        const mesh = new THREE.Mesh(e.g, e.m);
        mesh.renderOrder = 10;
        mesh.frustumCulled = false;
        return mesh;
      },
    };
  },
};
