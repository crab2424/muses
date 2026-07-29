import * as THREE from 'three';
import { COLORS, type StageConfig } from './config';
import type { Derived } from './derive';

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
    const layers = [
      {
        y: 0,
        laneX: d.groundLaneX,
        color: COLORS.ground,
        grid: COLORS.gridGround,
        near: d.groundNear,
      },
      {
        y: d.skyHeight,
        laneX: d.skyLaneX,
        color: COLORS.sky,
        grid: COLORS.gridSky,
        near: d.skyNear,
      },
    ];

    const step = Math.max(1, Math.round(cfg.laneLineStep));

    for (const L of layers) {
      const x0 = L.laneX[0];
      const x1 = L.laneX[L.laneX.length - 1];
      const n0 = Math.max(d.zJudge * 0.02, L.near);
      const f0 = d.zFar;
      if (f0 <= n0) continue;

      // 面（帯そのもの）
      const plane = new THREE.PlaneGeometry(x1 - x0, f0 - n0);
      plane.rotateX(-Math.PI / 2);
      plane.translate((x0 + x1) / 2, L.y, -(n0 + f0) / 2);
      const planeMesh = new THREE.Mesh(
        plane,
        stageMaterial(L.grid, 0.2, n0, f0, cfg.hardFarEdge),
      );
      planeMesh.renderOrder = 0;
      this.root.add(planeMesh);

      // レーン境界（カメラの向きに平行 = ワールドの x 一定の直線）
      // → 画面上では消失点に収束する。判定帯オーバーレイの垂直線とは判定線上でのみ一致する。
      // step で間引き、両端は必ず描く。
      const pts: number[] = [];
      for (let k = 0; k < L.laneX.length; k++) {
        if (k % step !== 0 && k !== L.laneX.length - 1) continue;
        const x = L.laneX[k];
        pts.push(x, L.y, -n0, x, L.y, -f0);
      }
      // 最遠端の横線（はっきりさせる場合のみ）
      if (cfg.hardFarEdge) pts.push(x0, L.y, -f0, x1, L.y, -f0);
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
