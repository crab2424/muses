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
 * 頂点は (u, y, ノーツ時刻) を持つ。u はレーン内の符号付き位置（-1〜+1、判定帯の u_k と同じ座標系）。
 * 奥行きとワールド x は頂点シェーダで毎フレーム計算する:
 *
 *   depth  = z_j + (aTime − songTime) · speed
 *   x(z)   = u · laneK · mix(zc(depth), zc(z_j), laneConverge)     （derive.laneX と同じ式）
 *
 * ジオメトリを動かさないので、
 *  - 長い譜面でもワールド座標が巨大にならず float32 精度が劣化しない
 *  - laneConverge を変えても再ビルドなしで見た目が追従する（stage.ts の静的面と一貫）
 */
export class NoteField {
  readonly root = new THREE.Group();
  runtimes: NoteRuntime[] = [];
  private stateAttr: THREE.BufferAttribute | null = null;
  private uniforms: Record<string, THREE.IUniform> = {};
  private cells = 12;

  /** セル境界インデックス（連続値）からレーン内の符号付き位置 u (-1〜+1) を求める */
  uAt(cellF: number): number {
    return -1 + (2 * cellF) / this.cells;
  }

  yAt(layerF: number, skyHeight: number): number {
    return layerF * skyHeight;
  }

  build(cfg: StageConfig, d: Derived, notes: Note[]): void {
    this.dispose();
    this.cells = cfg.cells;

    const pos: number[] = [];
    const col: number[] = [];
    const st: number[] = [];
    const nearArr: number[] = [];
    const layerArr: number[] = [];
    this.runtimes = [];

    // 最遠端 zFar と速度は両層共通（uniform）。手前端だけは層ごとに違うので頂点属性で持つ。
    const nearOf = (layerF: number) => d.groundNear + (d.skyNear - d.groundNear) * layerF;

    const push = (u: number, y: number, time: number, layerF: number, c: THREE.Color, nearD: number) => {
      pos.push(u, y, time);
      col.push(c.r, c.g, c.b);
      st.push(1);
      nearArr.push(nearD);
      layerArr.push(layerF);
    };
    const quad = (
      p: [number, number, number][],
      layerF: number,
      c: THREE.Color,
      nearD: number,
    ) => {
      // p = [左手前, 右手前, 右奥, 左奥]（1番目は u、3番目は z ではなく「時刻」）
      const idx: number[] = [0, 1, 2, 0, 2, 3];
      for (const i of idx) push(p[i][0], p[i][1], p[i][2], layerF, c, nearD);
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
        const y = this.yAt(layerF, d.skyHeight) + d.zJudge * 0.002;
        const u0 = this.uAt(n.cell + 0.04);
        const u1 = this.uAt(n.cell + n.width - 0.04);
        quad(
          [
            [u0, y, n.time - dt],
            [u1, y, n.time - dt],
            [u1, y, n.time + dt],
            [u0, y, n.time + dt],
          ],
          layerF,
          n.layer === LAYER_SKY ? cS : cG,
          nearOf(layerF),
        );
      } else if (n.kind === 'hold') {
        const layerF = n.layer === LAYER_SKY ? 1 : 0;
        const dt = thicknessWorld / d.speed / 2;
        const y = this.yAt(layerF, d.skyHeight) + d.zJudge * 0.002;
        const c = n.layer === LAYER_SKY ? cS : cG;
        const nr = nearOf(layerF);
        // 本体
        const u0 = this.uAt(n.cell + 0.16);
        const u1 = this.uAt(n.cell + n.width - 0.16);
        quad(
          [
            [u0, y, n.time],
            [u1, y, n.time],
            [u1, y, n.endTime],
            [u0, y, n.endTime],
          ],
          layerF,
          c.clone().multiplyScalar(0.6),
          nr,
        );
        // 始点（タップ相当の判定を持つ）
        const hu0 = this.uAt(n.cell + 0.04);
        const hu1 = this.uAt(n.cell + n.width - 0.04);
        const hy = y + d.zJudge * 0.001;
        quad(
          [
            [hu0, hy, n.time - dt],
            [hu1, hy, n.time - dt],
            [hu1, hy, n.time + dt],
            [hu0, hy, n.time + dt],
          ],
          layerF,
          c,
          nr,
        );
      } else {
        this.pushArc(n, d, push, nearOf);
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
    const beatLayer: number[] = [];
    for (let t = 0; t < last + 4; t += b * 4) {
      beatPos.push(-1, d.zJudge * 0.0005, t, 1, d.zJudge * 0.0005, t);
      beatNear.push(d.groundNear, d.groundNear);
      beatLayer.push(0, 0);
    }

    const laneConverge = Math.min(1, Math.max(0, cfg.laneConverge));
    this.uniforms = {
      uSongTime: { value: 0 },
      uZJudge: { value: d.zJudge },
      uSpeed: { value: d.speed },
      uFar: { value: d.zFar },
      uHardFar: { value: cfg.hardFarEdge ? 1 : 0 },
      uYCam: { value: cfg.yCam },
      uSkyHeight: { value: d.skyHeight },
      uSinTheta: { value: d.sinTheta },
      uCosTheta: { value: d.cosTheta },
      uLaneK: { value: d.laneK },
      uLaneConverge: { value: laneConverge },
    };

    // 速度と最遠端は両層共通なので uniform。手前端・層は頂点ごとに違うので属性。
    // x はワールド座標を焼き込まず、u (符号付きレーン位置) から毎フレーム再計算する
    // （derive.laneX と同じ式）。laneConverge の変更にジオメトリ再構築なしで追従できる。
    const vertexCommon = /* glsl */ `
      attribute float aNear;
      attribute float aLayerF;
      uniform float uSongTime;
      uniform float uZJudge;
      uniform float uSpeed;
      uniform float uYCam;
      uniform float uSkyHeight;
      uniform float uSinTheta;
      uniform float uCosTheta;
      uniform float uLaneK;
      uniform float uLaneConverge;
      varying float vDepth;
      varying float vNear;
      vec4 placeNote(vec3 p) {
        // p = (u, y, ノーツ時刻)
        float depth = uZJudge + (p.z - uSongTime) * uSpeed;
        float yPlane = aLayerF * uSkyHeight;
        float zc = (uYCam - yPlane) * uSinTheta + depth * uCosTheta;
        float zcJudge = (uYCam - yPlane) * uSinTheta + uZJudge * uCosTheta;
        float zcMix = mix(zc, zcJudge, uLaneConverge);
        float x = p.x * uLaneK * zcMix;
        vDepth = depth;
        vNear = aNear;
        return projectionMatrix * viewMatrix * vec4(x, p.y, -depth, 1.0);
      }
    `;

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('aColor', new THREE.Float32BufferAttribute(col, 3));
    this.stateAttr = new THREE.Float32BufferAttribute(st, 1);
    geo.setAttribute('aState', this.stateAttr);
    geo.setAttribute('aNear', new THREE.Float32BufferAttribute(nearArr, 1));
    geo.setAttribute('aLayerF', new THREE.Float32BufferAttribute(layerArr, 1));

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
    bgeo.setAttribute('aLayerF', new THREE.Float32BufferAttribute(beatLayer, 1));
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
   * 幅もセル分数（cellF ± arc.width/2）を u に変換して持たせる。ワールド単位に焼き込まないので
   * 頂点シェーダの zc スケーリングが左右の頂点にも一貫して効く。
   */
  private pushArc(
    arc: ArcNote,
    d: Derived,
    push: (u: number, y: number, time: number, layerF: number, c: THREE.Color, nearD: number) => void,
    nearOf: (layerF: number) => number,
  ): void {
    const t0 = noteStart(arc);
    const t1 = noteEnd(arc);
    const steps = Math.max(8, Math.ceil((t1 - t0) / 0.03));
    const c = new THREE.Color(0x35e8ff);

    type P = { cellF: number; y: number; t: number; layerF: number };
    const at = (t: number): P => {
      const { layerF, cellF } = arcAt(arc, t);
      return {
        cellF,
        y: this.yAt(layerF, d.skyHeight) + d.zJudge * 0.01,
        t,
        layerF,
      };
    };
    const emit = (p: P, side: -1 | 1) =>
      push(this.uAt(p.cellF + (side * arc.width) / 2), p.y, p.t, p.layerF, c, nearOf(p.layerF));

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
