// 基準: 現行 Unity の Slide / Riser（NoteGeometry.PushSlideBand / PushRiserWall + Note.shader の帯分岐）を再現。
//  - Slide 帯: 地上は「暗めの半透明＋重なるほど明るく」（premultiplied: α0.30 + 加算0.22、gameplay-feel-r1 §5.3。
//    ステンシルによる4枚上限は省略）、空中は緑 α0.35。layerF で連続補間。左右端と中央にスクリーン空間一定幅の白線。
//  - 中継点(visible)・始点: 角丸矩形 + 白輪郭（Tap と同じ薄い板、色は帯と同じ色相で α0.95）。
//  - Riser: 壁の塗りなし。左右端の細い縁線（緑 α0.9）＋ 白い矢印3つ（note-visual-r1 §6.2）。
//  - 判定線を越えた部分は「食べる」（オートプレイで押さえている前提）。
export default {
  id: 'current',
  name: '現行（帯＋白線 / 縁線＋矢印3つ）',
  model: '基準',
  concept: '現在の Unity 実装の再現。比較の基準線。',
  unityCost: '— （現行そのもの）',
  create({ THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt }) {
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky);
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS } },
      vertexShader: /* glsl */ `
        ${glsl.place}
        attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag(0=帯,1=マーカー,2=頂点色そのまま)
        attribute vec4 aColor;
        attribute float aSide;  // マーカーの厚み方向 -1..+1
        varying float vDepth, vLayer, vTag, vSide; varying float vLocalX; varying vec4 vColor;
        void main() {
          float depth, sc;
          vec3 wp = musePlace(position.x, position.y, position.z, aExtra.x, aExtra.y, depth, sc);
          vDepth = depth; vLayer = position.y; vTag = aExtra.w; vLocalX = aExtra.z; vColor = aColor; vSide = aSide;
          gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform vec3 uGround, uSky;
        varying float vDepth, vLayer, vTag, vSide; varying float vLocalX; varying vec4 vColor;
        float roundedBox(vec2 p, vec2 b, float r) {
          vec2 q = abs(p) - b + r;
          return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }
        void main() {
          museClip(vDepth, vLayer, 1.0);
          float L = clamp(vLayer, 0.0, 1.0);
          vec3 base = mix(uGround, uSky, L);
          vec3 rgb; float a;
          if (vTag < 0.5) {
            // 帯: 左右端 + 中央の白線（スクリーン空間一定幅）
            float du = max(fwidth(vLocalX), 1e-5);
            float edge = (1.0 - smoothstep(0.0, 1.2 * du, vLocalX)) + (1.0 - smoothstep(0.0, 1.2 * du, 1.0 - vLocalX));
            float center = 1.0 - smoothstep(0.0, du, abs(vLocalX - 0.5));
            rgb = mix(base, vec3(1.0), clamp(edge + center, 0.0, 1.0));
            // 地上: premultiplied（α0.30 + 加算0.22）/ 空中: α0.35
            gl_FragColor = mix(museOut(rgb, 0.30, 0.22), museOut(rgb, 0.35, 0.0), L);
          } else if (vTag < 1.5) {
            // マーカー（角丸矩形 + 白輪郭）
            vec2 uv = vec2(vLocalX, vSide * 0.5 + 0.5);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float rPx = min(0.16 / max(duv.x, duv.y), min(bPx.x, bPx.y) * 0.9);
            float dist = roundedBox(pPx, bPx, rPx);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
            rgb = mix(base, vec3(1.0), outline);
            a = 0.95 * shapeA;
            gl_FragColor = museOut(rgb, a, 0.0);
          } else {
            gl_FragColor = museOut(vColor.rgb, vColor.a, 0.0);
          }
        }`,
      transparent: true, depthTest: false, depthWrite: false, side: THREE.DoubleSide,
      blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    });

    // 頂点を積むヘルパ: (u, layerF, t) + extra(dz, yUp, localX, tag) + color + side
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

    // Tap と同じ薄い板（マーカー）: 奥行き ±halfT（空中は×skyThicknessMul）
    function marker(B, p) {
      const u0 = uAt(p.cellF + gap), u1 = uAt(p.cellF + p.width - gap);
      const ht = halfT * (1 + (cfg.skyThicknessMul - 1) * p.layerF), y = d.zJudge * 0.012;
      const q = [[u0, -ht, 0, -1], [u1, -ht, 1, -1], [u1, ht, 1, 1], [u0, -ht, 0, -1], [u1, ht, 1, 1], [u0, ht, 0, 1]];
      // dz: + が奥。side +1 を奥側に
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
        const y = d.zJudge * 0.01, eh = 0.006;
        const cr = new THREE.Color(colors.slide.riser); // 線形空間の値（出力時に sRGB へ戻る）
        const edgeC = [cr.r, cr.g, cr.b, 0.9], arrowC = [1, 1, 1, 0.9];
        const quad = (p, c) => { for (const i of [0, 1, 2, 0, 2, 3]) B.v(p[i][0], p[i][1], note.t, 0, y, 0, 2, c); };
        for (let i = 0; i < steps; i++) {
          const la = L(i / steps), lb = L((i + 1) / steps);
          for (const uc of [u0, u1]) quad([[uc - eh, la], [uc + eh, la], [uc + eh, lb], [uc - eh, lb]], edgeC);
        }
        const uc = (u0 + u1) / 2, halfW = (u1 - u0) * 0.3, dir = Math.sign(note.layerTo - note.layerF);
        const span = Math.abs(note.layerTo - note.layerF), armH = span * 0.16, thick = span * 0.08;
        for (const f of [0.22, 0.5, 0.78]) {
          const lm = L(f), tip = lm + dir * armH * 0.5, base = lm - dir * armH * 0.5;
          quad([[uc - halfW, base], [uc - halfW, base + dir * thick], [uc, tip + dir * thick], [uc, tip]], arrowC);
          quad([[uc + halfW, base], [uc + halfW, base + dir * thick], [uc, tip + dir * thick], [uc, tip]], arrowC);
        }
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
