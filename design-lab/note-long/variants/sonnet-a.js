// 案A「キーキャップ系」: 不透明寄りのマット。Slide=マットな板＋太い縁レール＋等間隔の「枕木」目盛り、
// マーカー=面取り風の角丸キーキャップ。Riser=太い縁レール＋ベース/ゴールの横バー＋白縁付きの太いシェブロン。
//  加算・グロー無しで、色は塗り分けとハイライトだけで作る（Tap「キーキャップ」と同系統）。
const VERT = /* glsl */ `
  attribute vec4 aExtra;  // x=dz, y=yUp, z=localX(0..1), w=tag(0=帯,1=マーカー,2=頂点色そのまま)
  attribute vec4 aColor;
  attribute float aSide;
  varying float vDepth, vLayer, vTag, vSide, vT; varying float vLocalX; varying vec4 vColor;
  void main() {
    float depth, sc;
    vec3 wp = musePlace(position.x, position.y, position.z, aExtra.x, aExtra.y, depth, sc);
    vDepth = depth; vLayer = position.y; vT = position.z; vTag = aExtra.w; vLocalX = aExtra.z; vColor = aColor; vSide = aSide;
    gl_Position = projectionMatrix * viewMatrix * vec4(wp, 1.0);
  }`;

export default {
  id: 'sonnet-a',
  name: 'キーキャップ系（マット板＋レール＋枕木 / シェブロン壁）',
  model: 'Sonnet 5.5',
  concept: 'Tap「キーキャップ」と同系統の不透明マット。Slide は暗めのマット板に太い縁レールと枕木（等間隔の横目盛り。帯の進行方向・速度感が読める）、中央に白い芯線。始点/中継点は面取り風のキーキャップ（Tap と同素材・別色）。Riser は左右の太いレール＋根元/到達点の横バー＋白縁付きの太いシェブロンを層方向に並べ、部分 Riser は到達点バーの高さで読める。',
  unityCost: 'Slide は現行と同じ頂点数で、フラグメントに枕木（fwidth(t) で密度を自動フェード）とレール幅のみ追加＝低。加算/ステンシル不要（板は α0.5 の通常合成のみで重なりは単純に濃くなる）。マーカーは現行 + 面取りの陰影計算だけ。Riser は現行の縁線＋矢印にバー2本とシェブロン外縁を足した程度（1 Riser ≒ 数百頂点）。壁の塗りは無し。',
  create({ THREE, cfg, d, colors, uniforms, glsl, sampleSlide, uAt }) {
    const cG = new THREE.Color(colors.slide.ground), cS = new THREE.Color(colors.slide.sky);
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...uniforms, uGround: { value: cG }, uSky: { value: cS } },
      vertexShader: `${glsl.place}\n${VERT}`,
      fragmentShader: /* glsl */ `
        ${glsl.clip}
        uniform vec3 uGround, uSky;
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
            float du = max(fwidth(vLocalX), 1e-5);            // 1px あたりの localX
            float xe = min(vLocalX, 1.0 - vLocalX);             // 端からの距離
            // レール: 幅 3px（帯が細い時は帯幅の 1/4 まで）
            float railW = min(3.0 * du, 0.25);
            float rail = 1.0 - smoothstep(railW - du, railW, xe);
            float railHi = 1.0 - smoothstep(0.0, 1.2 * du, xe);   // レール最外周の白ハイライト 1.2px
            // 中央の白芯線 1px
            float center = 1.0 - smoothstep(0.0, du, abs(vLocalX - 0.5));
            // 枕木: 0.25s 周期の横目盛り、幅 1px。周期が 7px 未満になったらフェードして消す（遠方のモアレ防止）
            float P = 0.25, ft = max(fwidth(vT), 1e-6);
            float tickD = abs(fract(vT / P + 0.5) - 0.5) * P;
            float tick = (1.0 - smoothstep(0.0, 1.2 * ft, tickD)) * smoothstep(5.0, 10.0, P / ft);
            // マットな板: 少し暗い基調色 α0.5（地上）/ 0.45（空中）
            vec3 rgb = base * 0.55;
            rgb = mix(rgb, base * 1.0, tick * 0.75);
            rgb = mix(rgb, base, rail);
            rgb = mix(rgb, vec3(1.0), clamp(railHi * 0.85 + center * 0.9, 0.0, 1.0));
            float a = mix(0.52, 0.45, L);
            a = max(a, max(rail * 0.95, tick * 0.7));
            a = max(a, max(center, railHi) * 0.95);
            gl_FragColor = museOut(rgb, a, 0.0);
          } else if (vTag < 1.5) {
            // ---- マーカー: 面取り風キーキャップ（不透明）----
            vec2 uv = vec2(vLocalX, vSide * 0.5 + 0.5);
            vec2 p = uv - 0.5;
            vec2 duv = vec2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
            vec2 pPx = p / duv, bPx = vec2(0.5) / duv;
            float rPx = min(0.5 / max(duv.x, duv.y) * 0.32, min(bPx.x, bPx.y) * 0.95);
            float dist = roundedBox(pPx, bPx, rPx);
            float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
            if (shapeA <= 0.003) discard;
            float w = min(1.5, max(0.0, bPx.y - 0.75) * 0.5);
            float outline = smoothstep(-w - 0.5, -w + 0.5, dist);
            // 面取り: 縁から内側へ 22%(奥行き基準) は暗い斜面、内側は明るい天面。上(手前)側が明るい疑似ライティング
            float bevelW = min(bPx.y * 0.42, 5.0);
            float inner = clamp(-dist / max(bevelW, 1.0), 0.0, 1.0);   // 0=縁 1=天面
            float lit = 0.5 + 0.5 * clamp(-p.y * 2.0 * (1.0 - inner), -1.0, 1.0); // 斜面は手前側が明るい
            vec3 rgb = base * mix(0.55 + 0.35 * lit, 0.95, inner);
            rgb += vec3(0.10) * smoothstep(0.5, 1.0, inner) * (0.5 - p.y);   // 天面のごく弱いグラデ
            rgb = mix(rgb, vec3(1.0), outline);
            gl_FragColor = museOut(rgb, 0.98 * shapeA, 0.0);
          } else {
            gl_FragColor = museOut(vColor.rgb, vColor.a, 0.0);
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

    // 始点・中継点: Tap と同じ薄い板（キーキャップ風シェーディング）
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
        const cr = new THREE.Color(colors.slide.riser);
        const green = [cr.r, cr.g, cr.b, 1.0], white = [1, 1, 1, 1];
        const dark = [cr.r * 0.55, cr.g * 0.55, cr.b * 0.55, 0.55];
        const quad = (p, c) => { for (const i of [0, 1, 2, 0, 2, 3]) B.v(p[i][0], p[i][1], note.t, 0, y, 0, 2, c); };
        const dir = Math.sign(note.layerTo - note.layerF) || 1;
        const span = Math.abs(note.layerTo - note.layerF);
        const uc = (u0 + u1) / 2;

        // 壁全面の薄いマット（幅と高さを面で示す。α0.16 の暗い緑。オーバードロー抑制のため 2 枚の三角形のみ）
        for (let i = 0; i < steps; i++) {
          const la = L(i / steps), lb = L((i + 1) / steps);
          quad([[u0, la], [u1, la], [u1, lb], [u0, lb]], [dark[0], dark[1], dark[2], 0.16]);
        }
        // レール: 白縁(太) の上に緑(細)
        const ew = 0.010, ei = 0.0055;
        for (let i = 0; i < steps; i++) {
          const la = L(i / steps), lb = L((i + 1) / steps);
          for (const uq of [u0, u1]) {
            quad([[uq - ew, la], [uq + ew, la], [uq + ew, lb], [uq - ew, lb]], white);
            quad([[uq - ei, la], [uq + ei, la], [uq + ei, lb], [uq - ei, lb]], green);
          }
        }
        // ベース/ゴールの横バー（到達点が部分 Riser でも分かるよう、ゴール側は太い白）
        const bar = (l, h, c) => quad([[u0 - ew, l], [u1 + ew, l], [u1 + ew, l + dir * h], [u0 - ew, l + dir * h]], c);
        bar(note.layerF, 0.03, green);
        bar(note.layerTo - dir * 0.045, 0.045, white);
        bar(note.layerTo - dir * 0.037, 0.029, green);

        // シェブロン（白縁 → 緑）: 層方向に 0.2 層ピッチで並べる（全 Riser ≒ 5 個、0.5 で 2〜3 個）
        const n = Math.max(2, Math.round(span / 0.2));
        const halfW = Math.min((u1 - u0) * 0.36, 0.34);
        for (let j = 0; j < n; j++) {
          const lc = L((j + 0.5) / n);
          const h = Math.min(span / n * 0.62, 0.15);
          for (const [grow, c] of [[1.0, white], [0.0, green]]) {
            const hw = halfW * (1 + 0.14 * grow), th = h * (0.5 + 0.28 * grow), e = grow * 0.012 * dir;
            const bs = lc - dir * h * 0.5 - e * 0.6, tip = lc + dir * h * 0.5 + e;
            // 左腕・右腕
            quad([[uc - hw, bs], [uc - hw, bs - dir * th], [uc, tip - dir * th], [uc, tip]], c);
            quad([[uc + hw, bs], [uc + hw, bs - dir * th], [uc, tip - dir * th], [uc, tip]], c);
          }
        }
        return new THREE.Mesh(B.build(), mat);
      },
    };
  },
};
