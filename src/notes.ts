import * as THREE from 'three';
import { COLORS, LAYER_SKY, type StageConfig } from './config';
import type { Derived } from './derive';
import { arcAt, noteEnd, noteStart, type ArcNote, type Note } from './chart';

export type NoteState = 'pending' | 'active' | 'hit' | 'missed';

export interface NoteRuntime {
  note: Note;
  state: NoteState;
  /** 頂点属性 aState の範囲 */
  vStart: number;
  vCount: number;
  /** hold / arc: 保持できている割合の判定用に、最後に成功していた時刻 */
  lastHeld: number;
  /** 判定結果表示用 */
  judgeMs: number;
}

/**
 * ノーツの描画とスクロール。
 * すべてのノーツを1つのマージ済みジオメトリに入れ、グループごと z 方向へ動かす。
 *  - ローカル z = −(zJudge + time · speed)
 *  - group.position.z = songTime · speed
 * → ワールド奥行き = zJudge + (time − songTime) · speed
 */
export class NoteField {
  readonly root = new THREE.Group();
  runtimes: NoteRuntime[] = [];
  private stateAttr: THREE.BufferAttribute | null = null;
  private mesh: THREE.Mesh | null = null;
  private beatLines: THREE.LineSegments | null = null;
  private cfg!: StageConfig;
  private d!: Derived;

  /** 層 (連続値) とセル境界インデックス (連続値) からワールド x を求める */
  xAt(layerF: number, cellF: number): number {
    const gi = lerpArray(this.d.groundLaneX, cellF);
    const si = lerpArray(this.d.skyLaneX, cellF);
    return gi + (si - gi) * layerF;
  }

  yAt(layerF: number): number {
    return layerF * this.d.skyHeight;
  }

  zAt(time: number): number {
    return -(this.d.zJudge + time * this.cfg.scrollSpeed);
  }

  build(cfg: StageConfig, d: Derived, notes: Note[]): void {
    this.dispose();
    this.cfg = cfg;
    this.d = d;

    const pos: number[] = [];
    const col: number[] = [];
    const st: number[] = [];
    const nearArr: number[] = [];
    this.runtimes = [];

    // ノーツが消える奥行き = その層の判定帯の下端（面の手前端と一致させる）
    const nearOf = (layerF: number) =>
      d.groundBandDepth[0] + (d.skyBandDepth[0] - d.groundBandDepth[0]) * layerF;

    const push = (
      x: number,
      y: number,
      z: number,
      c: THREE.Color,
      s: number,
      nearD: number,
    ) => {
      pos.push(x, y, z);
      col.push(c.r, c.g, c.b);
      st.push(s);
      nearArr.push(nearD);
    };
    const quad = (
      p: [number, number, number][],
      c: THREE.Color,
      nearD: number,
    ) => {
      // p = [左手前, 右手前, 右奥, 左奥]
      const idx: number[] = [0, 1, 2, 0, 2, 3];
      for (const i of idx) push(p[i][0], p[i][1], p[i][2], c, 1, nearD);
    };

    const cG = new THREE.Color(COLORS.ground);
    const cS = new THREE.Color(COLORS.sky);
    const thickness = 0.55; // タップノーツの奥行き方向の厚み

    for (const n of notes) {
      const vStart = st.length;

      if (n.kind === 'tap') {
        const layerF = n.layer === LAYER_SKY ? 1 : 0;
        const y = this.yAt(layerF) + 0.02;
        const x0 = this.xAt(layerF, n.cell + 0.06);
        const x1 = this.xAt(layerF, n.cell + 0.94);
        const zc = this.zAt(n.time);
        quad(
          [
            [x0, y, zc + thickness / 2],
            [x1, y, zc + thickness / 2],
            [x1, y, zc - thickness / 2],
            [x0, y, zc - thickness / 2],
          ],
          n.layer === LAYER_SKY ? cS : cG,
          nearOf(layerF),
        );
      } else if (n.kind === 'hold') {
        const layerF = n.layer === LAYER_SKY ? 1 : 0;
        const y = this.yAt(layerF) + 0.02;
        const x0 = this.xAt(layerF, n.cell + 0.18);
        const x1 = this.xAt(layerF, n.cell + 0.82);
        const zs = this.zAt(n.time);
        const ze = this.zAt(n.endTime);
        const c = n.layer === LAYER_SKY ? cS : cG;
        // 本体
        quad(
          [
            [x0, y, zs],
            [x1, y, zs],
            [x1, y, ze],
            [x0, y, ze],
          ],
          c.clone().multiplyScalar(0.65),
          nearOf(layerF),
        );
        // 始点（タップ相当の判定を持つ）
        const hx0 = this.xAt(layerF, n.cell + 0.06);
        const hx1 = this.xAt(layerF, n.cell + 0.94);
        quad(
          [
            [hx0, y + 0.01, zs + thickness / 2],
            [hx1, y + 0.01, zs + thickness / 2],
            [hx1, y + 0.01, zs - thickness / 2],
            [hx0, y + 0.01, zs - thickness / 2],
          ],
          c,
          nearOf(layerF),
        );
      } else {
        this.pushArc(n, quad, nearOf);
      }

      this.runtimes.push({
        note: n,
        state: 'pending',
        vStart,
        vCount: st.length - vStart,
        lastHeld: -Infinity,
        judgeMs: 0,
      });
    }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('aColor', new THREE.Float32BufferAttribute(col, 3));
    this.stateAttr = new THREE.Float32BufferAttribute(st, 1);
    geo.setAttribute('aState', this.stateAttr);
    geo.setAttribute('aNear', new THREE.Float32BufferAttribute(nearArr, 1));

    const mat = new THREE.ShaderMaterial({
      uniforms: {
        uFar: { value: cfg.drawFar },
      },
      vertexShader: /* glsl */ `
        attribute vec3 aColor;
        attribute float aState;
        attribute float aNear;
        varying vec3 vColor;
        varying float vState;
        varying float vDepth;
        varying float vNear;
        void main() {
          vec4 wp = modelMatrix * vec4(position, 1.0);
          vColor = aColor;
          vState = aState;
          vNear = aNear;
          vDepth = -wp.z;
          gl_Position = projectionMatrix * viewMatrix * wp;
        }
      `,
      fragmentShader: /* glsl */ `
        uniform float uFar;
        varying vec3 vColor;
        varying float vState;
        varying float vDepth;
        varying float vNear;
        void main() {
          if (vState <= 0.001) discard;
          // 遠方でフェードイン、自分の層の判定帯の下端より手前でフェードアウト
          float aFar = 1.0 - smoothstep(uFar * 0.75, uFar, vDepth);
          float aNear = smoothstep(vNear * 0.72, vNear, vDepth);
          float a = vState * aFar * aNear;
          if (a <= 0.003) discard;
          gl_FragColor = vec4(vColor * (0.7 + 0.6 * vState), a);
        }
      `,
      transparent: true,
      depthWrite: false,
      side: THREE.DoubleSide,
      blending: THREE.AdditiveBlending,
    });

    this.mesh = new THREE.Mesh(geo, mat);
    this.mesh.frustumCulled = false;
    this.mesh.renderOrder = 5;
    this.root.add(this.mesh);

    this.buildBeatLines(cfg, d, notes);
  }

  private pushArc(
    arc: ArcNote,
    quad: (p: [number, number, number][], c: THREE.Color, nearD: number) => void,
    nearOf: (layerF: number) => number,
  ): void {
    const t0 = noteStart(arc);
    const t1 = noteEnd(arc);
    const steps = Math.max(8, Math.ceil((t1 - t0) / 0.03));
    let prev: { x: number; y: number; z: number; w: number } | null = null;
    const c = new THREE.Color(0x35e8ff);
    for (let i = 0; i <= steps; i++) {
      const t = t0 + ((t1 - t0) * i) / steps;
      const { layerF, cellF } = arcAt(arc, t);
      const x = this.xAt(layerF, cellF);
      const y = this.yAt(layerF) + 0.12;
      const z = this.zAt(t);
      const cellW =
        this.d.groundCellWidth + (this.d.skyCellWidth - this.d.groundCellWidth) * layerF;
      const w = cellW * 0.34;
      if (prev) {
        quad(
          [
            [prev.x - prev.w, prev.y, prev.z],
            [prev.x + prev.w, prev.y, prev.z],
            [x + w, y, z],
            [x - w, y, z],
          ],
          c,
          nearOf(layerF),
        );
      }
      prev = { x, y, z, w };
    }
  }

  private buildBeatLines(cfg: StageConfig, d: Derived, notes: Note[]): void {
    const b = 60 / cfg.bpm;
    const last = notes.length ? noteEnd(notes[notes.length - 1]) : 0;
    const pts: number[] = [];
    const x0 = d.groundLaneX[0];
    const x1 = d.groundLaneX[d.groundLaneX.length - 1];
    for (let t = 0; t < last + 4; t += b * 4) {
      const z = this.zAt(t);
      pts.push(x0, 0.005, z, x1, 0.005, z);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
    const m = new THREE.ShaderMaterial({
      uniforms: { uFar: { value: cfg.drawFar } },
      vertexShader: /* glsl */ `
        varying float vDepth;
        void main() {
          vec4 wp = modelMatrix * vec4(position, 1.0);
          vDepth = -wp.z;
          gl_Position = projectionMatrix * viewMatrix * wp;
        }
      `,
      fragmentShader: /* glsl */ `
        uniform float uFar;
        varying float vDepth;
        void main() {
          float a = 0.22 * (1.0 - smoothstep(uFar * 0.4, uFar, vDepth));
          if (a <= 0.003) discard;
          gl_FragColor = vec4(0.55, 0.65, 0.95, a);
        }
      `,
      transparent: true,
      depthWrite: false,
    });
    this.beatLines = new THREE.LineSegments(g, m);
    this.beatLines.frustumCulled = false;
    this.root.add(this.beatLines);
  }

  /** ノーツの表示状態を更新（0 で非表示） */
  setNoteAlpha(rt: NoteRuntime, alpha: number): void {
    if (!this.stateAttr) return;
    const arr = this.stateAttr.array as Float32Array;
    for (let i = rt.vStart; i < rt.vStart + rt.vCount; i++) arr[i] = alpha;
    this.stateAttr.needsUpdate = true;
  }

  update(songTime: number): void {
    this.root.position.z = songTime * this.cfg.scrollSpeed;
  }

  dispose(): void {
    this.root.traverse((o) => {
      const m = o as THREE.Mesh;
      if (m.geometry) m.geometry.dispose();
      if (m.material) (m.material as THREE.Material).dispose();
    });
    this.root.clear();
    this.mesh = null;
    this.beatLines = null;
    this.stateAttr = null;
  }
}

function lerpArray(arr: number[], idx: number): number {
  const i = Math.max(0, Math.min(arr.length - 2, Math.floor(idx)));
  const f = idx - i;
  return arr[i] + (arr[i + 1] - arr[i]) * f;
}
