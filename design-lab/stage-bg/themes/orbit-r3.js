// orbit-r3「神話の軌道都市 r3」: Sonnet 5.5 の r2 を土台に、ユーザー評価(2026-09-30)で調整（Opus 5.5）
// r2 からの変更:
//  (1) ゲート = r1 のデザイン（中心 v=0.9、半径 = NDC横0.23 でステージ幅を超え上部は画面外、縁2重＋動かない同心リング3本）を
//      画面上の真円で描く（スクリーン空間の板1枚）。r1 の暗い円盤だけで暗部は十分暗いので共通の暗幕はなし
//  (2) r2 で左右に浮いていた時計盤のリングを、ゲートの外周に置いてゆっくり回す（ゲート板の中で描く＝追加 draw なし）
//  (3) 神殿・浮遊足場を手前へ（ゲート付近の密集を避ける。最前の神殿は画面外にはみ出してよい）
//  (4) 都市の灯りを減らして神殿を浮かせる
// draw call: 都市板1 + ゲート板1 + 神殿1 + 浮遊足場2 + 粒子1 = 6（暗幕なし）
import { buildLines } from '../shared/lines.js';
import { GATE_GLSL } from '../shared/zone.js';

export default {
  id: 'orbit-r3', name: '神話の軌道都市 r3', model: 'Sonnet 5.5 → Opus 5.5 調整',
  concept: '高空に架かる橋。眼下に夜の未来都市、大通りは奥の巨大な真円のゲート（虚空の門）へ収束する。ゲートの外周を時計盤のような目盛り環が回り、左右にはステージと平行に神殿が並ぶ。',
  palette: '深い紺〜群青の地。灯りは彩度を抑えた冷たいシアンと淡い金（r2 より数を減らした）。構造線は #6f93b8 / 金 #b39a64。ゲートは黒い円盤＋シアンの縁と同心リング、外周に金の目盛り環。',
  motion: '外周の目盛り環がゆっくり回る（主環は約3分で1周、外側の金環は逆向き）と主環を周回する光点3つ／大通りを光の粒が奥へ流れる／神殿の列柱を下から上へ光がなぞる／梁・棟を光が奥へ走る／浮遊足場の上下／光の粒の上昇。ゲート本体（円盤・縁・同心リング）は動かない。点滅なし。',
  perf: '全画面板1（都市）、ゲート板1（円の外接矩形のみ）、線メッシュ3、粒子1（400点）。draw 6。暗幕なし。',
  unityCost: '都市板・ゲート板は Unlit シェーダー各1本。ゲートの円は「中心 NDC(0,0.9)、半径 = 0.23×aspect（縦単位）」の画面上の真円なので C# 側の計算は不要。線は shared/lines.js、粒子は Points→クアッド粒子に置換。',
  clearColor: '#03050d',
  shade: { color: '#02030a', strength: 0 },
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
    // r1 のゲートの寸法: 中心 NDC(0, 0.9)、半径は画面横 0.23（= 縦単位で 0.23×aspect）。上部は画面外にはみ出す
    const gate = { cx: 0, cy: 0.9, r: 0.23 * aspect };
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
            vec3 L = lights(p, 4.0, px / 4.0, 0.35, uTime) * 0.6;   // r3: 数と明るさを減らす
            L += lights(p + 31.7, 1.5, px / 1.5, 0.22, uTime + 5.0) * 0.3;
            // 街路の碁盤（ごく淡い）
            vec2 gq = abs(fract(p / 12.0 + 0.5) - 0.5) * 12.0;
            float gl = 1.0 - smoothstep(0.0, max(px, 0.12) * 1.5, min(gq.x, gq.y));
            L += vec3(0.03, 0.06, 0.10) * gl * (1.0 - smoothstep(0.15, 0.6, px)); // r3: 碁盤を弱める
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
            col += vec3(0.16, 0.30, 0.42) * (road * roadFade * 0.32 * dash + ringLine * ringFade * 0.10) * (1.0 - 0.35 * haze); // r3: 道路を弱める
            // 眼下の深さ感: 画面下ほどわずかに明るい紺
            col += vec3(0.004, 0.010, 0.026) * smoothstep(0.2, -1.0, ndc.y);
          }
          // ゲートの外側すぐは少しだけ沈める（縁を浮かせる）
          float gd = museGateDist(ndc);
          col *= mix(0.35, 1.0, smoothstep(0.0, 0.10, gd)); // ゲートの縁の外側に薄い影
          gl_FragColor = vec4(col, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    });
    const city = new THREE.Mesh(qg, cityMat);
    city.frustumCulled = false; city.renderOrder = -100;
    root.add(city);
    disposables.push(qg, cityMat);

    // ---------- 虚空のゲート（r1 のデザインを画面上の真円で。スクリーン空間の板1枚） ----------
    // 円盤（不透明の黒）＋縁（シアン、外側に淡い2本目）＋奥へ続く同心リング3本（動かない）
    // ＋外周の時計盤（r2 で左右に浮いていたリングのデザイン: 主環・内環・金の外環・36本の目盛り、光点3つ）がゆっくり回る
    const ext = 1.55; // 外周の時計盤まで覆う
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
        // 半径 r の円の線（幅 w は縦単位）。fw でアンチエイリアス
        float ring(float rad, float r, float w, float fw) { return 1.0 - smoothstep(w * 0.5, w * 0.5 + fw, abs(rad - r)); }
        void main() {
          vec2 ndc = museBaseNdc();
          vec2 q = (ndc - uGate.xy) * vec2(uGate.w, 1.0);
          float R = uGate.z;
          float rad = length(q), ang = atan(q.y, q.x);
          float fw = fwidth(rad);
          float w = 2.0 / 1668.0 * 1.6;                     // 線幅（iPad 11" 換算 1.6px）
          float disc = 1.0 - smoothstep(-fw, fw, rad - R);
          float quiet = 1.0 - 0.8 * museSpawnMask(ndc);     // ノーツ出現位置の中の線は控えめに
          vec3 cyanC = vec3(0.42, 0.62, 0.80), goldC = vec3(0.70, 0.60, 0.38);
          vec3 glow = vec3(0.0);
          // --- r1 のゲート（動かない）: 縁 0.9、外側の2本目 0.35、奥の同心リング 0.28/k（r1 の線の不透明度 0.6 を掛けた値）
          glow += cyanC * 0.54 * ring(rad, R, w, fw);
          glow += cyanC * 0.21 * ring(rad, R * 1.06, w, fw);
          for (int k = 1; k <= 3; k++) glow += cyanC * (0.17 / float(k)) * ring(rad, R * (1.0 - 0.22 * float(k)), w, fw) * quiet;
          // --- 外周の時計盤（r2 の左右のリングの比率: 主環15 / 内環13.6 / 金環19 / 目盛り 15→16, 長 15→17.4）
          float Rm = R * 1.16, s = Rm / 15.0;
          float spin = uTime * 0.035;                        // 主環（目盛りごと）: 約3分で1周
          float tk = fract((ang + spin) / 6.28318) * 36.0;   // 36本
          float tIdx = floor(tk + 0.5);
          float tick = 1.0 - smoothstep(w * 0.5, w * 0.5 + fw, abs(tk - tIdx) / 36.0 * 6.28318 * rad);
          float longT = step(mod(tIdx, 3.0), 0.5);
          float tickLen = mix(1.0, 2.4, longT) * s;
          float inTick = step(Rm, rad) * (1.0 - step(Rm + tickLen, rad));
          glow += goldC * mix(0.24, 0.42, longT) * tick * inTick;
          glow += cyanC * 0.45 * ring(rad, Rm, w, fw);
          glow += cyanC * 0.22 * ring(rad, 13.6 * s, w, fw);
          // 金の外環: 逆向きにゆっくり回る破線（12分割）
          float dashA = fract((ang - uTime * 0.02) / 6.28318 * 12.0);
          glow += goldC * 0.26 * ring(rad, 19.0 * s, w, fw) * step(0.18, dashA);
          // 主環を周回する光点3つ（約60秒）
          float bead = abs(fract((ang / 6.28318 - uTime * 0.016) * 3.0) - 0.5) / 3.0 * 6.28318 * Rm;
          glow += vec3(0.75, 0.92, 1.0) * 0.7 * exp(-bead * bead / (0.006 * 0.006)) * ring(rad, Rm, w * 3.0, fw);
          vec3 dark = vec3(0.0003, 0.0006, 0.0024);           // r1 の円盤 #010208（sRGB）の linear 値
          float a = disc;
          gl_FragColor = vec4(dark * a + glow, a);
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
      temple(TK, [sgn * 24.7, -10, -26], 1.1);   // 手前の大きな神殿（r3: 手前へ。画面外にはみ出してよい）
      temple(TK, [sgn * 27, -12, -66], 1.3);    // 奥の神殿（r3: ゲートの外に出るよう手前へ）
    }
    const templeMesh = buildLines(THREE, ctx, TK.segs, {
      widthPx: 2.2, opacity: 0.9,
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
      plat(15, -2, -44, 6, 5);
      plat(46, -6, -52, 8, 6);
      const m = buildLines(THREE, ctx, K.segs, { widthPx: 1.6, opacity: 0.55 });
      m.renderOrder = -72;
      root.add(m);
      disposables.push(m.geometry, m.material);
      platforms.push({ m, sgn, phase: sgn > 0 ? 0 : Math.PI });
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
      },
      dispose() { disposables.forEach((o) => o.dispose && o.dispose()); },
    };
  },
};
