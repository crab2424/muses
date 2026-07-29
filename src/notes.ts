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
  /** 現在の表示アルファ。値が変わらないときは属性更新をスキップする */
  alpha: number;
  /** hold / arc: 最後に保持できていた時刻 */
  lastHeld: number;
}

/**
 * ノーツの描画。
 *
 * 頂点は (x, y, ノーツ時刻) を持ち、奥行きは頂点シェーダで毎フレーム計算する:
 *
 *   depth = z_j + (aTime − songTime) · aSpeed
 *
 * ジオメトリを動かさないので、
 *  - 層ごとにスクロール速度を変えられる（aSpeed を頂点ごとに持てる）
 *  - 長い譜面でもワールド座標が巨大にならず float32 精度が劣化しない
 */
export class NoteField {
  readonly root = new THREE.Group();
  runtimes: NoteRuntime[] = [];
  private stateAttr: THREE.BufferAttribute | null = null;
  private uniforms: Record<string, THREE.IUniform> = {};
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

  build(cfg: StageConfig, d: Derived, notes: Note[]): void {
    this.dispose();
    this.d = d;

    const pos: number[] = [];
    const col: number[] = [];
    const st: number[] = [];
    const nearArr: number[] = [];
    this.runtimes = [];

    // 最遠端 zFar と速度は両層共通（uniform）。手前端だけは層ごとに違うので頂点属性で持つ。
    const nearOf = (layerF: number) => d.groundNear + (d.skyNear - d.groundNear) * layerF;

    const push = (x: number, y: number, time: number, c: THREE.Color, nearD: number) => {
      pos.push(x, y, time);
      col.push(c.r, c.g, c.b);
      st.push(1);
      nearArr.push(nearD);
    };
    const quad = (p: [number, number, number][], c: THREE.Color, nearD: number) => {
      // p = [左手前, 右手前, 右奥, 左奥]（3 番目の要素は z ではなく「時刻」）
      const idx: number[] = [0, 1, 2, 0, 2, 3];
      for (const i of idx) push(p[i][0], p[i][1], p[i][2], c, nearD);
    };

    const cG = new THREE.Color(COLORS.ground);
    const cS = new THREE.Color(COLORS.sky);
    // タップノーツの奥行き方向の厚み（ワールド単位）。判定線の奥行きに比例させて
    // 画角を変えても画面上の見た目の厚みが保たれるようにする
    const thicknessWorld = d.zJudge * 0.05;

    for (const n of notes) {
      const vStart = st.length;

      if (n.kind === 'tap') {
        const layerF = n.layer === LAYER_SKY ? 1 : 0;
        const dt = thicknessWorld / d.speed / 2; // 厚みを時刻の幅に換算
        const y = this.yAt(layerF) + d.zJudge * 0.002;
        const x0 = this.xAt(layerF, n.cell + 0.04);
        const x1 = this.xAt(layerF, n.cell + n.width - 0.04);
        quad(
          [
            [x0, y, n.time - dt],
            [x1, y, n.time - dt],
            [x1, y, n.time + dt],
            [x0, y, n.time + dt],
          ],
          n.layer === LAYER_SKY ? cS : cG,
          nearOf(layerF),
        );
      } else if (n.kind === 'hold') {
        const layerF = n.layer === LAYER_SKY ? 1 : 0;
        const dt = thicknessWorld / d.speed / 2;
        const y = this.yAt(layerF) + d.zJudge * 0.002;
        const c = n.layer === LAYER_SKY ? cS : cG;
        const nr = nearOf(layerF);
        // 本体
        const x0 = this.xAt(layerF, n.cell + 0.16);
        const x1 = this.xAt(layerF, n.cell + n.width - 0.16);
        quad(
          [
            [x0, y, n.time],
            [x1, y, n.time],
            [x1, y, n.endTime],
            [x0, y, n.endTime],
          ],
          c.clone().multiplyScalar(0.6),
          nr,
        );
        // 始点（タップ相当の判定を持つ）
        const hx0 = this.xAt(layerF, n.cell + 0.04);
        const hx1 = this.xAt(layerF, n.cell + n.width - 0.04);
        const hy = y + d.zJudge * 0.001;
        quad(
          [
            [hx0, hy, n.time - dt],
            [hx1, hy, n.time - dt],
            [hx1, hy, n.time + dt],
            [hx0, hy, n.time + dt],
          ],
          c,
          nr,
        );
      } else {
        this.pushArc(n, push, nearOf);
      }

      this.runtimes.push({
        note: n,
        state: 'pending',
        vStart,
        vCount: st.length - vStart,
        alpha: 1,
        lastHeld: -Infinity,
      });
    }

    // ビートライン（地上のみ）
    const b = 60 / cfg.bpm;
    const last = notes.length ? noteEnd(notes[notes.length - 1]) : 0;
    const beatPos: number[] = [];
    const beatNear: number[] = [];
    const gx0 = d.groundLaneX[0];
    const gx1 = d.groundLaneX[d.groundLaneX.length - 1];
    for (let t = 0; t < last + 4; t += b * 4) {
      beatPos.push(gx0, d.zJudge * 0.0005, t, gx1, d.zJudge * 0.0005, t);
      beatNear.push(d.groundNear, d.groundNear);
    }

    this.uniforms = {
      uSongTime: { value: 0 },
      uZJudge: { value: d.zJudge },
      uSpeed: { value: d.speed },
      uFar: { value: d.zFar },
      uHardFar: { value: cfg.hardFarEdge ? 1 : 0 },
    };

    // 速度と最遠端は両層共通なので uniform。手前端だけ層ごとに違うので属性。
    const vertexCommon = /* glsl */ `
      attribute float aNear;
      uniform float uSongTime;
      uniform float uZJudge;
      uniform float uSpeed;
      varying float vDepth;
      varying float vNear;
      vec4 placeNote(vec3 p) {
        // p = (x, y, ノーツ時刻)
        float depth = uZJudge + (p.z - uSongTime) * uSpeed;
        vDepth = depth;
        vNear = aNear;
        return projectionMatrix * viewMatrix * vec4(p.x, p.y, -depth, 1.0);
      }
    `;

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('aColor', new THREE.Float32BufferAttribute(col, 3));
    this.stateAttr = new THREE.Float32BufferAttribute(st, 1);
    geo.setAttribute('aState', this.stateAttr);
    geo.setAttribute('aNear', new THREE.Float32BufferAttribute(nearArr, 1));

    const mat = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader:
        vertexCommon +
        /* glsl */ `
        attribute vec3 aColor;
        attribute float aState;
        varying vec3 vColor;
        varying float vState;
        void main() {
          vColor = aColor;
          vState = aState;
          gl_Position = placeNote(position);
        }
      `,
      fragmentShader: /* glsl */ `
        uniform float uHardFar;
        uniform float uFar;
        varying vec3 vColor;
        varying float vState;
        varying float vDepth;
        varying float vNear;
        void main() {
          if (vState <= 0.001) discard;
          if (vDepth > uFar) discard;                       // 最遠端で切る（両層共通）
          float aFar = uHardFar > 0.5 ? 1.0 : 1.0 - smoothstep(uFar * 0.7, uFar, vDepth);
          // 手前端でフェードアウト。範囲を狭くして、面が終わった先へノーツがはみ出さないようにする
          float aNear = smoothstep(vNear * 0.90, vNear, vDepth);
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

    const mesh = new THREE.Mesh(geo, mat);
    mesh.frustumCulled = false;
    mesh.renderOrder = 5;
    this.root.add(mesh);

    const bgeo = new THREE.BufferGeometry();
    bgeo.setAttribute('position', new THREE.Float32BufferAttribute(beatPos, 3));
    bgeo.setAttribute('aNear', new THREE.Float32BufferAttribute(beatNear, 1));
    const bmat = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader: vertexCommon + `void main() { gl_Position = placeNote(position); }`,
      fragmentShader: /* glsl */ `
        uniform float uHardFar;
        uniform float uFar;
        varying float vDepth;
        varying float vNear;
        void main() {
          if (vDepth > uFar || vDepth < vNear) discard;
          float a = 0.22 * (uHardFar > 0.5 ? 1.0 : 1.0 - smoothstep(uFar * 0.55, uFar, vDepth));
          if (a <= 0.003) discard;
          gl_FragColor = vec4(0.55, 0.65, 0.95, a);
        }
      `,
      transparent: true,
      depthWrite: false,
    });
    const beatLines = new THREE.LineSegments(bgeo, bmat);
    beatLines.frustumCulled = false;
    this.root.add(beatLines);
  }

  /**
   * アークは層をまたぐため、頂点ごとに自分の layerF に応じた手前端を持たせる。
   * （速度と最遠端は層共通になったので uniform 側で処理される。以前は速度も層ごとに
   *   違ったため、セグメント単位で速度を与えると隣接セグメントで奥行きがずれて帯が裂けた）
   */
  private pushArc(
    arc: ArcNote,
    push: (x: number, y: number, time: number, c: THREE.Color, nearD: number) => void,
    nearOf: (layerF: number) => number,
  ): void {
    const t0 = noteStart(arc);
    const t1 = noteEnd(arc);
    const steps = Math.max(8, Math.ceil((t1 - t0) / 0.03));
    const c = new THREE.Color(0x35e8ff);

    type P = { x: number; y: number; t: number; w: number; layerF: number };
    const at = (t: number): P => {
      const { layerF, cellF } = arcAt(arc, t);
      const cellW =
        this.d.groundCellWidth + (this.d.skyCellWidth - this.d.groundCellWidth) * layerF;
      return {
        x: this.xAt(layerF, cellF),
        y: this.yAt(layerF) + this.d.zJudge * 0.01,
        t,
        w: (cellW * arc.width) / 2,
        layerF,
      };
    };
    const emit = (p: P, side: -1 | 1) =>
      push(p.x + side * p.w, p.y, p.t, c, nearOf(p.layerF));

    let prev = at(t0);
    for (let i = 1; i <= steps; i++) {
      const cur = at(t0 + ((t1 - t0) * i) / steps);
      // 三角形 (prevL, prevR, curR) と (prevL, curR, curL)
      emit(prev, -1);
      emit(prev, 1);
      emit(cur, 1);
      emit(prev, -1);
      emit(cur, 1);
      emit(cur, -1);
      prev = cur;
    }
  }

  /** ノーツの表示状態を更新（0 で非表示）。値が変わらないときは何もしない */
  setNoteAlpha(rt: NoteRuntime, alpha: number): void {
    if (!this.stateAttr || rt.alpha === alpha) return;
    rt.alpha = alpha;
    const arr = this.stateAttr.array as Float32Array;
    for (let i = rt.vStart; i < rt.vStart + rt.vCount; i++) arr[i] = alpha;
    this.stateAttr.needsUpdate = true;
  }

  update(songTime: number): void {
    if (this.uniforms.uSongTime) this.uniforms.uSongTime.value = songTime;
  }

  dispose(): void {
    this.root.traverse((o) => {
      const m = o as THREE.Mesh;
      if (m.geometry) m.geometry.dispose();
      if (m.material) (m.material as THREE.Material).dispose();
    });
    this.root.clear();
    this.stateAttr = null;
    this.uniforms = {};
  }
}

function lerpArray(arr: number[], idx: number): number {
  const i = Math.max(0, Math.min(arr.length - 2, Math.floor(idx)));
  const f = idx - i;
  return arr[i] + (arr[i + 1] - arr[i]) * f;
}
