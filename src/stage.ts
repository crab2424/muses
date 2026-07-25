import * as THREE from 'three';
import { COLORS, type StageConfig } from './config';
import type { Derived } from './derive';

/**
 * 奥行きでフェードアウトするマテリアル。
 * 面もラインも同じシェーダを使う（ワールド z の絶対値 = 奥行き）。
 */
function fadeMaterial(color: number, alpha: number, near: number, far: number) {
  return new THREE.ShaderMaterial({
    uniforms: {
      uColor: { value: new THREE.Color(color) },
      uAlpha: { value: alpha },
      uNear: { value: near },
      uFar: { value: far },
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
      varying float vDepth;
      void main() {
        float a = uAlpha * (1.0 - smoothstep(uNear, uFar, vDepth));
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

    const near = d.zJudge * 0.5;
    const far = cfg.drawFar;

    // 各層の面は「その層の判定帯の下端に対応する奥行き」から描き始める。
    // こうすると面の手前端が画面上でちょうど自分の判定帯の下端に一致し、
    // 手前側に面が広がって画面を覆う問題が起きない。
    const layers: Array<{
      y: number;
      laneX: number[];
      color: number;
      grid: number;
      nearDepth: number;
      visible: boolean;
    }> = [
      {
        y: 0,
        laneX: d.groundLaneX,
        color: COLORS.ground,
        grid: COLORS.gridGround,
        nearDepth: d.groundBandDepth[0],
        visible: cfg.showLaneFloor,
      },
      {
        y: d.skyHeight,
        laneX: d.skyLaneX,
        color: COLORS.sky,
        grid: COLORS.gridSky,
        nearDepth: d.skyBandDepth[0],
        visible: cfg.showLaneFloor,
      },
    ];

    for (const L of layers) {
      if (!L.visible) continue;
      const x0 = L.laneX[0];
      const x1 = L.laneX[L.laneX.length - 1];
      const n0 = Math.max(0.5, L.nearDepth);

      // 面（帯そのもの）
      const plane = new THREE.PlaneGeometry(x1 - x0, far - n0);
      plane.rotateX(-Math.PI / 2);
      plane.translate((x0 + x1) / 2, L.y, -(n0 + far) / 2);
      const planeMesh = new THREE.Mesh(plane, fadeMaterial(L.grid, 0.18, near, far));
      planeMesh.renderOrder = 0;
      this.root.add(planeMesh);

      // レーン境界（カメラの向きに平行 = ワールドの x 一定の直線）
      // → 画面上では消失点に収束する。判定帯オーバーレイの垂直線とは判定線上でのみ一致する。
      const pts: number[] = [];
      for (let k = 0; k < L.laneX.length; k++) {
        const x = L.laneX[k];
        pts.push(x, L.y, -n0, x, L.y, -far);
      }
      const lg = new THREE.BufferGeometry();
      lg.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
      const lines = new THREE.LineSegments(lg, fadeMaterial(L.color, 0.45, near, far));
      lines.renderOrder = 1;
      this.root.add(lines);

      // 判定線（奥行き zJudge の横線）— 3D 側の位置確認用。
      // 実際の判定線描画はスクリーン空間オーバーレイ側が正。
      const jg = new THREE.BufferGeometry();
      jg.setAttribute(
        'position',
        new THREE.Float32BufferAttribute([x0, L.y, -d.zJudge, x1, L.y, -d.zJudge], 3),
      );
      const jline = new THREE.LineSegments(jg, fadeMaterial(L.color, 0.9, far, far + 1));
      jline.renderOrder = 2;
      this.root.add(jline);
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
