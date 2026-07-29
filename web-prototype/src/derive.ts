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
 *
 * 権威の所在:
 *  - θ（視点）は**入力**。地平線 v_horizon はそこからの導出値。
 *    以前は逆（v_horizon が入力）だったが、それだと判定帯の上下端まで θ を制約してしまい
 *    可動域がほぼ無くなる。判定帯はスクリーン空間オーバーレイなので θ を制約すべきでない。
 *  - 判定線の画面位置（vGroundJudge / vSkyJudge）は人間工学上の固定点。ここから
 *    z_j と空中面の高さ h が決まる。したがって θ の有効域は判定線2本だけで決まる。
 *
 * スケール不変性について:
 *  - yCam は純粋なスケール。変えても投影像は完全に不変（全ワールド寸法が比例するだけ）。
 *  - φ は像の遠近の付き方（圧縮のカーブ）を変える。
 *  - 最遠端と速度はワールド単位ではなく比率 farFrac と先読み秒数で指定しているので、
 *    φ・yCam を動かしても最遠端の見た目と先読み時間は変わらない。
 */
export interface Derived {
  aspect: number;
  tanHalfPhi: number;
  /** カメラ俯角 (rad)。cfg.thetaDeg を有効域にクランプしたもの */
  theta: number;
  /** θ の有効域 (deg)。判定線2本の画面位置と φ から決まる */
  thetaMinDeg: number;
  thetaMaxDeg: number;
  /** cfg.thetaDeg が有効域外でクランプされたか */
  thetaClamped: boolean;
  /** 地平線の画面位置 (NDC v)。θ からの導出値。1 を超えると画面外 */
  vHorizon: number;
  /** 最遠端の画面位置 (NDC v)。farFrac からの導出値。※これは地上面での位置 */
  vFar: number;

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

  /** cfg.thetaDeg のクランプ後の sin/cos。laneX の計算に使う */
  sinTheta: number;
  cosTheta: number;
  /** レーン x = u_k・laneK・zc(z) の係数（U・aspect・tan(φ/2)） */
  laneK: number;
  /** 地上面の最遠端における視線方向距離。層別の収束率補正の基準（laneX 参照） */
  zcFarGround: number;

  /**
   * 最遠端の奥行き。**両層で共有する単一の値**。
   * 同じ画面行で切ると層ごとに全く別の奥行きになってしまうため、奥行きで切る。
   */
  zFar: number;
  /** 空中面の最遠端が画面上のどこに来るか（zFar は共有なので vFar より上に出る。参考値） */
  vFarSky: number;
  /** 各層の面・ノーツが消える手前側の奥行き */
  groundNear: number;
  skyNear: number;

  /** ワールド単位のスクロール速度。z_j も z_far も層共通なので速度も層共通 */
  speed: number;
  /** 先読み時間 (秒)。両層とも同じ */
  readAhead: number;

  /** カメラの far クリップに使う値 */
  drawFar: number;
}

const deg = (d: number) => (d * Math.PI) / 180;
const rad = (r: number) => (r * 180) / Math.PI;

/** NDC の v に対応する俯角 ψ (rad) */
export function psi(theta: number, tanHalfPhi: number, v: number): number {
  return theta - Math.atan(v * tanHalfPhi);
}

/**
 * NDC の v と面の高さ y_p から、その面に当たる水平奥行き d を求める。
 *
 * ψ ≥ 90° は「その画面行がカメラ真下より後ろを向いている」状態で、面との交点は
 * カメラ背後になる。手前端の指定に使われるので 0（＝手前は切らない）を返す。
 */
export function depthAt(yCam: number, yPlane: number, psiRad: number): number {
  if (psiRad <= 1e-6) return Infinity; // 地平線より上 → 面に当たらない
  if (psiRad >= Math.PI / 2 - 1e-6) return 0; // 真下より後ろ
  return (yCam - yPlane) / Math.tan(psiRad);
}

/** カメラ空間での視線方向距離 z_c。横幅の計算に使う */
export function viewDist(yCam: number, theta: number, yPlane: number, depth: number): number {
  return (yCam - yPlane) * Math.sin(theta) + depth * Math.cos(theta);
}

/** 視線方向距離 z_c における、NDC u=1 に対応するワールド半幅 */
function halfWidthAt(zc: number, aspect: number, tanHalfPhi: number): number {
  return zc * aspect * tanHalfPhi;
}

/**
 * レーンの x 座標。奥行き z における画面 u=u_k の位置を、
 * cfg.laneConverge で「画面上の長方形 (0)」〜「ワールド平行 = 現行の台形 (1)」の間で連続的に補間する。
 *
 *   zc(z)      = (yCam − yPlane)·sinθ + z·cosθ        （視線方向距離。z の1次式）
 *   zc(zJudge) = 同上を z=zJudge で評価した定数
 *   x(z) = u_k・laneK・[ (1−c)・zc(z) + c・zc(zJudge) ]
 *
 * c=0 のとき x は zc(z) に比例して奥ほど広がる → 画面上の u が z に依らず一定＝長方形。
 * c=1 のとき x は zc(zJudge) の定数（z に依らない）→ ワールド平行レーン（画面上は台形）。
 * yPlane は層ごとに異なる（layerF で連続的に補間できる）ので、アークが層をまたいでも破綻しない。
 *
 * ## 最遠端の断面を長方形にするための層別補正
 *
 * 上式の画面上の u は
 *
 *   u_screen(z) = U・[ (1−c) + c・zc(z_j)/zc(z) ]
 *
 * となる。zc は層ごとに違う（空中面は高いぶんカメラに近く zc が小さい）ので、両層に同じ c を
 * 使うと最遠端で空中のほうが狭くなり、断面が「上が狭い台形」になる。
 *
 * そこで最遠端の u_screen が層に依らず一致する条件を解くと、
 *
 *   u_screen(z_far) = U・[ 1 − c・(z_far − z_j)・cosθ / zc_far ]
 *
 * なので c を zc_far に比例させればよい:
 *
 *   c(layer) = laneConverge・zc_far(layer) / zc_far(地上)
 *
 * これで最遠端の項が層に依存しなくなり、断面は laneConverge の値によらず常に長方形になる。
 * 地上は c(0) = laneConverge で不変、判定線上は c に関係なく必ず ±U なので、
 * スクリーン空間の権威（判定線の位置と幅）も現状の見た目も損なわない。
 * c は zc_far ≤ zc_far(地上) より必ず laneConverge 以下に収まる（クランプは保険）。
 */
export function laneX(
  cfg: StageConfig,
  d: Derived,
  u: number,
  layerF: number,
  z: number,
): number {
  const yPlane = layerF * d.skyHeight;
  const a = (cfg.yCam - yPlane) * d.sinTheta;
  const zc = a + z * d.cosTheta;
  const zcJudge = a + d.zJudge * d.cosTheta;
  const zcFar = a + d.zFar * d.cosTheta;
  const c = Math.min(1, Math.max(0, cfg.laneConverge * (zcFar / d.zcFarGround)));
  const zcMix = zc + (zcJudge - zc) * c;
  return u * d.laneK * zcMix;
}

/**
 * θ の有効域。判定線が地平線を越えず、かつ視野の裏側に回らない範囲。
 * 端ちょうどでは奥行きが発散するのでマージン (ψ ∈ [1.5°, 88.5°]) を取る。
 */
export function thetaRange(cfg: StageConfig, tanHalfPhi: number): [min: number, max: number] {
  const vTop = Math.max(cfg.vSkyJudge, cfg.vGroundJudge);
  const vBot = Math.min(cfg.vSkyJudge, cfg.vGroundJudge);
  return [
    rad(Math.atan(vTop * tanHalfPhi) + deg(1.5)),
    rad(Math.atan(vBot * tanHalfPhi) + deg(88.5)),
  ];
}

export function derive(cfg: StageConfig, aspectIn: number): Derived {
  // アスペクト比が 0 や NaN だと全ワールド座標が NaN に伝播するので吸収しておく
  const aspect = Number.isFinite(aspectIn) && aspectIn > 0 ? aspectIn : 1;
  const tanHalfPhi = Math.tan(deg(cfg.phiDeg) / 2);

  // 視点（俯角）が入力。地平線はここからの導出値
  const [thetaMinDeg, thetaMaxDeg] = thetaRange(cfg, tanHalfPhi);
  const thetaDeg = Math.min(thetaMaxDeg, Math.max(thetaMinDeg, cfg.thetaDeg));
  const theta = deg(thetaDeg);
  const P = (v: number) => psi(theta, tanHalfPhi, v);
  const vHorizon = Math.tan(theta) / tanHalfPhi;

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

  const sinTheta = Math.sin(theta);
  const cosTheta = Math.cos(theta);
  const laneK = cfg.U * aspect * tanHalfPhi;

  // 最遠端。「地上判定線 → 地平線（画面外なら画面上端）」の何割かで指定する。
  // 地平線ちょうどでは奥行きが発散するのでマージンを取る。
  const vCeil = Math.min(vHorizon - 0.02, 1);
  const frac = Math.min(0.995, Math.max(0.02, cfg.farFrac));
  const vFar = cfg.vGroundJudge + frac * (vCeil - cfg.vGroundJudge);
  // これを単一のワールド奥行きに変換し、空中面も同じ奥行きで切る。
  // → 最遠端の断面は奥行き一定の垂直な切り口になる。
  const zFar = Math.min(depthAt(cfg.yCam, 0, P(vFar)), zJudge * 200);
  // 共有 zFar における空中面の画面位置（描画には使わない。ズレ量を GUI で見るための参考値）
  const vFarSky = Math.tan(theta - Math.atan((cfg.yCam - skyHeight) / zFar)) / tanHalfPhi;

  // 層別の収束率補正の基準（laneX 参照）。zFar が確定してから求める
  const zcFarGround = viewDist(cfg.yCam, theta, 0, zFar);

  // 手前側の消える位置。空中は判定線で切ると見やすい（ユーザー要望）
  const groundNear = gbNear;
  const skyNear = cfg.skyFloorFromJudge ? zJudge : sbNear;

  // 最遠端も判定線も層共通の奥行きなので、速度も層共通で先読み時間は自動的に一致する。
  const readAhead = Math.max(cfg.readAheadSec, 0.05);
  const speed = (zFar - zJudge) / readAhead;

  return {
    aspect,
    tanHalfPhi,
    theta,
    thetaMinDeg,
    thetaMaxDeg,
    thetaClamped: Math.abs(thetaDeg - cfg.thetaDeg) > 1e-6,
    vHorizon,
    vFar,
    zJudge,
    skyHeight,
    groundBandDepth: [gbNear, gbFar],
    skyBandDepth: [sbNear, sbFar],
    groundWidth: halfGround * 2,
    skyWidth: halfSky * 2,
    groundCellWidth: (halfGround * 2) / cfg.cells,
    skyCellWidth: (halfSky * 2) / cfg.cells,
    sinTheta,
    cosTheta,
    laneK,
    zcFarGround,
    zFar,
    vFarSky,
    groundNear,
    skyNear,
    speed,
    readAhead,
    drawFar: zFar * 1.15,
  };
}
