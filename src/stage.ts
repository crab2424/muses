import * as THREE from 'three';
import { COLORS, type StageConfig } from './config';
import { laneX, type Derived } from './derive';

/**
 * 奥行きで表示範囲を切るマテリアル。面もラインも同じシェーダを使う。
 * uHardFar=1 のとき最遠端をぼかさずはっきり切る。
 */
function stageMaterial(
  color: number,
  alpha: number,
  near: number,
  far: number,
  hardFar: boolean,
) {
  return new THREE.ShaderMaterial({
    uniforms: {
      uColor: { value: new THREE.Color(color) },
      uAlpha: { value: alpha },
      uNear: { value: near },
      uFar: { value: far },
      uHardFar: { value: hardFar ? 1 : 0 },
    },
    vertexShader: /* glsl */ `
      varying float vDepth;
      void main() {
        vec4 wp = modelMatrix * vec4(position, 1.0);
        vDepth = -wp.z;
        gl_Position = projectionMatrix * viewMatrix * wp;
      }
    `,
    fragmentShader: /* glsl */ `
      uniform vec3 uColor;
      uniform float uAlpha;
      uniform float uNear;
      uniform float uFar;
      uniform float uHardFar;
      varying float vDepth;
      void main() {
        if (vDepth > uFar || vDepth < uNear) discard;
        float a = uAlpha * (uHardFar > 0.5 ? 1.0 : 1.0 - smoothstep(uFar * 0.55, uFar, vDepth));
        if (a <= 0.001) discard;
        gl_FragColor = vec4(uColor, a);
      }
    `,
    transparent: true,
    depthWrite: false,
    side: THREE.DoubleSide,
  });
}

/** 静的なステージ（床・空中面・レーン境界）。config 変更時に rebuild する */
export class Stage {
  readonly root = new THREE.Group();

  build(cfg: StageConfig, d: Derived): void {
    this.dispose();
    if (!cfg.showLaneFloor) return;

    // 各層の面は「その層の手前端」から「両層共通の最遠端 zFar」まで描く。
    // 最遠端は層に依らず同じ奥行きなので、画面上では空中側のほうが上に出る
    // （＝最遠端の断面が奥行き一定の垂直な切り口になる）。
    // レーンの x は laneConverge に応じて奥行きの1次式になる（0=画面上で長方形、1=現行の台形）ので、
    // 面は「手前端の x」と「奥端の x」が異なる4頂点の平面として組む。
    const layers = [
      {
        y: 0,
        layerF: 0,
        color: COLORS.ground,
        grid: COLORS.gridGround,
        near: d.groundNear,
        fillAlpha: cfg.groundFillAlpha,
        step: cfg.laneLineStepGround,
      },
      {
        y: d.skyHeight,
        layerF: 1,
        color: COLORS.sky,
        grid: COLORS.gridSky,
        near: d.skyNear,
        fillAlpha: cfg.skyFillAlpha,
        step: cfg.laneLineStepSky,
      },
    ];

    for (const L of layers) {
      const n0 = Math.max(d.zJudge * 0.02, L.near);
      const f0 = d.zFar;
      if (f0 <= n0) continue;

      const xAt = (u: number, z: number) => laneX(cfg, d, u, L.layerF, z);
      const xLeftNear = xAt(-1, n0);
      const xRightNear = xAt(1, n0);
      const xLeftFar = xAt(-1, f0);
      const xRightFar = xAt(1, f0);

      // 面（帯そのもの）。手前と奥で x が異なりうる台形/長方形の平面
      if (L.fillAlpha > 0.001) {
        const plane = new THREE.BufferGeometry();
        plane.setAttribute(
          'position',
          new THREE.Float32BufferAttribute(
            [
              xLeftNear, L.y, -n0,
              xRightNear, L.y, -n0,
              xRightFar, L.y, -f0,
              xLeftFar, L.y, -f0,
            ],
            3,
          ),
        );
        plane.setIndex([0, 1, 2, 0, 2, 3]);
        plane.computeVertexNormals();
        const planeMesh = new THREE.Mesh(
          plane,
          stageMaterial(L.grid, L.fillAlpha, n0, f0, cfg.hardFarEdge),
        );
        planeMesh.renderOrder = 0;
        this.root.add(planeMesh);
      }

      // レーン境界。laneConverge=1 ではカメラの向きに平行（画面上は消失点に収束）、
      // laneConverge=0 では画面上の u が一定の直線（画面上は長方形）になる。
      // step で間引き、両端は必ず描く。
      const step = Math.max(1, Math.round(L.step));
      const pts: number[] = [];
      for (let k = 0; k <= cfg.cells; k++) {
        if (k % step !== 0 && k !== cfg.cells) continue;
        const u = -1 + (2 * k) / cfg.cells;
        pts.push(xAt(u, n0), L.y, -n0, xAt(u, f0), L.y, -f0);
      }
      // 最遠端の横線（はっきりさせる場合のみ）
      if (cfg.hardFarEdge) pts.push(xLeftFar, L.y, -f0, xRightFar, L.y, -f0);
      const lg = new THREE.BufferGeometry();
      lg.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
      const lines = new THREE.LineSegments(
        lg,
        stageMaterial(L.color, 0.45, n0, f0, cfg.hardFarEdge),
      );
      lines.renderOrder = 1;
      this.root.add(lines);
    }
  }

  dispose(): void {
    this.root.traverse((o) => {
      const m = o as THREE.Mesh;
      if (m.geometry) m.geometry.dispose();
      if (m.material) (m.material as THREE.Material).dispose();
    });
    this.root.clear();
  }
}
