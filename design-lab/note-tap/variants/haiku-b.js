// グラフィック寄り案B: ベベル + インナーラディアンス。
// 立体的なベベル端と、中心から周辺へのグラデーションで奥行き感を表現。
// 遠方でもエッジの構造が認識でき、隣接ノーツの融合を防ぐ。
export default {
  id: 'haiku-b',
  name: 'ベベルメタリック',
  model: 'Haiku 4.5',
  concept: 'ベベル端と中心発光グラデーションで立体感を強調。遠方では周辺暗化で輪郭が際立ち、隣接や詰まりを軽減。',
  unityCost: 'フラットクワッド・単材。ベベル計算は解析的（微分可能）なため、既存 ZTest Always パイプラインで高速。グラデーション uniform は深度非依存なため、軽量。',
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

          // 距離判定：bPx.y が小さい = 遠方、大きい = 近方
          float invDist = max(1.0, bPx.y);
          float closeFactor = clamp((bPx.y - 2.0) / 6.0, 0.0, 1.0);

          // ベベル効果（ボックス内側のエッジ浮き出し）
          float edgeDist = min(abs(pPx.x) - (bPx.x - 1.0), abs(pPx.y) - (bPx.y - 1.0));
          float bevelWidth = 2.0;
          float bevelSharp = smoothstep(bevelWidth, -bevelWidth, edgeDist);
          float bevelAmount = mix(0.6, 0.4, closeFactor);  // 遠方ほど浮き出し強調

          // インナーラディアンス（中心から周辺へのグラデーション）
          float centerDist = length(pPx) / (length(bPx) + 0.1);
          float innerGradient = 1.0 - smoothstep(0.0, 1.2, centerDist);

          // 基本アウトライン（スクリーン空間 1.5px）
          const float kOutlinePx = 1.5;
          float wy = min(kOutlinePx, max(0.0, bPx.y - 0.75) * 0.5);
          float endZone = max(bPx.y * 1.5, 1.0);
          float tEnd = clamp((abs(pPx.x) - (bPx.x - endZone)) / endZone, 0.0, 1.0);
          float w = mix(wy, kOutlinePx, tEnd);
          float outline = smoothstep(-w - 0.5, -w + 0.5, dist);

          // 周辺ビネット（遠方で強調）
          float vignetteStrength = mix(0.15, 0.35, 1.0 - closeFactor);
          float vignette = 1.0 - vignetteStrength * (1.0 - innerGradient);

          vec3 rgb = uColor;

          // ベベル面のライティング
          float side = uv.y * 2.0 - 1.0;
          float close = clamp((bPx.y - 3.0) / 4.0, 0.0, 1.0);
          rgb *= 1.0 + 0.08 * side * close;

          // インナーグロー（中心が明るい）
          float glowIntensity = mix(0.3, 0.6, closeFactor);
          vec3 glowColor = uColor + vec3(0.2, 0.25, 0.3);  // 白っぽく
          rgb = mix(rgb, glowColor, innerGradient * glowIntensity);

          // ベベル面の浮き出し
          vec3 bevelLight = vec3(1.0);
          rgb = mix(rgb, bevelLight, bevelSharp * bevelAmount * 0.5);

          // ビネット適用
          rgb *= vignette;

          // アウトライン
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
