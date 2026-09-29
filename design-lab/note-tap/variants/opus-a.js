// Opus-A: 二重輪郭（外=濃紺 / 内=白）＋ 両端キャップ `( )`。平面1枚、現行シェーダの延長。
// 狙い:
//  - 現行の白1.5px輪郭は、明るい背景(#a0b298)や半透明の空中面の上ではコントラストが低い。
//    外側に濃い1pxを足すと、背景が明るくても暗くても縁が立つ（地図記号のハロー処理と同じ考え方）。
//  - 両端に白いキャップを置くと、横に隣接したノーツの継ぎ目が `)(` として必ず読める。
//    キャップ幅はスクリーン空間で下限を持たせるので、遠方で本体が線になっても端の点は残る。
//  - Flick の `< >`・中継点の角丸矩形とは端の形（丸＋白キャップ）で区別できる。
export default {
  id: 'opus-a',
  name: '二重輪郭＋端キャップ',
  model: 'Opus 5.5',
  concept: '外側に濃紺1px・内側に白1.5pxの二重輪郭で、明背景・半透明の空中面どちらでも縁が立つようにした。両端を白いキャップ `( )` にして横並びの継ぎ目を `)(` として必ず読ませる。平面のまま。',
  unityCost: '低。Note.shader のタップ分岐に外輪郭とキャップの数行を足すだけ。頂点数・描画順・ZTest Always の前提は現行と同じ。',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: {
        uColor: { value: new THREE.Color(colors.tap) },
        uDark: { value: new THREE.Color('#0b1a3a') },
      },
      vertexShader: /* glsl */ `
        varying vec2 vUv;
        void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor, uDark;
        varying vec2 vUv;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          vec2 p = vUv - 0.5;
          vec2 duv = vec2(max(fwidth(vUv.x), 1e-5), max(fwidth(vUv.y), 1e-5));
          vec2 pPx = p / duv;
          vec2 bPx = vec2(0.5) / duv;
          float r = min(bPx.x, bPx.y);
          float dist = roundedBox(pPx, bPx, r);          // 外周 = 0、内側が負（px）
          float shapeAlpha = 1.0 - smoothstep(-0.5, 0.5, dist);
          if (shapeAlpha <= 0.003) discard;

          // 縁の太さ: 本体が薄い(遠方)ほど細らせ、塗りが消えないようにする
          float room = max(bPx.y - 0.5, 0.0);
          float wDark = min(1.0, room * 0.35);
          float wWhite = min(1.5, room * 0.45);

          // 両端キャップ: 端から capPx の範囲を白寄りに。下限3pxで遠方でも端の点が残る
          float capPx = max(r * 1.1, 3.0);
          float inCap = smoothstep(bPx.x - capPx - 0.5, bPx.x - capPx + 0.5, abs(pPx.x));
          // キャップと本体の境目に濃い細線（近距離のみ）
          float close = clamp((bPx.y - 3.0) / 4.0, 0.0, 1.0);
          float seam = (1.0 - smoothstep(0.0, 1.0, abs(abs(pPx.x) - (bPx.x - capPx)))) * close;

          // 本体: 上（奥）側を明るく、下（手前）側を僅かに暗く
          float side = vUv.y * 2.0 - 1.0;
          vec3 body = uColor * (1.0 + 0.12 * side * close);
          vec3 cap = mix(uColor, vec3(1.0), 0.82);
          vec3 rgb = mix(body, cap, inCap);
          rgb = mix(rgb, uDark, 0.55 * seam);

          // 二重輪郭（外=濃, 内=白）
          float outerDark = smoothstep(-wDark - 0.5, -wDark + 0.5, dist);
          float innerWhite = smoothstep(-wDark - wWhite - 0.5, -wDark - wWhite + 0.5, dist) * (1.0 - outerDark);
          rgb = mix(rgb, vec3(1.0), innerWhite);
          rgb = mix(rgb, uDark, outerDark);
          gl_FragColor = vec4(rgb, shapeAlpha);
          #include <colorspace_fragment>
        }`,
      transparent: true, depthWrite: false, depthTest: false, side: THREE.DoubleSide,
    });
    const geoCache = new Map();
    return {
      makeNote({ widthCells, wWorld, halfT }) {
        let g = geoCache.get(widthCells);
        if (!g) {
          g = new THREE.PlaneGeometry(wWorld, halfT * 2);
          g.rotateX(-Math.PI / 2);
          geoCache.set(widthCells, g);
        }
        const m = new THREE.Mesh(g, mat);
        m.renderOrder = 10;
        return m;
      },
    };
  },
};
