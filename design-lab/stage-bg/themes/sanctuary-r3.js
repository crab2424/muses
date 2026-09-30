// 天上の聖域 r3: r2（オブジェクトの種類・配色はそのまま）を軌道都市 r3 と同程度の細かさまで描き込んだ版。共通の暗幕なし。
// 描き込み:
//  浮島 … 下の岩を旋盤状の自作メッシュにして棚（地層のせり出し）・鍾乳石を追加、上面との境の暗い帯、下へ青紫へ落ちる階調、
//        地層の筋・縦の割れ目（画面上で一定幅の線）、上面の縁の明るい輪郭＋内側の2本目、苔・草・乾いた土の斑、
//        遺構は2段の基壇・柱（土台・縦溝・柱頭）・崩れた2分割のまぐさ・破風の断片・倒れた柱の胴・瓦礫
//  金の光輪 … 内側の細い2本目の輪、72本の目盛り（12本ごとに長い）、外側の破線環、主環を周回する光点3つ（それぞれゆっくり回る）
//  モノリス … 縁の光を3重（琥珀の主帯・桃色の外の細線・薄紫の内の細線）、内面にごく淡い入れ子の山形と横桟の刻線（暗部内は消える）、縁の光がゆっくり波打つ
//  ガラス片 … 6角の柱状の双錐に作り直して稜線のハイライト・屈折の縞を追加
//  雲海 … 雲の縁のリムライト（琥珀白）、雲の縁のすぐ外側の影（層の間の影）、雲の層の筋（n の等高線）、ステージ脇の滑らかな所に出る雲海の波紋（世界座標の細い筋2系統）、
//        浮島の影（雲の上に落ちる）。ノイズ回数は r2 と同じ（4オクターブ×4回）。追加は fwidth 3回と exp 6回だけ
//  光芒 … 細い筋
// draw: 雲海1 / 浮島1 / 浮島の線1 / ガラス1 / 金の輪1 / 輪の線1 / 光芒1 / モノリス1 = 8（暗幕なし）
import { buildLines, lineKit } from '../shared/lines.js';

export default {
  id: 'sanctuary-r3', name: '天上の聖域 r3', model: 'Sonnet 5.5',
  concept: '朝焼けと薄暮の色が層になった雲海の上に、大理石の遺構をのせた浮島が浮かぶ。島の下は青紫に沈む岩肌と鍾乳石、雲海に島の影が落ちる。ガラスの破片が稜線で光を返し、金の光輪は目盛りを刻んで空を巡る。奥（消失点）だけに黒い逆三角のモノリスが抜ける。',
  palette: '雲 白〜淡い桃 #ffd0bd / 薄紫 #cdbdf2 / 琥珀 #ffdca0、影は青紫 #6c6eb4、ステージ脇は沈めた青紫 #4a5490。光の輪 金 #ffd27a。ガラスは淡い虹色（彩度低）。岩は暖かい藤色から下へ青紫 #2b2b66。ノーツ色の飽和色は使わない。（r2 と同じ配色）',
  motion: '雲は右へゆっくり流れ、色の層もゆっくり移ろう（雲の影・リム・波紋の筋も一緒に動く）。浮島・ガラスは周期10〜20秒で上下・ゆっくり自転（島の線も追従）。金の輪は微小な揺れと自転、目盛りは順方向・外側の破線は逆方向にゆっくり回り、光点3つが周回する。光芒は明るさが±10%ゆらぐ。モノリスの縁の光がごくゆっくり波打つ。点滅なし。',
  perf: 'draw 8（雲海の全画面板1 / 浮島1 / 浮島の線1 / ガラス1 / 金の輪1 / 輪の線1 / 光芒1 / モノリス1）。暗幕なし。粒子なし。雲のノイズは r2 と同じ4オクターブ×4回（雲の縁・影・等高線・波紋・島の影は既に得た n の再利用。追加は fwidth 3回＋島6個の exp）。浮島の頂点数は r2 の約3倍（岩を細かくしたため）だがインスタンス6つで軽い。動きは頂点シェーダー（CPU更新なし）。',
  unityCost: '中。雲海シェーダー1枚＋Instanced メッシュ（浮島・ガラス・輪）＋Instanced の線メッシュ2（島・輪。shared/lines と同じ帯メッシュに、島の上下・自転を入れる頂点変換を足したもの）＋加算板＋モノリスのメッシュ。全部 Unlit、テクスチャ不要。',
  clearColor: '#8fb0d8',
  shade: { color: '#0a1020', strength: 0 },
  stage: {
    groundFill: '#232848', groundFillAlpha: 1, groundLine: '#e8e4ff', groundLineAlpha: 0.38, groundJudge: '#ffffff',
    skyFill: '#3f4878', skyFillAlpha: 0.22, skyLine: '#e8e4ff', skyLineAlpha: 0.34, skyJudge: '#ffffff',
  },
  create(ctx) {
    const { THREE, zone, glsl, d, camera, laneX } = ctx;
    const root = new THREE.Group();
    const CAM_Y = 8, CLOUD_Y = -9;
    const disposables = [];

    // ステージ半幅の線形近似（地上、ワールド）: half(z) = a + b*z
    const zA = d.zJudge, zB = d.zFar;
    const hA = laneX(1, 0, zA), hB = laneX(1, 0, zB);
    const hb = (hB - hA) / (zB - zA), ha = hA - hb * zA;

    // 浮島の影を雲海へ落とすため、島の配置は先に決めておく（乱数の呼び出し順は r2 と同じ）
    const uIsl = { value: Array.from({ length: 6 }, () => new THREE.Vector4()) };

    // ---------- 1) 雲海（全画面の板1枚） ----------
    const qg = new THREE.BufferGeometry();
    qg.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3));
    qg.setIndex([0, 1, 2, 0, 2, 3]);
    const cloud = new THREE.Mesh(qg, new THREE.ShaderMaterial({
      uniforms: {
        ...zone.uniforms, uTime: { value: 0 },
        uInvProj: { value: camera.projectionMatrixInverse.clone() },
        uCamRot: { value: new THREE.Matrix3().setFromMatrix4(camera.matrixWorld) },
        uStageHalf: { value: new THREE.Vector2(ha, hb) },
        uIsl,
      },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime; uniform mat4 uInvProj; uniform mat3 uCamRot; uniform vec2 uStageHalf; uniform vec4 uIsl[6];
        float h21(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
        float vn(vec2 p) {
          vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
          return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
        }
        float fbm(vec2 p) {
          float a = 0.5, s = 0.0;
          for (int i = 0; i < 4; i++) { s += a * vn(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; }
          return s;
        }
        void main() {
          vec2 ndc = museBaseNdc();
          vec4 vp = uInvProj * vec4(ndc, 1.0, 1.0);
          vec3 dir = normalize(uCamRot * (vp.xyz / vp.w));
          vec3 hazeB = vec3(0.70, 0.78, 0.96), hazeW = vec3(0.98, 0.76, 0.74);
          vec3 c;
          // 色の層: 大きな尺度の低周波ノイズがゆっくり移ろう（桃・薄紫・琥珀）
          float band = vn(dir.xz * (-8.0 / min(dir.y, -0.02)) * 0.022 + vec2(uTime * 0.0025, 3.0 - uTime * 0.0015));
          float band2 = vn(dir.xz * (-8.0 / min(dir.y, -0.02)) * 0.05 + vec2(9.0, uTime * 0.003));
          vec3 haze = mix(hazeB, hazeW, smoothstep(0.35, 0.75, band));
          if (dir.y > -0.02) {
            c = mix(haze, vec3(0.55, 0.72, 0.93), clamp(dir.y * 6.0 + 0.2, 0.0, 1.0));
          } else {
            float t = (${CLOUD_Y.toFixed(1)} - ${CAM_Y.toFixed(1)}) / dir.y;
            vec2 wp = dir.xz * t;
            vec2 q = wp * 0.028 + vec2(uTime * 0.010, uTime * 0.004);
            float n = fbm(q + fbm(q * 0.6 + 3.1) * 0.9);
            float dens = smoothstep(0.38, 0.68, n);
            float n2 = fbm(q + vec2(0.0, -0.05) + fbm(q * 0.6 + 3.1) * 0.9);
            float lit = clamp(0.5 + (n - n2) * 14.0, 0.0, 1.0);
            // 淡い色の層: 遠いほど暖色（桃）、近いほど薄紫。琥珀は明るい縁だけ
            float far = smoothstep(60.0, 300.0, t);
            vec3 peach = vec3(1.0, 0.72, 0.66), lav = vec3(0.74, 0.68, 0.96), amber = vec3(1.0, 0.82, 0.52);
            vec3 tint = mix(lav, peach, clamp(band * 1.5 + far * 0.6 - 0.45, 0.0, 1.0));
            tint = mix(tint, amber, smoothstep(0.55, 0.9, band2) * 0.7);
            vec3 cWhite = mix(vec3(0.98, 0.98, 1.0), tint, 0.9);
            vec3 cShade = mix(vec3(0.50, 0.60, 0.84), vec3(0.47, 0.44, 0.76), band);      // 影は青紫寄り
            vec3 cGap = mix(vec3(0.36, 0.50, 0.82), vec3(0.44, 0.38, 0.72), smoothstep(0.3, 0.8, band2));
            c = mix(cGap, mix(cShade, cWhite, lit * 0.75 + 0.25), dens);
            // r3: 層の間の影 = 雲の縁のすぐ外側（隙間側）を青紫に沈める。追加のノイズ呼び出しなし（n を再利用）
            float ao = smoothstep(0.18, 0.40, n) * (1.0 - dens);
            c = mix(c, c * vec3(0.62, 0.62, 0.84), ao * 0.75);
            // r3: 雲の縁のリムライト（琥珀白）。光が当たる側（lit）で強く
            float rim = smoothstep(0.44, 0.52, n) * (1.0 - smoothstep(0.52, 0.64, n));
            c += mix(vec3(1.0, 0.90, 0.80), tint, 0.25) * rim * (0.22 + 0.55 * lit) * 0.46;
            // r3: 雲の層の筋（n の等高線を細く。近すぎて筋が太る所は fade で消す）
            float nl = n * 8.0, isoW = fwidth(nl);
            float isoL = (1.0 - smoothstep(0.0, isoW * 1.3 + 0.015, abs(fract(nl + 0.5) - 0.5))) * (1.0 - smoothstep(0.35, 0.9, isoW));
            c = mix(c, mix(vec3(1.0), tint, 0.4) * (0.9 + 0.2 * lit), isoL * 0.30 * smoothstep(0.30, 0.6, dens + 0.15) * (0.4 + 0.6 * lit));
            // r3: 浮島の影。島から光の向きへずらした位置に、高さに応じてぼける
            float shd = 0.0;
            for (int i = 0; i < 6; i++) { vec2 dd = wp - uIsl[i].xy; shd += exp(-dot(dd, dd) / uIsl[i].z); }
            c = mix(c, vec3(0.34, 0.34, 0.62), min(shd, 1.0) * 0.30);
            float fade = 1.0 - exp(-t * 0.0045);
            c = mix(c, haze, clamp(fade * 1.15, 0.0, 1.0));
            // ステージの左右すぐ脇を青紫に沈める
            float t0 = -${CAM_Y.toFixed(1)} / dir.y;
            vec2 p0 = dir.xz * t0;
            float hw0 = uStageHalf.x + uStageHalf.y * (-p0.y);
            float dx = max(abs(p0.x) - hw0, 0.0);
            float sink = exp(-dx / (3.5 + 0.07 * t0));
            c = mix(c, vec3(0.26, 0.30, 0.52), 0.62 * sink);
            // r3: 雲海の波紋（世界座標 z 方向に等間隔で、雲の密度でゆらぐ細い筋。ステージ脇の滑らかな所にも細部を出す）。
            // 明るい筋のすぐ下に暗い筋を添えて段差に見せる。遠くで密になる所は fwidth で消える
            float wl = wp.y * 0.30 + n * 3.5 + sin(wp.x * 0.11 + n * 5.0) * 0.35 - uTime * 0.012;
            float wW = fwidth(wl), wFade = 1.0 - smoothstep(0.25, 0.7, wW);
            float wHi = (1.0 - smoothstep(0.0, wW * 1.3 + 0.014, abs(fract(wl + 0.5) - 0.5))) * wFade;
            float wLo = (1.0 - smoothstep(0.0, wW * 1.3 + 0.014, abs(fract(wl + 0.5 - 0.07) - 0.5))) * wFade;
            c = mix(c, vec3(0.94, 0.92, 1.0), wHi * 0.20 * (0.5 + 0.5 * lit));
            c = mix(c, vec3(0.30, 0.32, 0.60), wLo * 0.16);
            float wl2 = wp.y * 0.93 + n * 8.0 + sin(wp.x * 0.23 + n * 9.0) * 0.4 - uTime * 0.02;
            float wW2 = fwidth(wl2);
            float wHi2 = (1.0 - smoothstep(0.0, wW2 * 1.3 + 0.02, abs(fract(wl2 + 0.5) - 0.5))) * (1.0 - smoothstep(0.25, 0.6, wW2));
            c = mix(c, vec3(0.94, 0.92, 1.0), wHi2 * 0.11 * (0.4 + 0.6 * lit));
          }
          // 消失点まわりを暗い霞に（モノリスが立つ奥）
          vec2 e = (ndc - vec2(0.0, 0.93)) / vec2(0.42, 0.34);
          float hole = 1.0 - smoothstep(0.0, 1.0, length(e));
          c = mix(c, vec3(0.045, 0.06, 0.12), 0.7 * hole);
          c *= 1.0 - 0.6 * museSpawnMask(ndc);
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    }));
    cloud.frustumCulled = false; cloud.renderOrder = -100;
    root.add(cloud);
    disposables.push(qg, cloud.material);

    // ---------- 共通: 実時間ユニフォーム ----------
    const uTime = { value: 0 };

    // 決定的な擬似乱数（配置用。r2 と呼び出し順・回数を変えない）
    let rs = 12345; const rnd = () => (rs = (rs * 1664525 + 1013904223) >>> 0) / 4294967296;
    // 細部（割れ目など）用の別系列。配置の乱数列に影響させない
    let rs2 = 777; const rnd2 = () => (rs2 = (rs2 * 1664525 + 1013904223) >>> 0) / 4294967296;
    const m4 = new THREE.Matrix4(), qt = new THREE.Quaternion(), eu = new THREE.Euler(), v3 = new THREE.Vector3(), sc = new THREE.Vector3();
    // aSeed: x=位相 y=周期係数 z=自転係数 w=明るさ
    function inst(geom, mat, items) {
      const mesh = new THREE.InstancedMesh(geom, mat, items.length);
      const seed = new Float32Array(items.length * 4);
      items.forEach((it, i) => {
        eu.set(it.rx || 0, it.ry || 0, it.rz || 0); qt.setFromEuler(eu);
        v3.set(it.x, it.y, it.z); sc.set(it.sx, it.sy, it.sz);
        m4.compose(v3, qt, sc); mesh.setMatrixAt(i, m4);
        seed.set([rnd(), rnd(), (rnd() - 0.5) * 2, rnd()], i * 4);
      });
      geom.setAttribute('aSeed', new THREE.InstancedBufferAttribute(seed, 4));
      mesh.frustumCulled = false;
      return mesh;
    }
    // 浮遊の頂点シェーダー（uSpin=自転の速さ、uBob=上下の振幅）。pre = 自転より前にローカルで p, n を動かす差し込み
    const mkVs = (extra) => /* glsl */ `
      attribute vec4 aSeed; uniform float uTime, uSpin, uBob;
      ${extra.attr || ''}
      varying vec3 vN; varying float vDist; varying float vB; varying vec3 vView; varying vec3 vLocal; ${extra.vary || ''}
      void main() {
        float ph = aSeed.x * 6.2831 + uTime * (0.30 + aSeed.y * 0.22);
        float a = uTime * uSpin * aSeed.z + aSeed.x * 6.2831;
        float ca = cos(a), sa = sin(a);
        vec3 p = position, n = normal;
        ${extra.pre || ''}
        vLocal = p;
        p = vec3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z);
        n = vec3(ca * n.x + sa * n.z, n.y, -sa * n.x + ca * n.z);
        vec4 wp = modelMatrix * instanceMatrix * vec4(p, 1.0);
        wp.y += sin(ph) * uBob;
        wp.x += cos(ph * 0.7) * 0.25;
        vN = normalize(mat3(modelMatrix * instanceMatrix) * n);
        vDist = length(wp.xyz - cameraPosition);
        vView = normalize(cameraPosition - wp.xyz);
        vB = aSeed.w;
        ${extra.body || ''}
        gl_Position = projectionMatrix * viewMatrix * wp;
      }`;
    const mkMat = (vs, fragBody, uni, blendOpts) => new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime, ...uni },
      vertexShader: vs,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        varying vec3 vN; varying float vDist; varying float vB; varying vec3 vView; varying vec3 vLocal; uniform float uTime; ${blendOpts.fragDecl || ''}
        void main() {
          ${fragBody}
          c *= 1.0 - 0.9 * museSpawnMask(museBaseNdc());
          gl_FragColor = vec4(c, al);
          #include <colorspace_fragment>
        }`,
      transparent: !!blendOpts.transparent, depthWrite: !blendOpts.transparent,
      blending: blendOpts.additive ? THREE.AdditiveBlending : THREE.NormalBlending,
    });

    // 島・輪に追従する線メッシュ。shared/lines.js の帯メッシュを InstancedMesh にし、頂点シェーダーへ mkVs と同じ変換
    // （自転→instanceMatrix→上下）を差し込む。uRotOn>0 のとき、線分の p が 1/2 なら輪の面内で回す（目盛りの回転）。
    // uBias = カメラ側へずらす量（島のスケール倍）。深度テストして島の陰・重なりで隠れるようにする。
    function instLines(baseMesh, segs, opts, uni) {
      const m = buildLines(THREE, ctx, segs, { ...opts, uniforms: uni });
      let vs = m.material.vertexShader, fs = m.material.fragmentShader;
      const rep = (s, a, b) => { if (!s.includes(a)) throw new Error('instLines: shared/lines.js の形が変わった: ' + a); return s.replace(a, b); };
      vs = rep(vs, 'void main() {', /* glsl */ `
        attribute vec4 aSeed; uniform float uTime;
        vec3 xf(vec3 p) {
          float ph = aSeed.x * 6.2831 + uTime * (0.30 + aSeed.y * 0.22);
          float a = uTime * uSpin * aSeed.z + aSeed.x * 6.2831;
          if (uRotOn > 0.5 && aP > 0.5) {
            float th = uTime * uRotSpd * (aP < 1.5 ? 1.0 : -0.6);
            float cz = cos(th), sz = sin(th);
            p.xy = vec2(cz * p.x - sz * p.y, sz * p.x + cz * p.y);
          }
          float ca = cos(a), sa = sin(a);
          p = vec3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z);
          vec4 wp = modelMatrix * instanceMatrix * vec4(p, 1.0);
          wp.y += sin(ph) * uBob;
          wp.x += cos(ph * 0.7) * 0.25;
          wp.xyz += normalize(cameraPosition - wp.xyz) * uBias * length(instanceMatrix[0].xyz);
          return wp.xyz;
        }
        void main() {`);
      vs = rep(vs, 'vec4 ca = projectionMatrix * modelViewMatrix * vec4(position, 1.0);', 'vec3 wa = xf(position); vec3 wb = xf(aB); vec4 ca = projectionMatrix * viewMatrix * vec4(wa, 1.0);');
      vs = rep(vs, 'vec4 cb = projectionMatrix * modelViewMatrix * vec4(aB, 1.0);', 'vec4 cb = projectionMatrix * viewMatrix * vec4(wb, 1.0);');
      vs = rep(vs, 'vW = (modelMatrix * vec4(aS.x < 0.5 ? position : aB, 1.0)).xyz;', 'vW = aS.x < 0.5 ? wa : wb;');
      // xf が使う uniform（uSpin/uBob/uBias/uRotOn/uRotSpd）は opts.uniforms 経由で fragment 側にも宣言されるので vertex では宣言しない…
      // ただし vertex には無いので、ここで宣言する（同名・同型なので link は通る）
      vs = 'uniform float uSpin, uBob, uBias, uRotOn, uRotSpd;\n' + vs;
      m.material.vertexShader = vs;
      m.material.needsUpdate = true;
      const lm = new THREE.InstancedMesh(m.geometry, m.material, baseMesh.count);
      lm.instanceMatrix = baseMesh.instanceMatrix;
      m.geometry.setAttribute('aSeed', baseMesh.geometry.getAttribute('aSeed'));
      m.material.uniforms.uTime = uTime;
      lm.frustumCulled = false;
      disposables.push(m.geometry, m.material);
      return lm;
    }

    // ---------- 2) 浮島 ----------
    // aKind: 0=岩 1=上面・縁の土 2=大理石 3=基壇の石
    function mergeKinds(parts) {
      const P = [], N = [], K = [];
      for (const { g, kind, m } of parts) {
        const ng = g.index ? g.toNonIndexed() : g.clone();
        ng.applyMatrix4(m);
        ng.computeVertexNormals();
        const pa = ng.attributes.position.array, na = ng.attributes.normal.array;
        for (let i = 0; i < pa.length; i++) { P.push(pa[i]); N.push(na[i]); }
        for (let i = 0; i < pa.length / 3; i++) K.push(kind);
      }
      const out = new THREE.BufferGeometry();
      out.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      out.setAttribute('normal', new THREE.Float32BufferAttribute(N, 3));
      out.setAttribute('aKind', new THREE.Float32BufferAttribute(K, 1));
      return out;
    }
    const T = (x, y, z, sx = 1, sy = 1, sz = 1, ry = 0, rz = 0, rx = 0) =>
      new THREE.Matrix4().compose(new THREE.Vector3(x, y, z), new THREE.Quaternion().setFromEuler(new THREE.Euler(rx, ry, rz)), new THREE.Vector3(sx, sy, sz));

    // 岩の形（旋盤状）。a=方位, t=0(縁の直下)〜1(先端)。棚（地層のせり出し）とゆがみ。メッシュと線で同じ式を使う
    const ROCK = { top: -0.08, len: 1.5, R: 0.945 };
    const rockR = (a, t) => {
      const k = Math.min(1, t * 5);
      const shelf = 0.05 * Math.sin(t * 20 + Math.sin(a * 2.0) * 1.5);
      const bump = 0.09 * Math.sin(a * 3 + t * 4) + 0.05 * Math.sin(a * 7 - t * 6) + 0.03 * Math.sin(a * 13 + t * 9);
      return ROCK.R * Math.pow(1 - t, 0.85) * (1 + (shelf + bump) * k);
    };
    const rockPt = (a, t, o = 1) => { const r = rockR(a, t) * o; return [r * Math.cos(a) + 0.12 * t * t, ROCK.top - ROCK.len * t, r * Math.sin(a)]; };
    const rockGeom = (nA, nT) => {
      const P = [];
      const pt = (i, j) => rockPt((i / nA) * Math.PI * 2, j / nT);
      for (let j = 0; j < nT; j++) for (let i = 0; i < nA; i++) {
        const a = pt(i, j), b = pt(i + 1, j), c = pt(i, j + 1), e = pt(i + 1, j + 1);
        P.push(...a, ...b, ...c, ...b, ...e, ...c);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      return g;
    };
    // 円柱系の頂点角（CylinderGeometry と同じ x=sin, z=cos）
    const cylPt = (cx, cz, r, y, k, n) => { const th = (k / n) * Math.PI * 2; return [cx + r * Math.sin(th), y, cz + r * Math.cos(th)]; };

    const lk = lineKit();
    // 線の色（リニア寄りの値。フラグメントで表示側へ変換して混ぜる）と不透明度は p に入れる
    const C_DARK = [0.05, 0.04, 0.17], C_INK = [0.18, 0.16, 0.42], C_LITE = [1.0, 0.97, 0.84], C_WARM = [0.62, 0.52, 0.80], C_GRN = [0.80, 0.98, 0.78];
    const ln = (a, b, c, alpha) => lk.seg(a, b, c, 1, alpha);
    const poly = (pts, c, alpha, closed = false) => { for (let i = 0; i < pts.length - 1; i++) ln(pts[i], pts[i + 1], c, alpha); if (closed) ln(pts[pts.length - 1], pts[0], c, alpha); };

    const islandGeom = (() => {
      const parts = [];
      const add = (g, kind, m = T(0, 0, 0)) => parts.push({ g, kind, m });
      const boxPart = (w, h, dd, kind, m, aT = 0.6, aO = 0.5) => {
        add(new THREE.BoxGeometry(w, h, dd), kind, m);
        const hx = w / 2, hy = h / 2, hz = dd / 2;
        const V = [[-hx, -hy, -hz], [hx, -hy, -hz], [hx, hy, -hz], [-hx, hy, -hz], [-hx, -hy, hz], [hx, -hy, hz], [hx, hy, hz], [-hx, hy, hz]]
          .map((v) => new THREE.Vector3(...v).applyMatrix4(m).toArray());
        [[2, 3], [6, 7], [2, 6], [3, 7]].forEach(([i, j]) => ln(V[i], V[j], C_LITE, aT));
        [[0, 1], [4, 5], [0, 4], [1, 5], [0, 3], [1, 2], [4, 7], [5, 6]].forEach(([i, j]) => ln(V[i], V[j], C_INK, aO));
      };

      // --- 上面（円盤、28角）と縁の線
      const NR = 28, y0 = 0.08;
      add(new THREE.CylinderGeometry(1.0, 0.94, 0.16, NR, 1), 1);
      poly(Array.from({ length: NR }, (_, k) => cylPt(0, 0, 0.998, y0 + 0.002, k, NR)), C_LITE, 0.85, true);   // 縁の明るい輪郭
      poly(Array.from({ length: NR }, (_, k) => cylPt(0, 0, 0.90, y0 + 0.002, k, NR)), C_GRN, 0.30, true);     // 内側の2本目
      poly(Array.from({ length: NR }, (_, k) => cylPt(0, 0, 0.945, -0.078, k, NR)), C_DARK, 0.55, true);       // 縁の下の暗い線

      // --- 岩
      add(rockGeom(24, 14), 0);
      // 鍾乳石（岩の下部から垂れる細い錐）。[方位, t, 長さ, 半径]
      [[0.9, 0.52, 0.55, 0.11], [2.4, 0.44, 0.5, 0.10], [3.6, 0.60, 0.6, 0.09], [5.0, 0.48, 0.45, 0.10], [5.9, 0.66, 0.42, 0.07], [1.6, 0.34, 0.36, 0.08], [4.3, 0.36, 0.4, 0.075]]
        .forEach(([a, t, len, rad]) => {
          const p = rockPt(a, t, 0.96);
          add(new THREE.ConeGeometry(rad, len, 5, 1, true).rotateX(Math.PI), 0, T(p[0], p[1] - len / 2 + 0.04, p[2]));
        });
      add(new THREE.ConeGeometry(0.22, 0.85, 6, 1, true).rotateX(Math.PI), 0, T(0.30, -1.02, 0.20));

      // 縁から垂れる草の蔓（細い緑の筋）
      for (let i = 0; i < 26; i++) {
        const a = (i / 26) * Math.PI * 2 + (rnd2() - 0.5) * 0.18, len = 0.04 + rnd2() * 0.11;
        const pt = (r, y) => [r * Math.cos(a), y, r * Math.sin(a)];
        const pts = [pt(0.985, 0.03), pt(0.975, -0.03), pt(0.965, -0.075)];
        for (let s = 1; s <= 3; s++) { pts.push(rockPt(a + Math.sin(s * 2.1 + i) * 0.02, len * (s / 3), 1.012)); }
        poly(pts, [0.28, 0.52, 0.34], 0.36);
      }

      // 岩の線: 地層の筋（途切れ途切れ）と縦の割れ目
      [0.12, 0.26, 0.42, 0.60, 0.76].forEach((t, li) => {
        const nS = 36; let on = rnd2() < 0.7;
        for (let i = 0; i < nS; i++) {
          if (rnd2() < 0.16) on = !on;
          if (!on) continue;
          const a0 = (i / nS) * Math.PI * 2, a1 = ((i + 1) / nS) * Math.PI * 2;
          ln(rockPt(a0, t, 1.008), rockPt(a1, t, 1.008), C_DARK, 0.30);
          ln(rockPt(a0, t - 0.014, 1.008), rockPt(a1, t - 0.014, 1.008), C_WARM, li % 2 === 0 ? 0.24 : 0.14);
        }
      });
      for (let c = 0; c < 10; c++) {
        let a = rnd2() * Math.PI * 2, t = 0.03 + rnd2() * 0.12;
        const len = 0.22 + rnd2() * 0.45, steps = 5;
        let prev = rockPt(a, t, 1.01);
        for (let s = 1; s <= steps; s++) {
          a += (rnd2() - 0.5) * 0.11; t += len / steps;
          const cur = rockPt(a, Math.min(t, 0.95), 1.01);
          ln(prev, cur, C_DARK, 0.75 * (1 - s / (steps + 1)) + 0.15);
          prev = cur;
        }
      }

      // --- 遺構: 2段の基壇 / 柱 / 崩れたまぐさ / 破風の断片 / 倒れた柱の胴 / 瓦礫
      boxPart(1.16, 0.05, 0.52, 3, T(-0.02, y0 + 0.025, 0.02), 0.55, 0.45);
      boxPart(1.02, 0.05, 0.40, 3, T(-0.02, y0 + 0.075, 0.02), 0.6, 0.5);
      for (let i = -3; i <= 3; i++) ln([-0.02 + i * 0.145, y0 + 0.1005, -0.18], [-0.02 + i * 0.145, y0 + 0.1005, 0.22], C_INK, 0.22);   // 基壇の目地
      ln([-0.5, y0 + 0.1005, 0.02], [0.46, y0 + 0.1005, 0.02], C_INK, 0.18);
      const yc = y0 + 0.10;
      // 柱: 土台（円錐台）・胴（10角、上へ細く）・柱頭（エキヌスと平板）
      const col = (x, z, h, r = 0.05, capital = true) => {
        const NC = 10;
        add(new THREE.CylinderGeometry(r * 1.35, r * 1.5, 0.03, NC, 1), 2, T(x, yc + 0.015, z));
        add(new THREE.CylinderGeometry(r * 0.82, r, h, NC, 1), 2, T(x, yc + 0.03 + h / 2, z));
        const yBase = yc + 0.03, yTop = yBase + h;
        poly(Array.from({ length: NC }, (_, k) => cylPt(x, z, r * 1.35, yc + 0.031, k, NC)), C_INK, 0.5, true);   // 土台の上縁
        poly(Array.from({ length: NC }, (_, k) => cylPt(x, z, r * 1.5, yc + 0.001, k, NC)), C_INK, 0.35, true);   // 土台の下縁
        for (let k = 0; k < NC; k++) ln(cylPt(x, z, r * 1.002, yBase + 0.005, k, NC), cylPt(x, z, r * 0.83, yTop - 0.005, k, NC), C_INK, 0.30); // 縦溝（稜線）
        if (capital) {
          add(new THREE.CylinderGeometry(r * 1.32, r * 0.85, 0.03, NC, 1), 2, T(x, yTop + 0.015, z));
          poly(Array.from({ length: NC }, (_, k) => cylPt(x, z, r * 0.83, yTop + 0.001, k, NC)), C_DARK, 0.45, true);   // 胴と柱頭の境
          boxPart(r * 3.0, 0.03, r * 3.0, 2, T(x, yTop + 0.045, z), 0.7, 0.45);
        } else {
          // 折れた柱: 上端を斜めの割れ口に（明るい縁）
          poly(Array.from({ length: NC }, (_, k) => cylPt(x, z, r * 0.83, yTop, k, NC)), C_LITE, 0.6, true);
        }
        return yTop + 0.06;
      };
      const yTop = col(-0.42, 0.02, 0.5); col(-0.14, 0.02, 0.5); col(0.14, 0.02, 0.5);
      col(0.52, -0.30, 0.20, 0.05, false);
      // まぐさ: 左の1本は柱の上、右の1本は崩れて傾く
      boxPart(0.50, 0.07, 0.15, 2, T(-0.28, yTop + 0.035, 0.02), 0.75, 0.55);
      boxPart(0.34, 0.07, 0.15, 2, T(0.10, yTop + 0.03, 0.02, 1, 1, 1, 0, 0.15), 0.75, 0.55);
      // 破風の断片: 三角柱（頂点が上）
      {
        const R = 0.29, m = T(-0.28, yTop + 0.07 + 0.0653, 0.02, 1, 0.45, 1);
        add(new THREE.CylinderGeometry(R, R, 0.15, 3, 1).rotateX(-Math.PI / 2), 2, m);
        const tri = [[0, R, 0.075], [R * Math.sqrt(3) / 2, -R / 2, 0.075], [-R * Math.sqrt(3) / 2, -R / 2, 0.075]];
        const tri2 = tri.map((p) => [p[0], p[1], -0.075]);
        const W = (p) => new THREE.Vector3(...p).applyMatrix4(m).toArray();
        for (let i = 0; i < 3; i++) {
          const j = (i + 1) % 3;
          ln(W(tri[i]), W(tri[j]), i === 0 || i === 2 ? C_LITE : C_INK, i === 1 ? 0.5 : 0.75);
          ln(W(tri2[i]), W(tri2[j]), C_INK, 0.5);
          ln(W(tri[i]), W(tri2[i]), C_INK, 0.45);
        }
      }
      // 倒れた柱の胴と瓦礫
      add(new THREE.CylinderGeometry(0.048, 0.05, 0.22, 10, 1), 2, T(0.26, y0 + 0.05, 0.32, 1, 1, 1, 0.6, Math.PI / 2));
      boxPart(0.09, 0.05, 0.07, 2, T(-0.52, y0 + 0.025, 0.34, 1, 1, 1, 0.4), 0.6, 0.45);
      boxPart(0.06, 0.04, 0.06, 2, T(0.64, y0 + 0.02, 0.06, 1, 1, 1, -0.5), 0.6, 0.45);
      boxPart(0.07, 0.035, 0.05, 2, T(-0.10, y0 + 0.0175, -0.34, 1, 1, 1, 0.9), 0.55, 0.4);
      return mergeKinds(parts);
    })();
    const islandLineSegs = lk.segs;

    // 浮島の配置: y<0 は視線が地上面(|x|≈7.3)の内側を通らないよう x を十分外に
    const islandItems = [
      { x: -17, y: -1, z: -18, s: 4.6 }, { x: 19, y: -3, z: -26, s: 5.4 },
      { x: -30, y: 1.5, z: -40, s: 7.0 }, { x: 27, y: 2, z: -54, s: 6.4 },
      { x: -13.5, y: 4, z: -13, s: 2.4 }, { x: 34, y: -2, z: -14, s: 3.2 },
    ].map((it) => ({ x: it.x, y: it.y, z: it.z, sx: it.s, sy: it.s * 0.85, sz: it.s, ry: rnd() * 6 }));
    // 雲海に落ちる影: 光（-0.35,0.8,-0.45）の反対側へ高さぶんずらす。半径はやや大きめ、二乗を割る値を渡す
    islandItems.forEach((it, i) => {
      const h = it.y - CLOUD_Y;
      uIsl.value[i].set(it.x + 0.4375 * h, it.z + 0.5625 * h, (it.sx * 0.95 + h * 0.05) ** 2 * 0.8, 0);
    });
    const islandMat = mkMat(mkVs({ attr: 'attribute float aKind;', vary: 'varying float vK;', body: 'vK = aKind;' }), /* glsl */ `
      vec3 n = normalize(vN);
      float l = clamp(dot(n, normalize(vec3(-0.35, 0.8, -0.45))) * 0.5 + 0.5, 0.0, 1.0);
      float r = length(vLocal.xz);
      float ang = atan(vLocal.z, vLocal.x);
      vec3 c;
      if (vK < 0.5) {
        // 岩: 上面との境に暗い帯、下へ向かって暗い青紫、地層の筋、先端に雲の照り返し
        float ry = clamp((-vLocal.y - 0.08) / 1.5, 0.0, 1.0);
        float lr = clamp(dot(n, normalize(vec3(-0.35, 0.8, -0.45))) + 0.30, 0.0, 1.0);   // 下向きの面は光が当たらず暗い
        vec3 rockLit = mix(vec3(0.16, 0.14, 0.34), vec3(0.56, 0.42, 0.54), lr * lr);
        vec3 deep = vec3(0.025, 0.028, 0.12) * (0.7 + 0.7 * lr);
        c = mix(rockLit, deep, smoothstep(0.10, 0.80, ry) * 0.92);
        float lip = 1.0 - smoothstep(0.0, 0.2, ry);
        c = mix(c, vec3(0.035, 0.035, 0.13), lip * 0.7);
        float st = sin(ry * 64.0 + sin(ang * 3.0 + 1.0) * 2.4 + sin(ang * 7.0) * 0.8);
        c *= 1.0 - 0.16 * smoothstep(0.5, 1.0, st) + 0.10 * smoothstep(0.85, 1.0, -st);
        c += vec3(0.08, 0.045, 0.085) * smoothstep(0.75, 1.0, ry) * (0.3 + l);
      } else if (vK < 1.5) {
        if (n.y > 0.6) {
          // 上面: 草・苔・乾いた土の斑、縁は濃い草＋明るい縁、遺構の足元は影
          float m = sin(vLocal.x * 6.5 + 2.1 * sin(vLocal.z * 4.3)) * sin(vLocal.z * 7.3 - 1.7 * sin(vLocal.x * 3.1));
          vec3 grass = mix(vec3(0.52, 0.66, 0.62), vec3(0.86, 0.90, 0.72), l);
          vec3 moss = mix(vec3(0.34, 0.52, 0.50), vec3(0.62, 0.78, 0.60), l);
          vec3 dry = mix(vec3(0.66, 0.68, 0.62), vec3(0.95, 0.90, 0.72), l);
          c = mix(grass, moss, smoothstep(0.0, 0.6, m));
          c = mix(c, dry, smoothstep(0.3, 0.8, -m) * 0.6);
          c = mix(c, vec3(0.30, 0.46, 0.46), smoothstep(0.80, 0.96, r) * 0.55);
          c = mix(c, vec3(1.0, 0.98, 0.85), smoothstep(0.955, 0.995, r) * 0.7);
          float rd = max(abs(vLocal.x + 0.02) - 0.58, abs(vLocal.z - 0.02) * 1.9 - 0.26);
          c *= 1.0 - 0.30 * exp(-max(rd, 0.0) * 8.0) * step(0.0, rd);
        } else {
          // 縁の側面: 上端は草の垂れ、下は土
          float t = (vLocal.y + 0.08) / 0.16;
          vec3 soil = mix(vec3(0.24, 0.22, 0.42), vec3(0.62, 0.52, 0.58), l * l);
          vec3 turf = mix(vec3(0.30, 0.46, 0.46), vec3(0.66, 0.80, 0.62), l);
          c = mix(soil, turf, smoothstep(0.55, 0.78, t + 0.06 * sin(ang * 23.0)));
        }
      } else if (vK < 2.5) {
        // 大理石: 淡い脈、足元は少し暗い
        vec3 marble = mix(vec3(0.46, 0.44, 0.72), vec3(1.0, 0.97, 0.93), l);
        float vein = smoothstep(0.92, 1.0, sin(vLocal.x * 23.0 + vLocal.y * 11.0 + sin(vLocal.z * 9.0) * 2.0));
        c = mix(marble, marble * vec3(0.80, 0.80, 0.94), vein * 0.6);
        c *= 0.84 + 0.16 * smoothstep(0.10, 0.45, vLocal.y);
      } else {
        // 基壇の石: 大理石よりやや暗く暖かい
        c = mix(vec3(0.38, 0.36, 0.60), vec3(0.90, 0.85, 0.86), l);
        c *= 0.95 + 0.05 * sin(vLocal.x * 37.0 + vLocal.z * 29.0);
      }
      c *= 0.9 + 0.1 * vB;
      // 遠方は霞へ。岩の下面は霞を弱める（深い青紫を保つ）
      c = mix(c, vec3(0.80, 0.82, 0.96), clamp(1.0 - exp(-vDist * 0.005), 0.0, 0.6) * (vK < 0.5 ? 0.35 : 1.0));
      float al = 1.0;`, { uSpin: { value: 0.03 }, uBob: { value: 0.55 } }, { fragDecl: 'varying float vK;' });
    const islandMesh = inst(islandGeom, islandMat, islandItems);
    root.add(islandMesh);
    disposables.push(islandGeom, islandMat);

    // 島の線（岩の筋・割れ目・縁の輪郭・遺構の稜線）。premultiplied で「塗る」ので暗い線も描ける。深度テストあり
    {
      const lm = instLines(islandMesh, islandLineSegs, {
        widthPx: 1.25, opacity: 1,
        frag: /* glsl */ `
          col = pow(col, vec3(0.4545));
          float dist = length(vW - cameraPosition);
          float fog = clamp(1.0 - exp(-dist * 0.005), 0.0, 0.6);
          col = mix(col, pow(vec3(0.80, 0.82, 0.96), vec3(0.4545)), fog);
          a *= vP;`,
      }, { uSpin: { value: 0.03 }, uBob: { value: 0.55 }, uBias: { value: 0.03 }, uRotOn: { value: 0 }, uRotSpd: { value: 0 } });
      const mat = lm.material;
      mat.blending = THREE.CustomBlending;
      mat.blendSrc = THREE.OneFactor; mat.blendDst = THREE.OneMinusSrcAlphaFactor;
      mat.blendSrcAlpha = THREE.OneFactor; mat.blendDstAlpha = THREE.OneMinusSrcAlphaFactor;
      mat.depthTest = true;
      lm.renderOrder = -30;
      root.add(lm);
    }

    // ---------- 3) ガラスの破片: 6角の柱状の双錐（平面ごとに虹色の薄膜反射）。稜線のハイライトと屈折の縞 ----------
    const glassItems = [];
    for (let i = 0; i < 22; i++) {
      const side = i % 2 ? 1 : -1, z = -(9 + rnd() * 55);
      const s = 0.7 + rnd() * 1.3;
      glassItems.push({ x: side * (12 + rnd() * 12 + (-z) * 0.28), y: -3 + rnd() * 9, z, sx: s * 0.55, sy: s * 2.2, sz: s * 0.4,
        rx: (rnd() - 0.5) * 1.2, rz: (rnd() - 0.5) * 1.2, ry: rnd() * 6 });
    }
    const glassGeom = (() => {
      const n = 6, top = [0.05, 1.0, 0.0], bot = [-0.05, -1.0, 0.03], A = [], B = [];
      for (let i = 0; i < n; i++) {
        const a = (i / n) * Math.PI * 2 + 0.2 * Math.sin(i * 2.3);
        const ra = 0.95 + 0.12 * Math.sin(i * 1.7 + 0.5), rb = 0.80 + 0.1 * Math.cos(i * 2.1);
        A.push([ra * Math.cos(a), 0.30, ra * Math.sin(a)]);
        B.push([rb * Math.cos(a + 0.15), -0.22, rb * Math.sin(a + 0.15)]);
      }
      const P = [], BY = [], NRM = [];
      const tri = (a, b, c) => {
        const u = new THREE.Vector3(...b).sub(new THREE.Vector3(...a)), w = new THREE.Vector3(...c).sub(new THREE.Vector3(...a));
        const nn = u.cross(w), cen = new THREE.Vector3(...a).add(new THREE.Vector3(...b)).add(new THREE.Vector3(...c)).divideScalar(3);
        if (nn.dot(cen) < 0) { const t = b; b = c; c = t; nn.negate(); }
        nn.normalize();
        P.push(...a, ...b, ...c); BY.push(1, 0, 0, 0, 1, 0, 0, 0, 1);
        for (let i = 0; i < 3; i++) NRM.push(nn.x, nn.y, nn.z);
      };
      for (let i = 0; i < n; i++) {
        const j = (i + 1) % n;
        tri(top, A[j], A[i]);
        tri(A[i], A[j], B[j]); tri(A[i], B[j], B[i]);
        tri(B[i], B[j], bot);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      g.setAttribute('normal', new THREE.Float32BufferAttribute(NRM, 3));
      g.setAttribute('aBary', new THREE.Float32BufferAttribute(BY, 3));
      return g;
    })();
    const glassMat = mkMat(mkVs({ attr: 'attribute vec3 aBary;', vary: 'varying vec3 vBary;', body: 'vBary = aBary;' }), /* glsl */ `
      vec3 n = normalize(vN);
      float f = 1.0 - abs(dot(n, normalize(vView)));
      float ph = f * 1.6 + vB * 2.0 + uTime * 0.02;
      vec3 iri = 0.5 + 0.5 * cos(6.2831 * (ph + vec3(0.0, 0.33, 0.67)));
      vec3 base = mix(vec3(0.82, 0.90, 1.0), vec3(1.0), clamp(dot(n, normalize(vec3(-0.3, 0.8, -0.5))), 0.0, 1.0));
      // 屈折の縞: 面ごとにずれた細い帯が虹色を強める
      float sp = sin(vLocal.y * 9.0 + vB * 6.0 + f * 4.0 + dot(n, vec3(1.0, 0.0, 0.6)) * 3.0);
      float band = smoothstep(0.55, 0.95, sp);
      vec3 c = mix(base, iri, 0.32 + 0.25 * f) * 0.95;
      c = mix(c, iri * 1.05 + 0.10, band * 0.35);
      // 稜線のハイライト（画面上でほぼ一定の細さ）
      float e = min(min(vBary.x, vBary.y), vBary.z);
      float ridge = 1.0 - smoothstep(0.0, max(fwidth(e) * 1.8, 1e-4), e);
      c = mix(c, vec3(1.0, 0.99, 0.96), ridge * 0.75);
      c = mix(c, vec3(0.80, 0.82, 0.96), clamp(1.0 - exp(-vDist * 0.008), 0.0, 0.7));
      float al = clamp(0.55 + 0.35 * f + band * 0.10 + ridge * 0.35, 0.0, 1.0);`, { uSpin: { value: 0.12 }, uBob: { value: 0.7 } }, { transparent: true, fragDecl: 'varying vec3 vBary;' });
    const glassMesh = inst(glassGeom, glassMat, glassItems);
    glassMesh.renderOrder = -40;
    root.add(glassMesh);
    disposables.push(glassGeom, glassMat);

    // ---------- 4) 光の輪（金）: 太めの環＋周回する光点3つ。細部（内側の輪・目盛り・外側の破線）は線メッシュ ----------
    const ringItems = [];
    const ringSpec = [[-26, 4.5, -22, 8.5], [27, 6, -34, 9.5], [-33, 8, -50, 12], [15.5, 9, -14, 4.5]];
    ringSpec.forEach(([x, y, z, r], i) => ringItems.push({ x, y, z, sx: r, sy: r, sz: r, ry: (x < 0 ? 1 : -1) * 0.5, rx: -0.5 + i * 0.05 }));
    const ringGeom = (() => {
      const tor = new THREE.TorusGeometry(1, 0.05, 8, 128);
      const P = [], N = [], K = [], I = [];
      const push = (g, kind) => {
        const base = P.length / 3;
        for (let i = 0; i < g.attributes.position.array.length; i++) { P.push(g.attributes.position.array[i]); N.push(g.attributes.normal.array[i]); }
        for (let i = 0; i < g.attributes.position.count; i++) K.push(kind);
        if (g.index) for (let i = 0; i < g.index.count; i++) I.push(base + g.index.array[i]);
        else for (let i = 0; i < g.attributes.position.count; i++) I.push(base + i);
      };
      push(tor, 0);
      for (let i = 0; i < 3; i++) {
        const a = (i / 3) * Math.PI * 2 + 0.4;
        push(new THREE.IcosahedronGeometry(0.06, 2).translate(Math.cos(a), Math.sin(a), 0), 1);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      g.setAttribute('normal', new THREE.Float32BufferAttribute(N, 3));
      g.setAttribute('aKind', new THREE.Float32BufferAttribute(K, 1));
      g.setIndex(I);
      return g;
    })();
    const ringMat = mkMat(mkVs({
      attr: 'attribute float aKind;', vary: 'varying float vK;', body: 'vK = aKind;',
      // 光点は輪の面内でゆっくり周回（約2分で1周）
      pre: 'if (aKind > 0.5) { float th = uTime * 0.05; float cs = cos(th), sn = sin(th); p.xy = vec2(cs * p.x - sn * p.y, sn * p.x + cs * p.y); n.xy = vec2(cs * n.x - sn * n.y, sn * n.x + cs * n.y); }',
    }), /* glsl */ `
      float f = 1.0 - abs(dot(normalize(vN), normalize(vView)));
      vec3 c = mix(vec3(1.0, 0.80, 0.42), vec3(1.0, 0.92, 0.66), f) * (0.85 + 0.15 * vB);
      c = vK > 0.5 ? mix(vec3(1.0, 0.95, 0.78), vec3(1.0, 0.99, 0.92), f) : c;
      c = mix(c, vec3(0.98, 0.84, 0.66), clamp(1.0 - exp(-vDist * 0.006), 0.0, 0.5));
      float al = 0.95;`, { uSpin: { value: 0.05 }, uBob: { value: 0.3 } }, { transparent: true, fragDecl: 'varying float vK;' });
    const ringMesh = inst(ringGeom, ringMat, ringItems);
    ringMesh.renderOrder = -45;
    root.add(ringMesh);
    disposables.push(ringGeom, ringMat);

    {
      const RK = lineKit();
      const gold = [0.96, 0.66, 0.20], goldD = [0.90, 0.58, 0.18];   // 明るい背景の上に「塗る」ので加算でなく濃い金
      const pt = (r, a) => [r * Math.cos(a), r * Math.sin(a), 0];
      const NA = 96;
      for (let i = 0; i < NA; i++) {   // 内側の細い2本目の輪と、外側の細い輪（静止）
        const a0 = (i / NA) * Math.PI * 2, a1 = ((i + 1) / NA) * Math.PI * 2;
        RK.seg(pt(0.86, a0), pt(0.86, a1), gold, 0.75, 0);
        RK.seg(pt(1.09, a0), pt(1.09, a1), goldD, 0.30, 0);
      }
      for (let i = 0; i < 72; i++) {   // 目盛り（順方向にゆっくり回る）。12本ごとに長い
        const a = (i / 72) * Math.PI * 2, long = i % 6 === 0;
        RK.seg(pt(long ? 0.855 : 0.90, a), pt(0.945, a), gold, long ? 0.85 : 0.5, 1);
      }
      for (let i = 0; i < 30; i++) {   // 外側の破線環（逆方向にゆっくり回る）。両端に小さな刻み
        const a0 = (i / 30) * Math.PI * 2, a1 = a0 + (Math.PI * 2 / 30) * 0.55;
        for (let s = 0; s < 3; s++) RK.seg(pt(1.15, a0 + (a1 - a0) * (s / 3)), pt(1.15, a0 + (a1 - a0) * ((s + 1) / 3)), goldD, 0.6, 2);
        RK.seg(pt(1.15, a0), pt(1.19, a0), goldD, 0.6, 2);
      }
      const lm = instLines(ringMesh, RK.segs, {
        widthPx: 1.2, opacity: 0.7,
        frag: /* glsl */ `
          float dist = length(vW - cameraPosition);
          a *= 1.0 - 0.5 * clamp(1.0 - exp(-dist * 0.006), 0.0, 0.6);`,
      }, { uSpin: { value: 0.05 }, uBob: { value: 0.3 }, uBias: { value: 0 }, uRotOn: { value: 1 }, uRotSpd: { value: 0.035 } });
      const mt = lm.material;
      mt.blending = THREE.CustomBlending;
      mt.blendSrc = THREE.OneFactor; mt.blendDst = THREE.OneMinusSrcAlphaFactor;
      mt.blendSrcAlpha = THREE.OneFactor; mt.blendDstAlpha = THREE.OneMinusSrcAlphaFactor;
      mt.depthTest = true;
      lm.renderOrder = -44;
      root.add(lm);
    }

    // ---------- 5) 光芒（加算の板をまとめた1メッシュ）。細い筋つき ----------
    {
      const pos = [], uv = [], sd = [], idx = [];
      const rays = 8;
      for (let i = 0; i < rays; i++) {
        const side = i % 2 ? 1 : -1, k = i >> 1;
        const sx = side * (6 + k * 7), ex = side * (26 + k * 13);
        const S = [sx, 34, -96], E = [ex, -8, -22 - k * 4];
        const w0 = 1.5, w1 = 4.5 + k * 1.2;
        const base = pos.length / 3;
        pos.push(S[0] - w0, S[1], S[2], S[0] + w0, S[1], S[2], E[0] + w1, E[1], E[2], E[0] - w1, E[1], E[2]);
        uv.push(-1, 0, 1, 0, 1, 1, -1, 1);
        const s = rnd(); sd.push(s, s, s, s);
        idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
      g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
      g.setAttribute('aSeed', new THREE.Float32BufferAttribute(sd, 1));
      g.setIndex(idx);
      const mat = new THREE.ShaderMaterial({
        uniforms: { ...zone.uniforms, uTime },
        vertexShader: /* glsl */ `
          attribute float aSeed; uniform float uTime; varying vec2 vUv; varying float vS;
          void main() {
            vUv = uv; vS = aSeed;
            vec3 p = position; p.x += sin(uTime * 0.18 + aSeed * 40.0) * 1.2 * uv.y;
            gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(p, 1.0);
          }`,
        fragmentShader: /* glsl */ `
          ${glsl.spawn}
          uniform float uTime; varying vec2 vUv; varying float vS;
          void main() {
            float across = 1.0 - smoothstep(0.0, 1.0, abs(vUv.x));
            float along = smoothstep(0.0, 0.25, vUv.y) * (1.0 - smoothstep(0.55, 1.0, vUv.y));
            float breathe = 0.9 + 0.1 * sin(uTime * 0.25 + vS * 30.0);
            float streak = 0.80 + 0.20 * sin(vUv.x * 38.0 + vS * 30.0 + vUv.y * 3.0);   // 細い筋
            float a = across * across * along * 0.16 * breathe * streak;
            a *= 1.0 - museSpawnMask(museBaseNdc());
            vec3 rc = mix(vec3(1.0, 0.86, 0.62), vec3(1.0, 0.80, 0.86), step(0.5, fract(vS * 7.0)));
            gl_FragColor = vec4(rc * a, a);
            #include <colorspace_fragment>
          }`,
        transparent: true, depthWrite: false, depthTest: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
      });
      const m = new THREE.Mesh(g, mat);
      m.frustumCulled = false; m.renderOrder = -50;
      root.add(m);
      disposables.push(g, mat);
    }

    // ---------- 6) モノリス: 消失点の黒い逆三角（画面座標で形を決め、カメラから一定距離のワールドに置く） ----------
    // 縁の光は3重（主帯・外側の桃色の細線・内側の薄紫の細線）。内面に淡い入れ子の山形と横桟の刻線。暗部ゾーン内では刻線を消す。
    {
      const D = 150;
      const at = (x, y) => {
        const p = new THREE.Vector3(x, y, 0.5).unproject(camera);
        return p.sub(camera.position).normalize().multiplyScalar(D).add(camera.position);
      };
      // 半幅 = k*(v - vApex)。ゾーン下端(v=0.79)で u=±0.17、頂点は地上面に隠れる位置
      const vApex = 0.5, vTop = 1.1, k = 0.19 / (0.79 - vApex);
      const hw = (v) => k * (v - vApex);
      const edge = 0.012;
      const P = [], CL = [], FX = [];
      const W = (p) => at(p[0], p[1]).toArray();
      const tri = (a, b, c, cc, fx) => { P.push(...W(a), ...W(b), ...W(c)); for (let i = 0; i < 3; i++) { CL.push(...cc); FX.push(fx); } };
      const quad = (a, b, c, e, cc, fx) => { tri(a, b, c, cc, fx); tri(a, c, e, cc, fx); };
      // 縁から水平に o（+外側 / -内側）ずらした線: 上端 vTop から、u=0 で交わる頂点まで
      const edgePt = (o, side, v) => [side * (hw(v) + o), v];
      const apexV = (o) => vApex - o / k;
      const band = (o0, o1, cc, fx) => {
        for (const side of [-1, 1]) {
          const a0 = edgePt(o0, side, vTop), a1 = [0, apexV(o0)], b0 = edgePt(o1, side, vTop), b1 = [0, apexV(o1)];
          quad(a0, b0, b1, a1, cc, fx);
        }
      };
      const glow = [1.0, 0.90, 0.68], black = [0.008, 0.012, 0.03];
      const inset = edge * 1.6;
      // 内側の黒（ほんの少し縁の下まで重ねる）
      { const o = -(inset - 0.002); tri(edgePt(o, -1, vTop), edgePt(o, 1, vTop), [0, apexV(o)], [...black, 1], 0); }
      band(0, -inset, [...glow, 1], 1);                                   // 主帯（琥珀）
      band(0.022, 0.0155, [1.0, 0.70, 0.74, 0.85], 1);                    // 外側の細線（桃）
      band(-inset - 0.018, -inset - 0.0225, [0.86, 0.82, 1.0, 0.55], 2);  // 内側の細線（薄紫）
      // 刻線: 入れ子の山形（縁に平行）と横桟
      [0.075, 0.13, 0.185].forEach((dd, i) => band(-inset - dd, -inset - dd - 0.0017, [0.16, 0.18, 0.38, 0.62 - i * 0.1], 2));
      const rung = (v, insetX, cc) => {
        const w = hw(v) - inset - insetX, t = 0.0008;
        quad([-w, v - t], [w, v - t], [w, v + t], [-w, v + t], cc, 2);
      };
      [0.83, 0.90, 0.97, 1.04].forEach((v, i) => rung(v, 0.03, [0.16, 0.18, 0.38, 0.55 - i * 0.06]));
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      g.setAttribute('aCol', new THREE.Float32BufferAttribute(CL, 4));
      g.setAttribute('aFx', new THREE.Float32BufferAttribute(FX, 1));
      const mat = new THREE.ShaderMaterial({
        uniforms: { ...zone.uniforms, uTime },
        vertexShader: /* glsl */ `
          attribute vec4 aCol; attribute float aFx; varying vec4 vCol; varying float vFx;
          void main() { vCol = aCol; vFx = aFx; gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(position, 1.0); }`,
        fragmentShader: /* glsl */ `
          ${glsl.spawn}
          uniform float uTime; varying vec4 vCol; varying float vFx;
          void main() {
            vec2 ndc = museBaseNdc();
            vec3 c = vCol.rgb; float a = vCol.a;
            if (vFx > 0.5 && vFx < 1.5) c *= 1.0 + 0.16 * sin(ndc.y * 24.0 - uTime * 0.5 + ndc.x * 3.0);   // 縁の光がゆっくり波打つ
            if (vFx > 1.5) a *= 1.0 - 0.92 * museSpawnMask(ndc);                                             // 刻線は暗部内では消す
            gl_FragColor = vec4(c, a);
            #include <colorspace_fragment>
          }`,
        transparent: true, side: THREE.DoubleSide, depthWrite: false, depthTest: false,
      });
      const m = new THREE.Mesh(g, mat);
      m.frustumCulled = false; m.renderOrder = -60;
      root.add(m);
      disposables.push(g, mat);
    }

    return {
      object: root,
      update({ t }) { cloud.material.uniforms.uTime.value = t; uTime.value = t; },
      dispose() { disposables.forEach((o) => o.dispose && o.dispose()); },
    };
  },
};
