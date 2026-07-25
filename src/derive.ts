import type { StageConfig } from './config';

/**
 * スクリーン空間 (NDC) → 3D の導出。
 *
 * 中核原理: 透視投影では画面位置が y_c / z_c の比のみで決まるため、
 * 画面の1行 v はカメラから見た1つの俯角 ψ に対応する。
 *
 *   ψ(v) = θ − atan(v · tan(φ/2))
 *   面 y = y_p に当たる水平奥行き  d = (y_cam − y_p) / tan ψ
 *   地平線                        v_horizon = tan θ / tan(φ/2)
 *
 * ワールド座標系: カメラは (0, yCam, 0) にあり、俯角 θ で −z 方向を向く。
 * 「奥行き d」は水平距離であり、ワールド z = −d に対応する。
 */
export interface Derived {
  aspect: number;
  tanHalfPhi: number;
  /** カメラ俯角 (rad) */
  theta: number;

  /** 判定線の奥行き（層に依らない単一の値。ノーツのタイミングはこれだけで決まる） */
  zJudge: number;
  /** 空中面の高さ */
  skyHeight: number;

  /** 各層の判定帯が対応する奥行き範囲（※タイミングとは無関係。参考値） */
  groundBandDepth: [near: number, far: number];
  skyBandDepth: [near: number, far: number];

  /** 判定線上での面の全幅 */
  groundWidth: number;
  skyWidth: number;
  /** 判定線上でのセル1個の幅 */
  groundCellWidth: number;
  skyCellWidth: number;

  /** 各層のセル境界の x 座標（判定線上で NDC の u_k に一致する。以降 x は一定） */
  groundLaneX: number[];
  skyLaneX: number[];

  /** 地上ノーツが空中判定帯の下端に隠れるまでの奥行き（実質的な可視限界） */
  groundVisibleFar: number;
  /** 先読み時間 (秒) */
  readAheadSec: number;
}

const deg = (d: number) => (d * Math.PI) / 180;

/** NDC の v に対応する俯角 ψ (rad) */
export function psi(theta: number, tanHalfPhi: number, v: number): number {
  return theta - Math.atan(v * tanHalfPhi);
}

/** NDC の v と面の高さ y_p から、その面に当たる水平奥行き d を求める */
export function depthAt(yCam: number, yPlane: number, psiRad: number): number {
  const t = Math.tan(psiRad);
  if (t <= 1e-6) return Infinity; // 地平線より上 → 面に当たらない
  return (yCam - yPlane) / t;
}

/** カメラ空間での視線方向距離 z_c。横幅の計算に使う */
export function viewDist(yCam: number, theta: number, yPlane: number, depth: number): number {
  return (yCam - yPlane) * Math.sin(theta) + depth * Math.cos(theta);
}

/** 視線方向距離 z_c における、NDC u=1 に対応するワールド半幅 */
function halfWidthAt(zc: number, aspect: number, tanHalfPhi: number): number {
  return zc * aspect * tanHalfPhi;
}

export function derive(cfg: StageConfig, aspect: number): Derived {
  const tanHalfPhi = Math.tan(deg(cfg.phiDeg) / 2);
  // 地平線の指定から俯角が決まる
  const theta = Math.atan(cfg.vHorizon * tanHalfPhi);
  const P = (v: number) => psi(theta, tanHalfPhi, v);

  // 判定線の奥行きは地上判定線の指定から決まる（地上面は y=0）
  const zJudge = depthAt(cfg.yCam, 0, P(cfg.vGroundJudge));

  // 空中面の高さは「空中判定線が同じ奥行き zJudge に来る」条件から決まる
  const skyHeight = cfg.yCam - zJudge * Math.tan(P(cfg.vSkyJudge));

  const gbFar = depthAt(cfg.yCam, 0, P(cfg.vGroundTop));
  const gbNear = depthAt(cfg.yCam, 0, P(cfg.vGroundBot));
  const sbFar = depthAt(cfg.yCam, skyHeight, P(cfg.vSkyTop));
  const sbNear = depthAt(cfg.yCam, skyHeight, P(cfg.vSkyBot));

  const zcGround = viewDist(cfg.yCam, theta, 0, zJudge);
  const zcSky = viewDist(cfg.yCam, theta, skyHeight, zJudge);

  const halfGround = halfWidthAt(zcGround, aspect, tanHalfPhi) * cfg.U;
  const halfSky = halfWidthAt(zcSky, aspect, tanHalfPhi) * cfg.U;

  const groundLaneX: number[] = [];
  const skyLaneX: number[] = [];
  for (let k = 0; k <= cfg.cells; k++) {
    const t = -1 + (2 * k) / cfg.cells; // -1 .. +1
    groundLaneX.push(t * halfGround);
    skyLaneX.push(t * halfSky);
  }

  // 地上ノーツは空中判定帯の下端 (vSkyBot) より奥では帯に隠れる
  const groundVisibleFar = Math.min(depthAt(cfg.yCam, 0, P(cfg.vSkyBot)), cfg.drawFar);

  return {
    aspect,
    tanHalfPhi,
    theta,
    zJudge,
    skyHeight,
    groundBandDepth: [gbNear, gbFar],
    skyBandDepth: [sbNear, sbFar],
    groundWidth: halfGround * 2,
    skyWidth: halfSky * 2,
    groundCellWidth: (halfGround * 2) / cfg.cells,
    skyCellWidth: (halfSky * 2) / cfg.cells,
    groundLaneX,
    skyLaneX,
    groundVisibleFar,
    readAheadSec: (groundVisibleFar - zJudge) / cfg.scrollSpeed,
  };
}
