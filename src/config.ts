/**
 * ステージ設定。
 *
 * 設計原則: **スクリーン空間 (NDC) の値が唯一の正**。
 * 3D のカメラ配置・面の高さ・面の幅・スクロール速度はすべてここから derive.ts で導出される。逆は不可。
 *
 * NDC の v は画面下端 -1 / 中央 0 / 上端 +1。u は左 -1 / 右 +1。
 */
export interface StageConfig {
  // ---- カメラ ----
  /** 垂直画角 (deg)。水平画角はアスペクト比から決まる */
  phiDeg: number;
  /** カメラ高さ。全ワールド寸法の基準スケール（これ自体に見た目上の意味はない） */
  yCam: number;
  /**
   * ステージの視点＝カメラ俯角 (deg)。地上面を基準に 0〜90。
   *  - 0°  : 視線が地上面と平行。ステージは地平線上の線に潰れて見えなくなる。
   *  - 90° : 地上面に対して垂直（真上から）。地上と空中の区別が視覚的に消える。
   *
   * 有効域は両端より狭い。判定線の画面位置と φ から決まり、derive() が
   * `thetaMin` / `thetaMax` として返す（範囲外はクランプされる）:
   *  - 下限: 上側の判定線が地平線を越えてしまう角度
   *  - 上限: 下側の判定線が視野の裏側（カメラの真下より後ろ）に回る角度
   *
   * 判定帯の上下端はスクリーン空間オーバーレイなので θ を制約しない。
   * 地平線 v_horizon = tan θ / tan(φ/2) は導出値（1 を超えると画面外）。
   */
  thetaDeg: number;

  // ---- スクリーン空間の権威値 (NDC v) ----
  /** 空中 判定線 */
  vSkyJudge: number;
  /** 空中 判定帯 上端 / 下端 */
  vSkyTop: number;
  vSkyBot: number;
  /** 層の境界（入力の layer 判定はこの1本のみ） */
  vSplit: number;
  /** 地上 判定帯 上端 / 下端 */
  vGroundTop: number;
  vGroundBot: number;
  /** 地上 判定線 */
  vGroundJudge: number;
  /**
   * ステージ最遠端の位置。「地上判定線から地平線（地平線が画面外なら画面上端）まで」を
   * 1 とした比率で指定する。
   *
   * 絶対的な画面行 (旧 vFar) で持つと θ と衝突する（最遠端は必ず地平線より下でなければ
   * ならないので、θ の可動域を潰してしまう）。比率にしておけば θ を振っても破綻しない。
   *
   * 最遠端は**両層で共有する単一のワールド奥行き z_far** に変換される。地上判定線の
   * 画面位置を基準に z_far を決め、空中面も同じ z_far で切る。したがって最遠端の断面は
   * 奥行き一定の垂直な切り口になる（空中面のワールド幅は地上より狭いので、
   * 厳密な長方形ではなく上が狭い台形）。
   */
  farFrac: number;

  // ---- 横方向 ----
  /** 判定帯の半幅 (NDC u)。0.87 = 画面幅の 87% */
  U: number;
  /** 1層あたりのセル数 */
  cells: number;

  // ---- ゲームプレイ ----
  /**
   * ノーツが最遠端 z_far から判定線 z_j まで来るのにかかる秒数 = 先読み時間。
   * ワールド単位のスクロール速度はここから導出される（φ・yCam に依存しない）。
   *
   * 最遠端も判定線も両層で共有する単一の奥行きなので、スクロール速度も両層共通になり、
   * 先読み時間は自動的に一致する（層ごとに速度を分ける補正は不要）。
   */
  readAheadSec: number;
  /** 譜面の BPM（デモ譜面生成用） */
  bpm: number;
  /** 判定ウィンドウ (ms)。※暫定値、未設計項目 */
  windowPerfect: number;
  windowGood: number;
  /** 押下中ポインタの層切り替えヒステリシス (NDC v) */
  splitHysteresis: number;

  // ---- 見た目 ----
  /** 最遠端をぼかさずはっきり切る */
  hardFarEdge: boolean;
  /** レーン境界線の間引き。1 = 全部、3 = 3セルごと */
  laneLineStep: number;
  /** 空中面を判定線で切る（判定線より手前＝時間的に以降を描かない） */
  skyFloorFromJudge: boolean;

  // ---- デバッグ表示 ----
  showBand: boolean;
  showJudgeLine: boolean;
  showSplitLine: boolean;
  showLaneFloor: boolean;
  showHorizon: boolean;
  showCellIndex: boolean;
  showTouchDebug: boolean;
  metronome: boolean;
}

export const DEFAULT_CONFIG: StageConfig = {
  // 2026-07-26 ユーザーが実機調整した値
  phiDeg: 77,
  yCam: 8,
  // 旧 vHorizon: 0.93 と等価な俯角。θ = atan(v_horizon · tan(φ/2))
  thetaDeg: 36.5,

  vSkyTop: 0.9,
  vSkyJudge: 0.15,
  vSkyBot: -0.1,
  vSplit: -0.1,
  vGroundTop: -0.1,
  vGroundJudge: -0.56,
  vGroundBot: -1,
  // 旧 vFar: 0.72 と等価な比率
  farFrac: 0.87,

  U: 0.87,
  cells: 12,

  readAheadSec: 1.8,
  bpm: 150,
  windowPerfect: 45,
  windowGood: 100,
  splitHysteresis: 0.05,

  hardFarEdge: true,
  laneLineStep: 3,
  skyFloorFromJudge: true,

  showBand: true,
  showJudgeLine: true,
  showSplitLine: true,
  showLaneFloor: true,
  showHorizon: true,
  showCellIndex: true,
  showTouchDebug: true,
  metronome: false,
};

/** 層の識別子。layer=0 が地上、layer=1 が空中。将来 layer=2 を足せる設計 */
export const LAYER_GROUND = 0;
export const LAYER_SKY = 1;
export type Layer = 0 | 1;

export const COLORS = {
  ground: 0x8b5cf6, // 紫（地上）
  sky: 0xff3ea5, // マゼンタ（空中）
  groundCss: '#8b5cf6',
  skyCss: '#ff3ea5',
  gridGround: 0x3a2f6b,
  gridSky: 0x6b2a55,
} as const;
