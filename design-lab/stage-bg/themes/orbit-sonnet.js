// orbit-sonnet「神話の軌道都市 r2」: 高空の橋から見る眼下の夜景 + 左右に奥へ並ぶ神殿（z軸に沿う）+ 奥の真円のゲート
// draw call: 都市板1 + ゲート板1 + 神殿1 + 浮遊足場2 + リング2 + 粒子1 = 8（＋共通の暗幕1）
// r1 からの変更: (1) ゲートを画面上の真円に（gateCircle / museGateDist、スクリーン空間の板）
//                (2) 神殿をステージ方向（z）に沿わせ、ひねり・ヨー揺れを廃止 (3) アニメ追加（大通りの光・同心リング・列柱の光・足場の上下・周回する光点）
//                全ての線は shared/lines.js（画面上で一定幅の帯メッシュ）
import { buildLines } from '../shared/lines.js';
import { gateCircle, GATE_GLSL } from '../shared/zone.js';

export default {
  id: 'orbit-sonnet', name: '神話の軌道都市 r2', model: 'Sonnet 5.5',
  concept: '高空に架かる橋。眼下に夜の未来都市の灯り、その大通りは奥の真円のゲート（虚空の門）の方へ収束して見える。左右にはステージと平行（奥行き方向）に神殿が回廊のように並び、浮遊足場と傾いたリングが漂う。',
  palette: '深い紺〜群青の地。灯りは彩度を抑えた冷たいシアン(#5b93b0)と淡い金(#b39a64)、構造線は #6f93b8 / 金 #b39a64。ノーツ色より暗く低彩度。ゲートは黒い円盤＋細いシアンの縁＋淡い金の目盛り環。',
  motion: '大通りを光の粒が奥（ゲート）へ流れる（周期約20秒）／ゲートの同心リングが奥へ吸い込まれる（1周36秒）／神殿の列柱を下から上へ光がなぞる（周期約17秒）／浮遊足場が上下（周期18〜20秒、逆位相）／リングの周回光点とリング自体の回転（1〜2分）。点滅なし。ゲート付近は遅く控えめ。',
  perf: '全画面板1（都市シェーダー: 2スケールのセルhash＋z方向の大通り＋環状路、ノイズなし）、ゲート板1（円のバウンディング矩形のみ）、線メッシュ5（合計約2000線分＝約8000頂点）、粒子1（400点、頂点シェーダーで上昇）。draw 8＋暗幕1。追加アニメは全て既存シェーダー内の数式のみ（追加テクスチャ・追加パスなし）。',
  unityCost: '都市板=Unlit シェーダー1本（逆射影でレイ→平面）。ゲート板=スクリーン空間クアッド＋円SDFシェーダー1本（gateCircle は C# に移植）。線は shared/lines.js のメッシュ＋頂点シェーダーをそのまま移植。粒子は Points 相当をクアッド粒子に置換。',
  clearColor: '#03050d',
  shade: { color: '#02030a', strength: 0.15 },
  stage: {
    groundFill: '#0b1226', groundFillAlpha: 1, groundLine: '#7fa4cc', groundLineAlpha: 0.28, groundJudge: '#9fc4e8',
    skyFill: '#2c4f80', skyFillAlpha: 0.2, skyLine: '#7fa4cc', skyLineAlpha: 0.28, skyJudge: '#9fc4e8',
  },
  create(ctx) {
    const { THREE, zone, glsl, camera, aspect } = ctx;
    const root = new THREE.Group();
    const disposables = [];
    const CITY_Y = -34;
    const cyan = [0.36, 0.52, 0.72], gold = [0.70, 0.60, 0.38];

    // ---------- ゲートの円（画面上の真円）と、都市の環状路の中心（ゲートの真後ろの遠方） ----------
    const gate = gateCircle(zone, aspect, { margin: 1.06 });
    const uGate = new THREE.Vector4(gate.cx, gate.cy, gate.r, aspect);
    // ゲート中心の視線が都市面 (y=CITY_Y) に当たる点 → 大通りの中心
    const cityCenter = (() => {
      const r = new THREE.Vector3(gate.cx, gate.cy, 0.5).unproject(camera).sub(camera.position).normalize();
      const t = (CITY_Y - camera.position.y) / r.y;
      return camera.position.clone().addScaledVector(r, t);
    })();

    const quad = (x0, y0, x1, y1) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute([x0, y0, 0, x1, y0, 0, x1, y1, 0, x0, y1, 0], 3));
      g.setIndex([0, 1, 2, 0, 2, 3]);
      return g;
    };

    // ---------- 都市（全画面板1枚） ----------
    const qg = quad(-1, -1, 1, 1);
    const cityMat = new THREE.ShaderMaterial({
      uniforms: {
        ...zone.uniforms, uTime: { value: 0 }, uGate: { value: uGate },
        uInvProj: { value: camera.projectionMatrixInverse.clone() },
        uCamRot: { value: new THREE.Matrix3().setFromMatrix4(camera.matrixWorld) },
        uCamPos: { value: camera.position.clone() },
        uCityY: { value: CITY_Y },
        uCityC: { value: new THREE.Vector2(cityCenter.x, cityCenter.z) },
      },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        ${GATE_GLSL}
        uniform float uTime, uCityY; uniform mat4 uInvProj; uniform mat3 uCamRot; uniform vec3 uCamPos; uniform vec2 uCityC;
        float h11(float n) { return fract(sin(n * 127.1) * 43758.5453); }
        float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        // 1スケール分の灯り。cs=セル寸法。aa=1ピクセルがセル何個分か
        vec3 lights(vec2 p, float cs, float aa, float dens, float t) {
          vec2 q = p / cs, id = floor(q), f = fract(q) - 0.5;
          float h = h21(id), h2 = h21(id + 7.3), h3 = h21(id + 19.1);
          float lit = step(1.0 - dens, h);
          vec2 off = (vec2(h2, h3) - 0.5) * 0.4;
          float r = 0.05 + 0.09 * h3;
          float soft = max(aa, 0.04);
          float dot_ = (1.0 - smoothstep(r - soft, r + soft, length(f - off))) * lit;
          float sway = 0.72 + 0.28 * sin(t * (0.25 + 0.2 * h2) + h * 40.0); // ゆっくり揺らぐ
          float avg = lit * 3.14159 * r * r;                                // 画素より小さくなったら平均輝度へ
          float fade = smoothstep(0.25, 0.7, aa);
          float a = mix(dot_, avg, fade) * sway;
          vec3 cool = vec3(0.30, 0.55, 0.70), gold = vec3(0.72, 0.60, 0.36);
          return a * mix(cool, gold, step(0.78, h3 * 0.6 + h2 * 0.5));
        }
        void main() {
          vec2 ndc = museBaseNdc();
          vec4 v = uInvProj * vec4(ndc, 1.0, 1.0);
          vec3 dir = normalize(uCamRot * (v.xyz / v.w));
          vec3 col = vec3(0.012, 0.022, 0.060);
          if (dir.y < -0.004) {
            float tt = (uCityY - uCamPos.y) / dir.y;
            vec3 hit = uCamPos + dir * tt;
            vec2 p = hit.xz;
            vec2 dp = fwidth(p);
            float px = max(dp.x, dp.y);
            // 碁盤（近い街区）と密な細かい灯り
            vec3 L = lights(p, 4.0, px / 4.0, 0.6, uTime) * 0.8;
            L += lights(p + 31.7, 1.5, px / 1.5, 0.4, uTime + 5.0) * 0.45;
            // 街路の碁盤（ごく淡い）
            vec2 gq = abs(fract(p / 12.0 + 0.5) - 0.5) * 12.0;
            float gl = 1.0 - smoothstep(0.0, max(px, 0.12) * 1.5, min(gq.x, gq.y));
            L += vec3(0.05, 0.10, 0.16) * gl * (1.0 - smoothstep(0.15, 0.6, px));
            // 大通り: z 方向に平行に走る（消失点＝ゲートの真後ろへ収束して見える）。光の粒が奥（-z）へ流れる
            float aaR = max(px, 0.2);
            float ax = p.x / 26.0;
            float ida = floor(ax + 0.5);
            float da = abs(fract(ax + 0.5) - 0.5) * 26.0;               // 通りまでの距離（m）
            float road = 1.0 - smoothstep(0.0, aaR * 1.6 + 0.10, da);
            // 光の粒: 7 m/s で -z へ（画面上は遠いほど遅く見える）。通りごとに位相・有無をずらす
            float flow = fract(p.y / 70.0 + uTime * 0.10 + h11(ida) * 3.0);
            float packet = exp(-pow((flow - 0.5) / 0.06, 2.0));
            float dash = 0.35 + 2.4 * packet * step(0.30, h11(ida + 3.0));
            float roadFade = smoothstep(1.0, 4.0, 26.0 / max(px, 0.01)) * step(0.5, abs(ida)); // 中央の通り（ステージの下）は省く
            // 環状路（都市中心から 60m ごと）: 遠方では横切る街路に見える
            float rr = length(p - uCityC);
            float ringL = abs(fract(rr / 60.0 + 0.5) - 0.5) * 60.0;
            float ringLine = 1.0 - smoothstep(0.0, aaR * 1.6 + 0.08, ringL);
            float ringFade = smoothstep(1.0, 4.0, 60.0 / max(px, 0.01));
            float haze = smoothstep(120.0, 520.0, tt);
            col += L * (1.0 - 0.8 * haze);
            col = mix(col, vec3(0.020, 0.040, 0.095), haze * 0.85);
            col += vec3(0.16, 0.30, 0.42) * (road * roadFade * 0.55 * dash + ringLine * ringFade * 0.18) * (1.0 - 0.35 * haze);
            // 眼下の深さ感: 画面下ほどわずかに明るい紺
            col += vec3(0.004, 0.010, 0.026) * smoothstep(0.2, -1.0, ndc.y);
          }
          // ゲートの周りは円形に沈める（暗い形が「円」として読める）。角丸長方形ではなく museGateDist 基準
          float gd = museGateDist(ndc);
          col *= mix(0.04, 1.0, smoothstep(0.0, 0.34, gd));
          gl_FragColor = vec4(col, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    });
    const city = new THREE.Mesh(qg, cityMat);
    city.frustumCulled = false; city.renderOrder = -100;
    root.add(city);
    disposables.push(qg, cityMat);

    // ---------- 虚空のゲート（画面上の真円。スクリーン空間の板1枚で、円盤・縁・同心リング・目盛り環・光点を描く） ----------
    const ext = 1.32; // 円の外側（目盛り環・ハロー）まで覆う
    const gateGeo = quad(gate.cx - gate.r * ext / aspect, gate.cy - gate.r * ext, gate.cx + gate.r * ext / aspect, gate.cy + gate.r * ext);
    const gateMat = new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime: { value: 0 }, uGate: { value: uGate } },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.0, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        ${GATE_GLSL}
        uniform float uTime;
        float band(float x, float w) { return exp(-x * x / (w * w)); }
        void main() {
          vec2 ndc = museBaseNdc();
          vec2 q = (ndc - uGate.xy) * vec2(uGate.w, 1.0);
          float R = uGate.z;
          float rad = length(q), ang = atan(q.y, q.x);
          float dist = rad - R;
          float fw = fwidth(rad);
          float wLine = max(0.0016, fw * 0.8);              // 縁の太さ（縦単位。約1.3px相当）
          float disc = 1.0 - smoothstep(-fw, fw, dist);
          float mk = museSpawnMask(ndc);
          float quiet = 1.0 - 0.75 * mk, quietRim = 1.0 - 0.86 * mk;     // ノーツ出現位置の中は控えめに（縁・目盛りも含めて全て）
          vec3 cyanC = vec3(0.42, 0.62, 0.80), goldC = vec3(0.72, 0.62, 0.40);
          vec3 glow = vec3(0.0);
          // 縁の光（細い）と、内外にじむ淡い光
          glow += cyanC * 0.85 * band(dist, wLine) * quietRim;
          glow += cyanC * 0.10 * exp(-max(dist, 0.0) / 0.035) * step(0.0, dist) * quietRim;
          glow += cyanC * 0.07 * exp(dist / 0.05) * step(dist, 0.0) * quiet;
          // 奥へ続く同心リング: 中心へ吸い込まれる（1周36秒、位相ずらし5本）。近似的な遠近（半径 ∝ 1/z）
          for (int i = 0; i < 5; i++) {
            float s = fract(uTime / 36.0 + float(i) / 5.0);
            float rr = R * 0.93 / (1.0 + s * 4.5);
            float fade = smoothstep(0.0, 0.12, s) * (1.0 - smoothstep(0.55, 1.0, s));
            glow += cyanC * 0.32 * fade * quiet * band(rad - rr, max(wLine * 0.9, rr * 0.010));
          }
          // 外側の目盛り環（金）: ごくゆっくり回る（周期約4分）
          float tk = ang / 6.28318 + uTime * 0.004;
          float m60 = fract(tk * 60.0) - 0.5;
          float m12 = fract(tk * 12.0) - 0.5;
          float tickShort = 1.0 - smoothstep(0.10, 0.16, abs(m60));
          float tickLong = 1.0 - smoothstep(0.06, 0.10, abs(m12) * 5.0);
          float bandShort = smoothstep(R * 1.035, R * 1.037, rad) * (1.0 - smoothstep(R * 1.062, R * 1.064, rad));
          float bandLong = smoothstep(R * 1.035, R * 1.037, rad) * (1.0 - smoothstep(R * 1.105, R * 1.107, rad));
          glow += goldC * (0.34 * tickShort * bandShort + 0.55 * tickLong * bandLong) * quiet;
          glow += goldC * 0.22 * band(rad - R * 1.03, wLine) * quiet;
          // 縁を周回する小さな光点3つ（周期約100秒）
          float da = abs(fract((ang / 6.28318 - uTime * 0.01) * 3.0) - 0.5) / 3.0 * 6.28318 * R; // 弧長
          glow += vec3(0.75, 0.90, 1.0) * 0.9 * band(da, 0.006) * band(dist, wLine * 2.0) * quiet;
          // 全体を円のバウンディング内でフェード（矩形の切れ目を出さない）
          float edgeFade = 1.0 - smoothstep(R * 1.16, R * 1.30, rad);
          vec3 dark = vec3(0.003, 0.006, 0.016);
          float a = disc * 0.985;
          vec3 rgb = dark * a + glow * edgeFade;
          gl_FragColor = vec4(rgb, a);
          #include <colorspace_fragment>
        }`,
      transparent: true, blending: THREE.CustomBlending,
      blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor, blendSrcAlpha: THREE.OneFactor, blendDstAlpha: THREE.OneMinusSrcAlphaFactor,
      depthTest: false, depthWrite: false,
    });
    const gateMesh = new THREE.Mesh(gateGeo, gateMat);
    gateMesh.frustumCulled = false; gateMesh.renderOrder = -90;
    root.add(gateMesh);
    disposables.push(gateGeo, gateMat);

    // ---------- 線の道具 ----------
    const mkKit = () => {
      const segs = [];
      const seg = (a, b, c, k = 1, p = 0) => segs.push({ a, b, c, k, p });
      const box = (cx, cy, cz, w, h, dd, c, k = 1, p = 0) => {
        const x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2, z0 = cz - dd / 2, z1 = cz + dd / 2;
        const V = [[x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0], [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]];
        [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]].forEach(([i, j]) => seg(V[i], V[j], c, k, p));
      };
      return { segs, seg, box };
    };

    // ---------- 神殿（長手 = z 方向。ステージと平行。近い端（+z）の破風と、ステージ側の列柱が見える） ----------
    // p: 1=列柱（光が下から上へ）、2=梁・棟（光が奥へ走る）、0=静的
    const temple = (K, o, S) => {
      // 上から見下ろす視点（38°）では垂直の線が画面下中央へ倒れて見えるので、柱は低く太く、屋根・台座（水平面）で神殿を読ませる
      const L = 24 * S, W = 14 * S;
      const at = (x, y, z) => [o[0] + x, o[1] + y, o[2] + z];
      // 台座（三段の基壇）
      K.box(...at(0, 0, 0), W + 4 * S, 0.9 * S, L + 4 * S, cyan, 0.85);
      K.box(...at(0, 0.7 * S, 0), W + 2.4 * S, 0.5 * S, L + 2.4 * S, cyan, 0.6);
      K.box(...at(0, 1.2 * S, 0), W + 0.8 * S, 0.5 * S, L + 0.8 * S, cyan, 0.45);
      // 列柱: 長手の両側 n=8、両端 n=4（角は共有）
      const cx = W / 2 - 0.9 * S, cz = L / 2 - 1.3 * S, ch = 5.0 * S, cy = 1.45 * S + ch / 2;
      const cw = 1.0 * S, nz = 6, nx = 4;
      for (let i = 0; i < nz; i++) {
        const z = -cz + (2 * cz * i) / (nz - 1);
        K.box(...at(-cx, cy, z), cw, ch, cw, cyan, 0.8, 1);
        K.box(...at(cx, cy, z), cw, ch, cw, cyan, 0.8, 1);
      }
      for (let i = 1; i < nx - 1; i++) {
        const x = -cx + (2 * cx * i) / (nx - 1);
        K.box(...at(x, cy, -cz), cw, ch, cw, cyan, 0.5, 1);
        K.box(...at(x, cy, cz), cw, ch, cw, cyan, 0.8, 1);
      }
      // 梁（エンタブラチュア）
      const yb = cy + ch / 2 + 0.4 * S;
      K.box(...at(0, yb, 0), W, 0.8 * S, L, gold, 0.8, 2);
      // 切妻屋根: 破風（三角）、棟、垂木。上から見て屋根の形が読める
      const yt = yb + 0.4 * S, hgt = 3.6 * S, ya = yt + hgt;
      for (const z of [L / 2, -L / 2]) {
        const a = at(-W / 2, yt, z), b = at(W / 2, yt, z), t = at(0, ya, z);
        const k = z > 0 ? 0.95 : 0.5;
        K.seg(a, b, gold, k); K.seg(a, t, gold, k); K.seg(b, t, gold, k);
      }
      K.seg(at(0, ya, -L / 2), at(0, ya, L / 2), gold, 0.8, 2);
      const nr = 6;
      for (let i = 1; i < nr; i++) {
        const z = -L / 2 + (L * i) / nr;
        K.seg(at(0, ya, z), at(-W / 2, yt, z), gold, 0.32); K.seg(at(0, ya, z), at(W / 2, yt, z), gold, 0.32);
      }
    };
    const TK = mkKit();
    for (const sgn of [-1, 1]) {
      temple(TK, [sgn * 24.7, -10, -46], 1.1);   // 手前の大きな神殿
      temple(TK, [sgn * 27, -12, -100], 1.3);   // 奥の神殿（消失点へ向かって小さく）
    }
    const templeMesh = buildLines(THREE, ctx, TK.segs, {
      widthPx: 2.1, opacity: 0.78,
      frag: /* glsl */ `
        // 列柱: 光の帯が下から上へなぞる（周期約17秒、柱ごとに位相をずらす）
        if (vP > 0.5 && vP < 1.5) {
          float ph = fract((vW.y + 12.0) * 0.045 - uTime * 0.06 + vW.z * 0.037 + vW.x * 0.021);
          float pu = exp(-pow((ph - 0.86) / 0.06, 2.0));
          col = mix(col * (1.0 + 0.4 * pu), vec3(0.62, 0.82, 1.0) * 0.9, pu * 0.55);
        }
        // 梁・棟: 小さな光が奥（ゲート方向）へ走る
        if (vP > 1.5) {
          float ph = fract(vW.z * 0.012 + uTime * 0.03 + vW.x * 0.02);
          float pu = exp(-pow((ph - 0.5) / 0.035, 2.0));
          col = mix(col, vec3(0.95, 0.85, 0.6), pu * 0.55);
        }`,
    });
    templeMesh.renderOrder = -70;
    root.add(templeMesh);
    disposables.push(templeMesh.geometry, templeMesh.material);

    // ---------- 浮遊足場（左右で逆位相に上下） ----------
    const platforms = [];
    for (const sgn of [-1, 1]) {
      const K = mkKit();
      const plat = (x, y, z, w, dd) => {
        K.box(sgn * x, y, z, w, 0.6, dd, cyan, 0.7);
        K.box(sgn * x, y - 0.75, z, w * 0.6, 0.9, dd * 0.6, cyan, 0.4);
        K.box(sgn * x, y - 1.6, z, w * 0.25, 0.9, dd * 0.25, cyan, 0.25);
      };
      plat(14, -6, -20, 6, 5);
      plat(40, -3, -30, 7, 6);
      plat(15, -2, -68, 6, 5);
      plat(46, -6, -76, 8, 6);
      const m = buildLines(THREE, ctx, K.segs, { widthPx: 1.6, opacity: 0.55 });
      m.renderOrder = -72;
      root.add(m);
      disposables.push(m.geometry, m.material);
      platforms.push({ m, sgn, phase: sgn > 0 ? 0 : Math.PI });
    }

    // ---------- リング（傾けて回転、光点が周回） ----------
    const rings = [];
    for (const sgn of [-1, 1]) {
      const K = mkKit();
      const ringLines = (r, n, k, col, flag) => {
        for (let i = 0; i < n; i++) {
          const a0 = (i / n) * Math.PI * 2, a1 = ((i + 1) / n) * Math.PI * 2;
          K.seg([Math.cos(a0) * r, 0, Math.sin(a0) * r], [Math.cos(a1) * r, 0, Math.sin(a1) * r], col, k, flag ? 1 + i / n : 0);
        }
      };
      ringLines(15, 96, 0.8, cyan, true); ringLines(13.6, 96, 0.4, cyan, false); ringLines(19, 96, 0.35, gold, false);
      for (let i = 0; i < 36; i++) { // 目盛り
        const a = (i / 36) * Math.PI * 2, r0 = 15, r1 = i % 3 === 0 ? 17.4 : 16;
        K.seg([Math.cos(a) * r0, 0, Math.sin(a) * r0], [Math.cos(a) * r1, 0, Math.sin(a) * r1], gold, i % 3 === 0 ? 0.7 : 0.4);
      }
      const l = buildLines(THREE, ctx, K.segs, {
        widthPx: 1.6, opacity: 0.55, uniforms: { uDir: { value: sgn } },
        frag: /* glsl */ `
          if (vP > 0.5) { // 周回する小さな光点（3つ、周期約60秒）
            float ph = vP - 1.0 + vU / 96.0;
            float x = fract((ph - uTime * 0.016 * uDir) * 3.0);
            float dd = min(x, 1.0 - x) / 3.0 * 6.28318 * 15.0;
            float pu = exp(-pow(dd / 0.9, 2.0));
            col = mix(col * (1.0 + 0.3 * pu), vec3(0.75, 0.92, 1.0), pu * 0.6);
          }`,
      });
      l.renderOrder = -71;
      const g = new THREE.Group(), spin = new THREE.Group();
      spin.add(l); g.add(spin);
      g.position.set(sgn * 50, 6, -70);
      g.rotation.set(0.5, 0, sgn * 0.35);
      root.add(g);
      rings.push({ spin, sgn });
      disposables.push(l.geometry, l.material);
    }

    // ---------- 粒子（400点、頂点シェーダーで上昇） ----------
    const N = 400;
    const pg = new THREE.BufferGeometry();
    const seedA = new Float32Array(N * 4), posA = new Float32Array(N * 3);
    let s = 12345;
    const rnd = () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
    for (let i = 0; i < N; i++) {
      const side = rnd() < 0.5 ? -1 : 1;
      seedA[i * 4] = side * (8 + rnd() * 62);     // x
      seedA[i * 4 + 1] = rnd();                  // 位相
      seedA[i * 4 + 2] = -(12 + rnd() * 88);     // z
      seedA[i * 4 + 3] = 0.6 + rnd() * 0.8;      // 速さ係数
    }
    pg.setAttribute('position', new THREE.BufferAttribute(posA, 3));
    pg.setAttribute('aSeed', new THREE.BufferAttribute(seedA, 4));
    const pm = new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uGate: { value: uGate }, uTime: { value: 0 }, uPx: { value: 1 } },
      vertexShader: /* glsl */ `
        attribute vec4 aSeed; uniform float uTime, uPx; varying float vA; varying float vG;
        void main() {
          float H = 44.0;
          float ph = fract(aSeed.y + uTime * 0.018 * aSeed.w);   // 約1〜1.5分で1周（0.8 m/s前後）
          vec3 p = vec3(aSeed.x + 1.5 * sin(uTime * 0.2 + aSeed.y * 30.0), -34.0 + ph * H, aSeed.z);
          vA = smoothstep(0.0, 0.15, ph) * (1.0 - smoothstep(0.65, 1.0, ph));
          vG = fract(aSeed.y * 91.7);
          vec4 mv = modelViewMatrix * vec4(p, 1.0);
          gl_Position = projectionMatrix * mv;
          gl_PointSize = uPx * clamp(60.0 / -mv.z, 1.5, 3.2);
        }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        ${GATE_GLSL}
        varying float vA; varying float vG;
        void main() {
          vec2 c = gl_PointCoord - 0.5;
          float a = (1.0 - smoothstep(0.15, 0.5, length(c))) * vA;
          vec3 col = mix(vec3(0.30, 0.52, 0.66), vec3(0.72, 0.62, 0.40), step(0.8, vG));
          a *= 0.55;
          vec2 ndc = museBaseNdc();
          a *= 1.0 - museSpawnMask(ndc);                      // ノーツ出現位置には出さない
          a *= smoothstep(0.0, 0.08, museGateDist(ndc));      // ゲートの中には出さない
          gl_FragColor = vec4(col * a, a);
        }`,
      transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false,
    });
    const pts = new THREE.Points(pg, pm);
    pts.frustumCulled = false; pts.renderOrder = -60;
    root.add(pts);
    disposables.push(pg, pm);

    return {
      object: root,
      update({ t }) {
        cityMat.uniforms.uTime.value = t;
        gateMat.uniforms.uTime.value = t;
        pm.uniforms.uTime.value = t;
        pm.uniforms.uPx.value = window.devicePixelRatio || 1;
        templeMesh.material.uniforms.uTime.value = t;
        for (const p of platforms) {
          p.m.material.uniforms.uTime.value = t;
          p.m.position.y = 0.7 * Math.sin(t * 0.33 + p.phase);   // 周期約19秒
        }
        for (const r of rings) {
          r.spin.rotation.y = t * 0.05 * r.sgn;
          r.spin.children[0].material.uniforms.uTime.value = t;
        }
      },
      dispose() { disposables.forEach((o) => o.dispose && o.dispose()); },
    };
  },
};
