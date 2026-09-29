// Opus-A: 「レール＋拍刻み」の Slide と「発射台」の Riser。平面のまま（現行の頂点構造に近い）。
// Slide:
//  - 塗りは薄く（重なっても明るくなりすぎない）、左右端を太めのレール（白芯＋層色の縁、スクリーン空間一定幅）にして範囲を読ませる。
//  - 帯の中に「コンボ点の刻み」を横線で入れる（Slide は等間隔の判定点の列、note-spec §2.1）。
//    なぞるリズムが帯の上に見える。遠方で刻みが詰まったら自動で消す（fwidth で密度を判定）。
//  - 始点マーカーは Tap に近いカプセル＋白輪郭、中継点は角丸矩形（現行どおり「叩く」と誤読させない）。
// Riser:
//  - 足元（開始層）に太い発射台バー、行き先（layerTo）に細い到達線 → 部分移動（0→0.5）でも「どこからどこまで」が読める。
//  - 左右の縁レール＋大きい矢印1つ。壁の塗りは足元から上へ短くフェードする光だけ（面積を抑える）。
export default {
  id: 'opus-a',
  name: 'レール＋拍刻み / 発射台',
  model: 'Opus 5.5',
  concept: 'Slide は塗りを薄くして左右端を太いレールに、帯の中にコンボ点の刻み（横線）を入れてなぞるリズムを見せる。Riser は足元の発射台バー・行き先の到達線・大矢印1つで「ここから上へ、どこまで」を示し、壁の塗りは足元の短い光だけ。',
  unityCost: '低〜中。頂点構造は現行とほぼ同じ（帯・マーカー・Riser の四角形群）。刻みはフラグメントで時刻属性から計算するので頂点は増えない。Riser の足元の光は小面積の半透明のみ。',
  create({ THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt }) {
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky), cR = new THREE.Color(colors.slide.riser);
    const COMBO_STEP = 0.2; // BPM150 の 8分（コンボ刻み幅の既定、note-spec §2.3 の範囲内）
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS }, uRiser: { value: cR }, uStep: { value: COMBO_STEP } },
      vertexShader: /* glsl */ `
        ${glsl.place}
        attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag
        attribute vec2 aAux;    // x=厚み方向 -1..+1 / Riser の縦位置 0..1、y=Slide の始点時刻
        varying float vDepth, vLayer, vTag, vLocalX, vT;
        varying vec2 vAux;
        void main() {
          float depth, sc;
          vec3 wp = musePlace(position.x, position.y, position.z, aExtra.x, aExtra.y, depth, sc);
          vDepth = depth; vLayer = position.y; vTag = aExtra.w; vLocalX = aExtra.z; vAux = aAux; vT = position.z;
          gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform vec3 uGround, uSky, uRiser; uniform float uStep;
        varying float vDepth, vLayer, vTag, vLocalX, vT;
        varying vec2 vAux;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        // 幅 w(px) の線のマスク。d = 線の中心からの距離(px)
        float lineMask(float dpx, float w) { return 1.0 - smoothstep(w - 0.5, w + 0.5, dpx); }
        void main() {
          museClip(vDepth, vLayer, 1.0);
          float L = clamp(vLayer, 0.0, 1.0);
          vec3 base = mix(uGround, uSky, L);
          if (vTag < 0.5) {
            // ---- Slide 帯 ----
            float du = max(fwidth(vLocalX), 1e-5);
            float dEdge = min(vLocalX, 1.0 - vLocalX) / du;          // 端からの距離(px)
            float rail = lineMask(dEdge, 2.5);                       // レール全体 2.5px
            float railCore = lineMask(dEdge, 1.2);                   // 白芯
            float center = lineMask(abs(vLocalX - 0.5) / du, 0.5) * 0.6;
            // コンボ点の刻み: 始点からの経過を刻み幅で割った小数部。線は画面上 1.2px
            float ph = (vT - vAux.y) / uStep;
            float dph = max(fwidth(ph), 1e-5);
            float tick = lineMask(abs(fract(ph + 0.5) - 0.5) / dph, 0.6);
            tick *= 1.0 - smoothstep(0.12, 0.3, dph);                // 刻みが画面上で詰まったら消す
            tick *= smoothstep(3.0, 8.0, dEdge);                      // レールとは離す
            vec3 rgb = base; float a = mix(0.20, 0.22, L), add = mix(0.10, 0.0, L);
            rgb = mix(rgb, mix(base, vec3(1.0), 0.75), tick); a = mix(a, 0.75, tick);
            rgb = mix(rgb, vec3(1.0), center); a = max(a, center * 0.8);
            rgb = mix(rgb, mix(base, vec3(1.0), 0.35), rail); a = mix(a, 0.95, rail);
            rgb = mix(rgb, vec3(1.0), railCore);
            gl_FragColor = museOut(rgb, a, add * (1.0 - rail));
          } else if (vTag < 2.5) {
            // ---- 始点(1: カプセル) / 中継点(2: 角丸矩形) ----
            vec2 uv = vec2(vLocalX, vAux.x * 0.5 + 0.5);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float r = vTag < 1.5 ? min(bPx.x, bPx.y) : min(0.16 / max(duv.x, duv.y), min(bPx.x, bPx.y) * 0.9);
            float dist = roundedBox(pPx, bPx, r);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
            float close = clamp((bPx.y - 3.0) / 4.0, 0.0, 1.0);
            vec3 rgb = base * (1.0 + 0.12 * (uv.y * 2.0 - 1.0) * close);
            rgb = mix(rgb, vec3(1.0), outline);
            gl_FragColor = museOut(rgb, 0.97 * shapeA, 0.0);
          } else if (vTag < 3.5) {
            // ---- Riser: 足元の光（縦位置 vAux.x: 0=開始層 → 1=行き先）----
            float g = 1.0 - smoothstep(0.0, 0.45, vAux.x);
            gl_FragColor = museOut(uRiser, 0.45 * g * g, 0.0);
          } else {
            // ---- Riser: 線・矢印・バー（頂点色なし、白寄りの Riser 色）----
            vec3 rgb = vTag < 4.5 ? mix(uRiser, vec3(1.0), 0.55) : vec3(1.0);
            gl_FragColor = museOut(rgb, 0.95, 0.0);
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

    function marker(B, p, tag, thickMul) {
      const u0 = uAt(p.cellF + gap), u1 = uAt(p.cellF + p.width - gap);
      const ht = halfT * thickMul * (1 + (cfg.skyThicknessMul - 1) * p.layerF), y = d.zJudge * 0.012;
      for (const [u, s, lx] of [[u0, -1, 0], [u1, -1, 1], [u1, 1, 1], [u0, -1, 0], [u1, 1, 1], [u0, 1, 0]])
        B.v(u, p.layerF, p.t, s * ht, y, lx, tag, s);
    }

    return {
      makeSlide(note) {
        const B = builder(), S = sampleSlide(note), y = d.zJudge * 0.01, t0 = note.points[0].t;
        for (let i = 0; i < S.length - 1; i++) {
          const a = S[i], b = S[i + 1];
          const a0 = uAt(a.cellF), a1 = uAt(a.cellF + a.width), b0 = uAt(b.cellF), b1 = uAt(b.cellF + b.width);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0, 0, t0); B.v(a1, a.layerF, a.t, 0, y, 1, 0, 0, t0); B.v(b1, b.layerF, b.t, 0, y, 1, 0, 0, t0);
          B.v(a0, a.layerF, a.t, 0, y, 0, 0, 0, t0); B.v(b1, b.layerF, b.t, 0, y, 1, 0, 0, t0); B.v(b0, b.layerF, b.t, 0, y, 0, 0, 0, t0);
        }
        note.points.forEach((p, i) => {
          if (i === 0) marker(B, p, 1, 1.0);
          else if (p.marker === 'visible') marker(B, p, 2, 0.8);
        });
        return new THREE.Mesh(B.build(), mat);
      },
      makeRiser(note) {
        const B = builder(), y = d.zJudge * 0.01;
        const u0 = uAt(note.cellF + gap), u1 = uAt(note.cellF + note.width - gap);
        const L = (k) => note.layerF + (note.layerTo - note.layerF) * k;
        const quad = (p, tag) => { for (const i of [0, 1, 2, 0, 2, 3]) B.v(p[i][0], L(p[i][1]), note.t, 0, y, 0, tag, p[i][1]); };
        const span = Math.abs(note.layerTo - note.layerF);
        // 足元の光（下 45% だけ）
        const steps = 6;
        for (let i = 0; i < steps; i++) quad([[u0, 0.45 * i / steps], [u1, 0.45 * i / steps], [u1, 0.45 * (i + 1) / steps], [u0, 0.45 * (i + 1) / steps]], 3);
        // 縁レール（u 空間で細い帯。層方向に分割して収束補正に追従させる）
        const eh = 0.008;
        for (let i = 0; i < 12; i++) {
          const ka = i / 12, kb = (i + 1) / 12;
          for (const uc of [u0, u1]) quad([[uc - eh, ka], [uc + eh, ka], [uc + eh, kb], [uc - eh, kb]], 4);
        }
        // 発射台バー（開始層、太い）と到達線（行き先、細い）: 層方向の厚みで表す
        const barH = 0.06 / Math.max(span, 0.25), topH = 0.018 / Math.max(span, 0.25);
        quad([[u0, 0], [u1, 0], [u1, barH], [u0, barH]], 5);
        quad([[u0, 1 - topH], [u1, 1 - topH], [u1, 1], [u0, 1]], 4);
        // 大きい矢印1つ（中央やや上）
        const uc = (u0 + u1) / 2, hw = Math.min((u1 - u0) * 0.32, 0.12), mid = 0.6, ah = 0.22, th = 0.09;
        quad([[uc - hw, mid - ah / 2], [uc - hw, mid - ah / 2 + th], [uc, mid + ah / 2 + th], [uc, mid + ah / 2]], 5);
        quad([[uc + hw, mid - ah / 2], [uc + hw, mid - ah / 2 + th], [uc, mid + ah / 2 + th], [uc, mid + ah / 2]], 5);
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
