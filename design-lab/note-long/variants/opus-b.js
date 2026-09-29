// Opus-B: キーキャップ系の「低い立体の帯」と「ゲート」の Riser。Sonnet のキーキャップ Tap と並べて一家族に見えることを狙う。
// Slide:
//  - 帯を低く持ち上げた板にし、内側（画面中央側）の側壁と手前の端面を暗いトーンで描く → 天面が浮いて見える。
//  - 天面は中央へ向かう柔らかいグラデーション（なぞる位置 = 中央が一番明るい）＋左右端の白線 1.5px。
//  - 凸な板なので「側壁 → 天面」の順に積めば ZTest Always のままでも前後関係が破綻しない
//    （外向きの側壁は天面の投影の内側に収まり、天面で上書きされる）。
// Riser:
//  - 左右の柱（円柱風の陰影を付けた細い面）＋行き先の横梁＋足元のキーキャップ風の台 → 「門をくぐって上へ」。
//  - 矢印は白2つ。壁の塗りはなし。
export default {
  id: 'opus-b',
  name: '立体の帯 / ゲート',
  model: 'Opus 5.5',
  concept: 'Slide は低く持ち上げた板（天面は中央ほど明るいグラデ＋白い縁、内側の側壁と手前の端面は暗色）で、キーキャップ Tap と同じ立体の家族にする。Riser は左右の柱・行き先の横梁・足元の台で「門」を作り、白い矢印2つで上向きを示す。',
  unityCost: '中。帯の頂点は現行の約3倍（天面＋側壁2枚）。凸形なので「側壁→天面」の積み順だけで ZTest Always のまま描ける。天面の半透明は現行の帯と同面積。Riser は細い面のみで現行より軽い。',
  create({ THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt }) {
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky), cR = new THREE.Color(colors.slide.riser);
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS }, uRiser: { value: cR } },
      vertexShader: /* glsl */ `
        ${glsl.place}
        attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag
        attribute vec2 aAux;    // x=面内の縦方向 0..1 など、y=予備
        varying float vDepth, vLayer, vTag, vLocalX;
        varying vec2 vAux;
        void main() {
          float depth, sc;
          vec3 wp = musePlace(position.x, position.y, position.z, aExtra.x, aExtra.y, depth, sc);
          vDepth = depth; vLayer = position.y; vTag = aExtra.w; vLocalX = aExtra.z; vAux = aAux;
          gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform vec3 uGround, uSky, uRiser;
        varying float vDepth, vLayer, vTag, vLocalX;
        varying vec2 vAux;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          museClip(vDepth, vLayer, 1.0);
          float L = clamp(vLayer, 0.0, 1.0);
          vec3 base = mix(uGround, uSky, L);
          float aTop = mix(0.62, 0.42, L);
          if (vTag < 0.5) {
            // ---- 天面: 中央ほど明るい + 左右端の白線 ----
            float du = max(fwidth(vLocalX), 1e-5);
            float c = 1.0 - abs(vLocalX - 0.5) * 2.0;          // 端0 → 中央1
            vec3 rgb = base * (0.78 + 0.22 * c) + vec3(0.18) * c * c;
            float dEdge = min(vLocalX, 1.0 - vLocalX) / du;
            float edge = 1.0 - smoothstep(1.0, 2.0, dEdge);
            rgb = mix(rgb, vec3(1.0), edge);
            gl_FragColor = museOut(rgb, mix(aTop, 0.95, edge), 0.0);
          } else if (vTag < 1.5) {
            // ---- 側壁・手前の端面: 暗色。上端に細い明線 ----
            float h = vAux.x; float dh = max(fwidth(h), 1e-5);
            vec3 rgb = base * mix(0.30, 0.50, h);
            rgb = mix(rgb, mix(base, vec3(1.0), 0.5), 1.0 - smoothstep(0.0, 1.2 * dh, 1.0 - h));
            gl_FragColor = museOut(rgb, mix(aTop, 0.9, 0.5), 0.0);
          } else if (vTag < 2.5) {
            // ---- 始点・中継点マーカー（天面の上に載るキーキャップ風の板） ----
            vec2 uv = vec2(vLocalX, vAux.x);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float isStart = step(0.5, vAux.y);
            float r = isStart > 0.5 ? min(bPx.x, bPx.y) : min(0.16 / max(duv.x, duv.y), min(bPx.x, bPx.y) * 0.9);
            float dist = roundedBox(pPx, bPx, r);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float ring = smoothstep(-w - 0.5, -w + 0.5, dist);
            float cx = 1.0 - abs(uv.x - 0.5) * 2.0;
            vec3 rgb = mix(base, vec3(1.0), 0.25 + 0.2 * cx);
            rgb = mix(rgb, vec3(1.0), ring);
            gl_FragColor = museOut(rgb, 0.97 * shapeA, 0.0);
          } else if (vTag < 3.5) {
            // ---- Riser の柱・梁: 円柱風（横方向の中央が明るい） ----
            float c = 1.0 - abs(vLocalX - 0.5) * 2.0;
            vec3 rgb = uRiser * (0.45 + 0.55 * c) + vec3(0.35) * pow(c, 4.0);
            gl_FragColor = museOut(rgb, 0.95, 0.0);
          } else if (vTag < 4.5) {
            // ---- Riser の足元の台（カプセル、キーキャップ風の天面） ----
            vec2 uv = vec2(vLocalX, vAux.x);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float dist = roundedBox(pPx, bPx, min(bPx.x, bPx.y));
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float ring = smoothstep(-w - 0.5, -w + 0.5, dist);
            vec3 rgb = mix(uRiser, vec3(1.0), 0.15 + 0.25 * (1.0 - abs(uv.x - 0.5) * 2.0));
            gl_FragColor = museOut(mix(rgb, vec3(1.0), ring), 0.97 * shapeA, 0.0);
          } else {
            gl_FragColor = museOut(vec3(1.0), 0.95, 0.0); // 矢印
          }
        }`,
      transparent: true, depthTest: false, depthWrite: false, side: THREE.DoubleSide,
      blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    });

    function builder() {
      const pos = [], ext = [], aux = [];
      return {
        v(u, L, t, dz, yUp, lx, tag, ax = 0, ay = 0) { pos.push(u, L, t); ext.push(dz, yUp, lx, tag); aux.push(ax, ay); },
        build() {
          const g = new THREE.BufferGeometry();
          g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
          g.setAttribute('aExtra', new THREE.Float32BufferAttribute(ext, 4));
          g.setAttribute('aAux', new THREE.Float32BufferAttribute(aux, 2));
          return g;
        },
      };
    }
    const halfT = d.zJudge * cfg.thicknessFrac, gap = cfg.gapCells;
    const H = halfT * 0.9; // 帯の高さ（地上判定線上のワールド単位）
    const y0 = d.zJudge * 0.01;

    function marker(B, p, isStart) {
      const u0 = uAt(p.cellF + gap), u1 = uAt(p.cellF + p.width - gap);
      const ht = halfT * (isStart ? 1.0 : 0.8) * (1 + (cfg.skyThicknessMul - 1) * p.layerF);
      const y = y0 + H + d.zJudge * 0.004;
      for (const [u, s, lx] of [[u0, -1, 0], [u1, -1, 1], [u1, 1, 1], [u0, -1, 0], [u1, 1, 1], [u0, 1, 0]])
        B.v(u, p.layerF, p.t, s * ht, y, lx, 2, s * 0.5 + 0.5, isStart ? 1 : 0);
    }

    return {
      makeSlide(note) {
        const B = builder(), S = sampleSlide(note);
        // 1) 側壁（左右とも積む。外向きの壁は後で天面に上書きされる）
        for (let i = 0; i < S.length - 1; i++) {
          const a = S[i], b = S[i + 1];
          for (const side of [0, 1]) {
            const ua = uAt(a.cellF + side * a.width), ub = uAt(b.cellF + side * b.width);
            B.v(ua, a.layerF, a.t, 0, y0, 0, 1, 0); B.v(ub, b.layerF, b.t, 0, y0, 0, 1, 0); B.v(ub, b.layerF, b.t, 0, y0 + H, 0, 1, 1);
            B.v(ua, a.layerF, a.t, 0, y0, 0, 1, 0); B.v(ub, b.layerF, b.t, 0, y0 + H, 0, 1, 1); B.v(ua, a.layerF, a.t, 0, y0 + H, 0, 1, 1);
          }
        }
        // 2) 手前の端面（始点、カメラ側を向く）
        const s0 = S[0], e0 = uAt(s0.cellF), e1 = uAt(s0.cellF + s0.width);
        B.v(e0, s0.layerF, s0.t, 0, y0, 0, 1, 0); B.v(e1, s0.layerF, s0.t, 0, y0, 0, 1, 0); B.v(e1, s0.layerF, s0.t, 0, y0 + H, 0, 1, 1);
        B.v(e0, s0.layerF, s0.t, 0, y0, 0, 1, 0); B.v(e1, s0.layerF, s0.t, 0, y0 + H, 0, 1, 1); B.v(e0, s0.layerF, s0.t, 0, y0 + H, 0, 1, 1);
        // 3) 天面
        for (let i = 0; i < S.length - 1; i++) {
          const a = S[i], b = S[i + 1], y = y0 + H;
          const a0 = uAt(a.cellF), a1 = uAt(a.cellF + a.width), b0 = uAt(b.cellF), b1 = uAt(b.cellF + b.width);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0); B.v(a1, a.layerF, a.t, 0, y, 1, 0); B.v(b1, b.layerF, b.t, 0, y, 1, 0);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0); B.v(b1, b.layerF, b.t, 0, y, 1, 0); B.v(b0, b.layerF, b.t, 0, y, 0, 0);
        }
        // 4) マーカー
        note.points.forEach((p, i) => { if (i === 0 || p.marker === 'visible') marker(B, p, i === 0); });
        return new THREE.Mesh(B.build(), mat);
      },
      makeRiser(note) {
        const B = builder();
        const u0 = uAt(note.cellF + gap), u1 = uAt(note.cellF + note.width - gap);
        const L = (k) => note.layerF + (note.layerTo - note.layerF) * k;
        const span = Math.max(Math.abs(note.layerTo - note.layerF), 0.25);
        const quad = (p, tag) => { for (const i of [0, 1, 2, 0, 2, 3]) B.v(p[i][0], L(p[i][1]), note.t, p[i][3] || 0, y0, p[i][2], tag, p[i][4] || 0); };
        // 足元の台（開始層の面に寝かせたカプセル）: dz で奥行き方向の厚みを持たせる
        const ht = halfT * 1.1;
        for (const [a, b, c] of [[0, 1, 2], [0, 2, 3]]) {
          const P = [[u0, -ht, 0, 0], [u1, -ht, 1, 0], [u1, ht, 1, 1], [u0, ht, 0, 1]];
          for (const k of [a, b, c]) B.v(P[k][0], note.layerF, note.t, P[k][1], y0 + d.zJudge * 0.004, P[k][2], 4, P[k][3]);
        }
        // 柱（左右、層方向に分割）
        const pw = 0.016;
        for (let i = 0; i < 12; i++) {
          const ka = i / 12, kb = (i + 1) / 12;
          for (const uc of [u0 + pw, u1 - pw]) quad([[uc - pw, ka, 0], [uc + pw, ka, 1], [uc + pw, kb, 1], [uc - pw, kb, 0]], 3);
        }
        // 横梁（行き先）
        const bh = 0.035 / span;
        quad([[u0, 1 - bh, 0], [u1, 1 - bh, 1], [u1, 1, 1], [u0, 1, 0]], 3);
        // 矢印2つ
        const uc = (u0 + u1) / 2, hw = Math.min((u1 - u0) * 0.28, 0.1), ah = 0.16, th = 0.06;
        for (const mid of [0.4, 0.68]) {
          quad([[uc - hw, mid - ah / 2, 0], [uc - hw, mid - ah / 2 + th, 0], [uc, mid + ah / 2 + th, 0], [uc, mid + ah / 2, 0]], 5);
          quad([[uc + hw, mid - ah / 2, 0], [uc + hw, mid - ah / 2 + th, 0], [uc, mid + ah / 2 + th, 0], [uc, mid + ah / 2, 0]], 5);
        }
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
