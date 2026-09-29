// 基準: 現行 Unity の Tap（Note.shader の「タップ形状」分岐を GLSL へ移植）。
// 薄い板 + カプセル形SDF + スクリーン空間一定幅(1.5px)の白い輪郭線 + 近距離だけ出る上端ハイライト/下端影。
export default {
  id: 'current',
  name: '現行（フラットSDFカプセル）',
  model: '基準',
  concept: '現在の Unity 実装の再現。比較の基準線。note-visual-r1.md §2.2 の白輪郭 + gameplay-feel-r1 §5.2 の薄い立体感。',
  unityCost: '— （現行そのもの）',
  create({ THREE, colors }) {
    const mat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: new THREE.Color(colors.tap) } },
      vertexShader: /* glsl */ `
        varying vec2 vUv;
        void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
      fragmentShader: /* glsl */ `
        uniform vec3 uColor;
        varying vec2 vUv;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          vec2 uv = vUv;
          vec2 p = uv - 0.5;
          vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
          vec2 pPx = p / duv;
          vec2 bPx = vec2(0.5) / duv;
          float dist = roundedBox(pPx, bPx, min(bPx.x, bPx.y));
          float shapeAlpha = 1.0 - smoothstep(0.0, 1.0, dist);
          if (shapeAlpha <= 0.003) discard;
          const float kOutlinePx = 1.5;
          float wy = min(kOutlinePx, max(0.0, bPx.y - 0.75) * 0.5);
          float endZone = max(bPx.y * 1.5, 1.0);
          float tEnd = clamp((abs(pPx.x) - (bPx.x - endZone)) / endZone, 0.0, 1.0);
          float w = mix(wy, kOutlinePx, tEnd);
          float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
          vec3 rgb = uColor;
          float side = uv.y * 2.0 - 1.0;
          float close = clamp((bPx.y - 3.0) / 4.0, 0.0, 1.0);
          rgb *= 1.0 + 0.08 * side * close;
          float dTop = (0.5 - p.y) / duv.y;
          float dBot = (0.5 + p.y) / duv.y;
          float hl = (1.0 - smoothstep(w + 0.5, w + 2.0, dTop)) * step(w, dTop);
          float sh = (1.0 - smoothstep(w + 0.5, w + 2.0, dBot)) * step(w, dBot);
          rgb = mix(rgb, vec3(1.0), 0.35 * hl * close);
          rgb *= 1.0 - 0.22 * sh * close;
          rgb = mix(rgb, vec3(1.0), outline);
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
          g.rotateX(-Math.PI / 2); // uv.y=1 が奥(-z)側
          geoCache.set(widthCells, g);
        }
        const m = new THREE.Mesh(g, mat);
        m.renderOrder = 10;
        return m;
      },
    };
  },
};
