/**
 * ステージ設定。
 *
 * 設計原則: **スクリーン空間 (NDC) の値が唯一の正**。
 * 3D のカメラ配置・面の高さ・面の幅はすべてここから derive.ts で導出される。逆は不可。
 *
 * NDC の v は画面下端 -1 / 中央 0 / 上端 +1。u は左 -1 / 右 +1。
 */
export interface StageConfig {
  // ---- カメラ ----
  /** 垂直画角 (deg)。水平画角はアスペクト比から決まる */
  phiDeg: number;
  /** カメラ高さ。全ワールド寸法の基準スケール（これ自体に絶対的な意味はない） */
  yCam: number;

  // ---- スクリーン空間の権威値 (NDC v) ----
  /** 地平線 */
  vHorizon: number;
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

  // ---- 横方向 ----
  /** 判定帯の半幅 (NDC u)。0.84 = 画面幅の 84% */
  U: number;
  /** 1層あたりのセル数 */
  cells: number;

  // ---- ゲームプレイ ----
  /** スクロール速度 (ワールド単位 / 秒) */
  scrollSpeed: number;
  /** 描画する最遠の奥行き */
  drawFar: number;
  /** 譜面の BPM（デモ譜面生成用） */
  bpm: number;
  /** 判定ウィンドウ (ms)。※暫定値、未設計項目 */
  windowPerfect: number;
  windowGood: number;
  /** 押下中ポインタの層切り替えヒステリシス (NDC v) */
  splitHysteresis: number;

  // ---- デバッグ表示 ----
  showSplitLine: boolean;
  showLaneFloor: boolean;
  showHorizon: boolean;
  showCellIndex: boolean;
  showTouchDebug: boolean;
  metronome: boolean;
}

export const DEFAULT_CONFIG: StageConfig = {
  phiDeg: 50,
  yCam: 10,

  vHorizon: 0.75,
  vSkyJudge: 0.35,
  vSkyTop: 0.52,
  vSkyBot: 0.18,
  vSplit: -0.1,
  vGroundTop: -0.34,
  vGroundBot: -0.8,
  vGroundJudge: -0.57,

  U: 0.84,
  cells: 12,

  scrollSpeed: 14,
  drawFar: 60,
  bpm: 150,
  windowPerfect: 45,
  windowGood: 100,
  splitHysteresis: 0.05,

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
