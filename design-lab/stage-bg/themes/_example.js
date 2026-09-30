// 見本（ハーネスの動作確認・契約の参考実装）。比較対象ではないので registry には載せない（?v=_example.js で表示）。
// 1) 空: 背景用の全画面板1枚（base NDC で置く）に縦グラデーション。museSpawnMask でゾーン内を沈める
// 2) 浮遊物: 箱を数個、実時間 t でゆっくり上下させる（1 InstancedMesh = 1 draw）
export default {
  id: 'example', name: '見本', model: 'Opus 5.5',
  concept: 'ハーネス確認用', palette: '上: 淡い空色 / 下: 灰', motion: '箱の浮遊', perf: '全画面1枚＋インスタンス1',
  unityCost: '-',
  clearColor: '#000000',
  shade: { color: '#05060c', strength: 0.85 },
  stage: null, // 例: { groundFill: '#2a2a30', groundLine: '#ffffff', groundLineAlpha: 0.3 }
  create({ THREE, zone, glsl, d }) {
    const root = new THREE.Group();

    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3));
    g.setIndex([0, 1, 2, 0, 2, 3]);
    const sky = new THREE.Mesh(g, new THREE.ShaderMaterial({
      uniforms: { ...zone.uniforms, uTime: { value: 0 } },
      vertexShader: /* glsl */ `
        uniform vec4 uBaseToClip;
        void main() { gl_Position = vec4(position.xy * uBaseToClip.xy + uBaseToClip.zw, 0.999, 1.0); }`,
      fragmentShader: /* glsl */ `
        ${glsl.spawn}
        uniform float uTime;
        void main() {
          vec2 p = museBaseNdc();
          vec3 top = vec3(0.78, 0.88, 0.95), bot = vec3(0.55, 0.56, 0.60);
          vec3 c = mix(bot, top, smoothstep(-1.0, 1.0, p.y));
          c *= 1.0 - 0.6 * museSpawnMask(p); // テーマ側でも少し沈める（最終保証は共通の暗幕）
          gl_FragColor = vec4(c, 1.0);
          #include <colorspace_fragment>
        }`,
      depthWrite: false, depthTest: false,
    }));
    sky.frustumCulled = false; sky.renderOrder = -100;
    root.add(sky);

    const N = 8;
    const boxes = new THREE.InstancedMesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial({ color: 0xf2f2ee }), N);
    boxes.frustumCulled = false;
    const seeds = Array.from({ length: N }, (_, i) => ({
      x: (i % 2 ? 1 : -1) * (d.zFar * 0.35 + (i * 7) % 11), y: 6 + (i * 5) % 9, z: -d.zFar * (0.4 + 0.12 * (i % 5)), s: 1.5 + (i % 3),
    }));
    root.add(boxes);
    const m = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler();
    return {
      object: root,
      update({ t }) {
        seeds.forEach((s, i) => {
          e.set(0.2 * t + i, 0.3 * t, 0); q.setFromEuler(e);
          m.compose(new THREE.Vector3(s.x, s.y + Math.sin(t * 0.6 + i) * 0.8, s.z), q, new THREE.Vector3(s.s, s.s, s.s));
          boxes.setMatrixAt(i, m);
        });
        boxes.instanceMatrix.needsUpdate = true;
      },
      dispose() { g.dispose(); boxes.geometry.dispose(); },
    };
  },
};
