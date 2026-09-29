// 案B「ネオン系」: 加算発光。Slide=淡い発光の帯＋ネオンチューブ状の縁（芯＋ハロー）＋流れる光パルス、
// マーカー=ドーム状の発光（中心が白熱、縁にリム光）。Riser=上向きに流れるシェブロン模様の発光壁＋縁チューブ＋ゴールの光線。
//  Tap「ネオンドーム」と同系統。
const VERT = /* glsl */ `
  attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag(0=帯,1=マーカー,3=Riser壁)
  attribute vec4 aColor;  // 壁: x=k(0..1: 根元→到達点), y=span(層の移動量)
  attribute float aSide;
  varying float vDepth, vLayer, vTag, vSide, vT; varying float vLocalX; varying vec4 vColor;
  void main() {
    float depth, sc;
    vec3 wp = musePlace(position.x, position.y, position.z, aExtra.x, aExtra.y, depth, sc);
    vDepth = depth; vLayer = position.y; vT = position.z; vTag = aExtra.w; vLocalX = aExtra.z; vColor = aColor; vSide = aSide;
    gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
  }`;

export default {
  id: 'sonnet-b',
  name: 'ネオン系（発光チューブ帯 / 流れるシェブロン壁）',
  model: 'Sonnet 5.5',
  concept: 'Tap「ネオンドーム」と同系統の加算発光。Slide は淡い発光の帯に、白い芯＋色付きハローのネオンチューブ縁と中央の芯線を描き、光のパルスが進行方向に流れる。始点/中継点はドーム状（中心が白熱・縁にリム光）で Tap と同素材・別色。Riser は上向きに流れるシェブロン（∧）模様の発光壁＋縁チューブ＋ゴールの光線で、動きで「上に振る」を伝え、部分 Riser は光線の高さで読める。',
  unityCost: '頂点数は現行とほぼ同じ（Slide 現行同等、Riser は壁の12分割×2三角形＋縁線程度）。加算合成（museOut の add）で重なりは自然に明るくなりステンシル不要。ただしフラグメントは fwidth と sin/fract が増え、Riser の壁は塗り面（加算・低α）でオーバードローが現行より増える（面積は判定線付近の1枚のみ）。パルス/流れは uSongTime を使うだけ。',
  create({ THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt }) {
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky);
    const cR = new THREE.Color(colors.slide.riser);
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS }, uRiser: { value: cR } },
      vertexShader: `${glsl.place}\n${VERT}`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform float uSongTime;
        uniform vec3 uGround, uSky, uRiser;
        varying float vDepth, vLayer, vTag, vSide, vT; varying float vLocalX; varying vec4 vColor;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          museClip(vDepth, vLayer, 1.0);
          float L = clamp(vLayer, 0.0, 1.0);
          vec3 base = mix(uGround, uSky, L);
          if (vTag < 0.5) {
            // ---- 帯 ----
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            // ネオン縁: 芯 1.2px（白）＋ハロー 幅6px（帯幅の 30% まで）
            float haloW = min(6.0 * du, 0.3);
            float halo = exp(-xe / max(haloW, 1e-4) * 2.2);
            float core = 1.0 - smoothstep(0.0, 1.4 * du, xe);
            // 中央: 芯 1px ＋ 小ハロー
            float xc = abs(vLocalX - 0.5);
            float cCore = 1.0 - smoothstep(0.0, du, xc);
            float cHalo = exp(-xc / max(min(3.0 * du, 0.1), 1e-4) * 2.0) * 0.35;
            // 進行方向パルス: 帯上で 0.8s 周期の明滅が判定線へ向けて流れる（振幅小）
            float pulse = 0.5 + 0.5 * sin((vT - uSongTime) * 7.85);
            pulse = mix(1.0, pulse, 0.35 * smoothstep(0.0, 1.0, 1.0 / max(fwidth(vT) * 40.0, 1.0)));
            float fill = 0.10;
            float glow = fill + halo * 0.55 + cHalo * pulse;
            vec3 rgb = mix(base, vec3(1.0), clamp(core * 0.9 + cCore * 0.85, 0.0, 1.0));
            // 加算主体（α0 に近い）＋薄い通常合成α で暗い背景でも帯の面を保つ
            float a = 0.10;
            float add = clamp(glow + core * 0.6 + cCore * 0.6, 0.0, 1.6);
            gl_FragColor = museOut(rgb, a, add * 0.9);
          } else if (vTag < 1.5) {
            // ---- マーカー: ドーム状の発光 ----
            vec2 uv = vec2(vLocalX, vSide * 0.5 + 0.5);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float rPx = min(0.5 / max(duv.x, duv.y) * 0.5, min(bPx.x, bPx.y) * 0.98); // ほぼカプセル
            float dist = roundedBox(pPx, bPx, rPx);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
            // ドーム: 縁からの深さ 0(縁)→1(中心) を球面プロファイルに
            float depthPx = clamp(-dist / max(bPx.y, 1.0), 0.0, 1.0);
            float dome = sqrt(1.0 - (1.0 - depthPx) * (1.0 - depthPx));
            float rim = pow(1.0 - dome, 2.5);                    // 縁のフレネル
            vec3 rgb = base * (0.55 + 0.7 * dome);               // 中心ほど明るい
            rgb = mix(rgb, vec3(1.0), smoothstep(0.75, 1.0, dome) * 0.55);   // 中心の白熱
            rgb += base * rim * 0.6;
            rgb = mix(rgb, vec3(1.0), outline);
            gl_FragColor = museOut(rgb, 0.95 * shapeA, 0.25 * shapeA);
          } else {
            // ---- Riser 壁 ----
            float k = vColor.x, span = max(vColor.y, 0.05);
            float du = max(fwidth(vLocalX), 1e-5);
            float xe = min(vLocalX, 1.0 - vLocalX);
            float x = abs(vLocalX - 0.5) * 2.0;                  // 0=中央 1=端
            // 縁チューブ（芯＋ハロー）
            float haloW = min(7.0 * du, 0.3);
            float halo = exp(-xe / max(haloW, 1e-4) * 2.2);
            float core = 1.0 - smoothstep(0.0, 1.5 * du, xe);
            // ゴール光線: 到達点(k=1) 付近
            float dk = max(fwidth(k), 1e-5);
            float goal = 1.0 - smoothstep(0.0, 1.6 * dk, 1.0 - k);
            float goalHalo = exp(-(1.0 - k) / max(min(8.0 * dk, 0.25), 1e-4) * 2.2) * 0.5;
            // 上向きに流れる∧シェブロン: 位相 = k*N + |x|*c - time*speed
            float N = span * 6.0 + 1.0;
            float ph = k * N + x * 0.45 - uSongTime * 1.6;
            float f = fract(ph);
            float fw = max(fwidth(ph), 1e-4);
            float line = (1.0 - smoothstep(0.0, 0.16 + fw, min(f, 1.0 - f))) * smoothstep(0.6, 0.25, fw);
            // 根元は暗く、到達点へ向かって明るく（上へ向かう方向性）
            float grad = mix(0.35, 1.0, k);
            float wall = 0.12 * grad + line * 0.55 * grad;
            vec3 rgb = mix(uRiser, vec3(1.0), clamp(core * 0.9 + goal * 0.9, 0.0, 1.0));
            float add = wall + halo * 0.55 + goalHalo + core * 0.6 + goal * 0.8;
            gl_FragColor = museOut(rgb, 0.05, clamp(add, 0.0, 1.6));
          }
        }`,
      transparent: true, depthTest: false, depthWrite: false, side: THREE.DoubleSide,
      blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    });

    function builder() {
      const pos = [], ext = [], col = [], side = [];
      return {
        v(u, L, t, dz, yUp, lx, tag, c = [1, 1, 1, 1], s = 0) { pos.push(u, L, t); ext.push(dz, yUp, lx, tag); col.push(...c); side.push(s); },
        build() {
          const g = new THREE.BufferGeometry();
          g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
          g.setAttribute('aExtra', new THREE.Float32BufferAttribute(ext, 4));
          g.setAttribute('aColor', new THREE.Float32BufferAttribute(col, 4));
          g.setAttribute('aSide', new THREE.Float32BufferAttribute(side, 1));
          return g;
        },
      };
    }
    const halfT = d.zJudge * cfg.thicknessFrac;
    const gap = cfg.gapCells;

    function marker(B, p) {
      const u0 = uAt(p.cellF + gap), u1 = uAt(p.cellF + p.width - gap);
      const ht = halfT * (1 + (cfg.skyThicknessMul - 1) * p.layerF), y = d.zJudge * 0.012;
      const q = [[u0, -ht, 0, -1], [u1, -ht, 1, -1], [u1, ht, 1, 1], [u0, -ht, 0, -1], [u1, ht, 1, 1], [u0, ht, 0, 1]];
      for (const [u, dz, lx, s] of q) B.v(u, p.layerF, p.t, dz, y, lx, 1, undefined, s);
    }

    return {
      makeSlide(note) {
        const B = builder();
        const S = sampleSlide(note);
        const y = d.zJudge * 0.01;
        for (let i = 0; i < S.length - 1; i++) {
          const a = S[i], b = S[i + 1];
          const a0 = uAt(a.cellF), a1 = uAt(a.cellF + a.width), b0 = uAt(b.cellF), b1 = uAt(b.cellF + b.width);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0); B.v(a1, a.layerF, a.t, 0, y, 1, 0); B.v(b1, b.layerF, b.t, 0, y, 1, 0);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0); B.v(b1, b.layerF, b.t, 0, y, 1, 0); B.v(b0, b.layerF, b.t, 0, y, 0, 0);
        }
        note.points.forEach((p, i) => { if (i === 0 || p.marker === 'visible') marker(B, p); });
        return new THREE.Mesh(B.build(), mat);
      },
      makeRiser(note) {
        const B = builder();
        const steps = 12, u0 = uAt(note.cellF), u1 = uAt(note.cellF + note.width);
        const L = (k) => note.layerF + (note.layerTo - note.layerF) * k;
        const y = d.zJudge * 0.01;
        const span = Math.abs(note.layerTo - note.layerF);
        // 壁: 層方向に12分割。k と span を頂点色に載せて fragment で模様を作る
        for (let i = 0; i < steps; i++) {
          const ka = i / steps, kb = (i + 1) / steps;
          const pts = [[u0, ka, 0], [u1, ka, 1], [u1, kb, 1], [u0, ka, 0], [u1, kb, 1], [u0, kb, 0]];
          for (const [u, k, lx] of pts) B.v(u, L(k), note.t, 0, y, lx, 3, [k, span, 0, 1]);
        }
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
