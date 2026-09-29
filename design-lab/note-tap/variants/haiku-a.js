// グラフィック寄り案A: インナーグルーブ + 適応型グロー。
// 縦方向の微細なグルーブ（方向性の強調）と、距離に応じた柔らかいグロー効果。
// 遠方では融合を防ぐため、グロー縁が強調される。
export default {
  id: 'haiku-a',
  name: 'インナーグルーブ + グロー',
  model: 'Haiku 4.5',
  concept: '縦グルーブで方向性を強調し、距離適応グローで遠方の視認性を改善。粒状感を抑えつつ、細い線化した状態でも位置が読める。',
  unityCost: 'フラットクワッド・単材でUnity移植は容易。グロー効果は uniform で深度対応なため、既存パイプライン（ZTest Always）と互換性あり。',
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

          // 遠方判定（fwidth 大きい = 遠方）
          float farness = log(bPx.y + 1.0);

          // 縦グルーブ（細い横条）
          float grooveFreq = 1.2;
          float grooveWave = sin(p.x * grooveFreq * 6.28) * 0.5 + 0.5;
          float grooveWidth = 0.04 * duv.x / bPx.x;
          float groove = abs(fract(p.x * grooveFreq) - 0.5) - 0.3;
          float grooveEffect = smoothstep(grooveWidth, -grooveWidth, groove) * 0.08;

          // 基本アウトライン（1.5px スクリーン空間）
          const float kOutlinePx = 1.5;
          float wy = min(kOutlinePx, max(0.0, bPx.y - 0.75) * 0.5);
          float endZone = max(bPx.y * 1.5, 1.0);
          float tEnd = clamp((abs(pPx.x) - (bPx.x - endZone)) / endZone, 0.0, 1.0);
          float w = mix(wy, kOutlinePx, tEnd);
          float outline = smoothstep(-w - 0.5, -w + 0.5, dist);

          // グロー効果（距離対応）
          float glowRadius = mix(2.0, 4.0, farness);
          float glowFalloff = 8.0 * max(1.0, farness * 0.5);
          float glow = exp(-dist * dist / glowFalloff) * mix(0.4, 0.8, farness);

          vec3 rgb = uColor;

          // 近距離：ハイライト/シャドウ
          float side = uv.y * 2.0 - 1.0;
          float close = clamp((bPx.y - 3.0) / 4.0, 0.0, 1.0);
          rgb *= 1.0 + 0.08 * side * close;
          float dTop = (0.5 - p.y) / duv.y;
          float dBot = (0.5 + p.y) / duv.y;
          float hl = (1.0 - smoothstep(w + 0.5, w + 2.0, dTop)) * step(w, dTop);
          float sh = (1.0 - smoothstep(w + 0.5, w + 2.0, dBot)) * step(w, dBot);
          rgb = mix(rgb, vec3(1.0), 0.35 * hl * close);
          rgb *= 1.0 - 0.22 * sh * close;

          // グルーブの暗化効果
          rgb *= 1.0 - grooveEffect;

          // グロー輪郭（ブレンド）
          vec3 glowColor = mix(uColor, vec3(1.0), 0.3);
          rgb = mix(rgb, glowColor, glow);

          // アウトライン合成
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
