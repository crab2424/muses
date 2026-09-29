// インポスター版「キーキャップ」: 1ノーツ=フラット1クアッド(4頂点, y=0面)。
// フラグメントでカプセルSDFの積層（壁→面取り2段→天面）から高さ/法線を再構成し、sonnet-a.js と同じ固定ライトで陰影をつける。
// 天面が持ち上がって見える視差・透視拡大は、カメラ→層の高さの投影拡大率 t で層ごとにSDF座標を写像して再現する（手前壁は帯として残る）。

const VERT = /* glsl */ `
  attribute vec2 aCB;            // x: 直線部半長/b, y: b(=halfT, ローカル)
  varying vec2 vP;               // ローカルxz / b（y=0面上の点）
  varying vec2 vCl;              // カメラ水平位置（ローカル xz / b）
  varying vec3 vT;               // 高さ 0.72/0.90/1.00 の各層の透視拡大率
  varying float vCx;             // 直線部半長 / b
  varying vec3 vV;
  uniform float uHf;             // 高さ係数(元案 1.3)
  void main() {
    float b = aCB.y;
    vec4 wc = modelMatrix * vec4(0.0, 0.0, 0.0, 1.0);
    float sx = length(modelMatrix[0].xyz), sz = length(modelMatrix[2].xyz);
    float H = uHf * b * length(modelMatrix[1].xyz);          // 天面のワールド高さ
    float dy = cameraPosition.y - wc.y;
    // 高さ y の点をカメラから y=0 面へ投影したときの水平拡大率 t = dy/(dy-y)
    vT = vec3(dy / max(dy - 0.72 * H, 1e-3), dy / max(dy - 0.90 * H, 1e-3), dy / max(dy - H, 1e-3));
    vec2 cl = vec2((cameraPosition.x - wc.x) / sx, (cameraPosition.z - wc.z) / sz); // ローカル単位
    float t1 = vT.z;
    // 天面(e=0)を y=0 面へ投影した範囲までクアッドを広げる
    float gx = cl.x + (sign(position.x) * abs(position.x) - cl.x) * t1;
    float ax = sign(position.x) > 0.0 ? max(position.x, gx) : min(position.x, gx);
    float gz = cl.y + (-b - cl.y) * t1;
    float zFar = max(min(-b, gz), -3.6 * b);                 // 奥へ伸ばす量の上限（隣接ノーツへ食い込ませない）
    float m = 0.08 * b;                                      // AA用の余白
    vec3 lp = vec3(ax + sign(position.x) * m, 0.0, position.z < 0.0 ? zFar - m : b + m);
    vP = vec2(lp.x, lp.z) / b;
    vCl = cl / b;
    vCx = aCB.x;
    vec4 wp = modelMatrix * vec4(lp, 1.0);
    vV = cameraPosition - wp.xyz;
    gl_Position = projectionMatrix * viewMatrix * wp;
  }`;

const FRAG = /* glsl */ `
  uniform vec3 uColor;
  varying vec2 vP; varying vec2 vCl; varying vec3 vT; varying vec3 vV;
  varying float vCx;
  // カプセル(直線部半長cx, 半径1-e, 原点中心)の内側距離。dir=外向き水平法線(x,z)
  float cap(vec2 q, float cx, float e, out vec2 dir) {
    vec2 r = vec2(max(abs(q.x) - cx, 0.0) * sign(q.x), q.y);
    float l = length(r);
    dir = l > 1e-4 ? r / l : vec2(0.0, 1.0);
    return (1.0 - e) - l;
  }
  void main() {
    float cx = vCx; // 直線部半長は幅ごとに違うので頂点側から受ける
    vec2 q = vP;
    // 各層の高さから見た点 = カメラ→y=0面上の点 のレイが層の高さで交わる位置（水平に 1/t 縮む）
    vec2 d0, d1, d2, dT;
    float s0 = cap(q, cx, 0.00, d0);
    float s1 = cap(vCl + (q - vCl) / vT.x, cx, 0.00, d1);
    float s2 = cap(vCl + (q - vCl) / vT.y, cx, 0.05, d2);
    float sT = cap(vCl + (q - vCl) / vT.z, cx, 0.14, dT);
    float sU = max(max(s0, s1), max(s2, sT));
    float fwU = max(fwidth(sU), 1e-5);
    float alpha = clamp(sU / fwU + 0.5, 0.0, 1.0);
    if (alpha <= 0.003) discard;
    float fwT = max(fwidth(sT), 1e-5);

    // 積層の上から判定して、見えている面の法線を決める（sonnet-a のリング表と同じ値）
    vec3 N; float onTop = 0.0;
    if (sT > 0.0) {
      N = vec3(0.0, 1.0, 0.0); onTop = 1.0;
    } else if (s2 > 0.0) {
      float t = s2 / (s2 - sT);
      vec2 nn = mix(vec2(0.85, 0.55), vec2(0.5, 0.87), t);
      N = vec3(nn.x * d2.x, nn.y, nn.x * d2.y);
    } else if (s1 > 0.0) {
      float t = s1 / (s1 - s2);
      vec2 nn = mix(vec2(1.0, 0.0), vec2(0.85, 0.55), t);
      N = vec3(nn.x * d1.x, nn.y, nn.x * d1.y);
    } else {
      N = vec3(d0.x, 0.0, d0.y); // 手前の壁
    }
    N = normalize(N);
    vec3 V = normalize(vV);
    vec3 L = normalize(vec3(-0.35, 0.85, 0.5));
    float hemi = mix(0.42, 0.78, N.y * 0.5 + 0.5);
    float diff = max(dot(N, L), 0.0) * 0.32;
    vec3 rgb = uColor * (hemi + diff);
    vec3 H = normalize(L + V);
    rgb += vec3(1.0) * pow(max(dot(N, H), 0.0), 36.0) * 0.18 * (1.0 - step(0.98, N.y));
    rgb *= mix(0.78, 1.0, smoothstep(0.0, 0.9, N.y));
    if (onTop > 0.5) {
      // 天面縁のスクリーン空間 1.5px の白リング（sonnet-a と同じ式。c → sT/(1-e) 相当）
      float dPx = sT / fwT, bPx = (1.0 - 0.14) / fwT;
      float w = min(1.5, max(0.0, bPx - 0.75) * 0.5);
      float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w);
      rgb = mix(rgb, vec3(1.0), ol);
    }
    gl_FragColor = vec4(rgb, alpha);
    #include <colorspace_fragment>
  }`;

export default {
  id: 'sonnet-a-imp',
  name: 'キーキャップ インポスター版（フラット1クアッド）',
  model: 'Sonnet 5.5',
  concept: '元のキーキャップ（面取りソリッド）を、4頂点のフラット1クアッドのフラグメントシェーダだけで再現するインポスター。カプセルSDFの積層（壁・面取り2段・天面）から法線を作って同じ固定ライトで陰影をつけ、天面が持ち上がって見える視差・透視拡大は、カメラ位置から各層の投影拡大率を求めて層ごとにSDFをずらす/拡大して再現し、クアッドは天面の投影範囲ぶん奥/横へ伸ばして手前の壁帯として描く。実体ソリッドと並べて見分けがつくかを見るための版。',
  unityCost: '現行と同じ4頂点/ノーツ・半透明・ZWrite Off/ZTest Always・1メッシュ結合のまま入る（ソリッド版の自己遮蔽/描画順問題が消える）。代償はフラグメントが重くなること（カプセルSDF×4段+ライティング）と、クアッドが奥へ最大3.6×halfT伸びて画素数が増えること。頂点シェーダでカメラ位置とスケールから押し出し量を計算するので、Unityではカメラ位置と各ノーツのmodelスケールを渡せる前提が必要。',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: new THREE.Color(colors.tap) }, uHf: { value: 1.3 } },
      vertexShader: VERT,
      fragmentShader: FRAG,
      transparent: true, depthWrite: false, depthTest: false, side: THREE.DoubleSide,
    });
    const geoCache = new Map();
    return {
      makeNote({ widthCells, wWorld, halfT }) {
        let g = geoCache.get(widthCells);
        if (!g) {
          const a = wWorld / 2, b = halfT;
          g = new THREE.BufferGeometry();
          // 4頂点: (±a, 0, ∓b)。z<0 側が奥。実際の位置は頂点シェーダで余白と奥方向押し出しを付けて決める
          g.setAttribute('position', new THREE.Float32BufferAttribute([-a, 0, -b, a, 0, -b, -a, 0, b, a, 0, b], 3));
          const cb = [Math.max(a - b, 0) / b, b];
          g.setAttribute('aCB', new THREE.Float32BufferAttribute([...cb, ...cb, ...cb, ...cb], 2));
          g.setIndex([0, 2, 1, 1, 2, 3]);
          geoCache.set(widthCells, g);
        }
        const m = new THREE.Mesh(g, mat);
        m.renderOrder = 10;
        m.frustumCulled = false; // 頂点シェーダで拡張するため
        return m;
      },
    };
  },
};
