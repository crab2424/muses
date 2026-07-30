using System;

namespace Muses.Stage
{
    /// <summary>
    /// ステージ設定。
    ///
    /// 設計原則: スクリーン空間 (NDC) の値が唯一の正。
    /// 3D のカメラ配置・面の高さ・面の幅・スクロール速度はすべてここから StageDerive で導出される。逆は不可。
    ///
    /// NDC の v は画面下端 -1 / 中央 0 / 上端 +1。u は左 -1 / 右 +1。
    /// 移植元: web-prototype/src/config.ts
    /// </summary>
    [Serializable]
    public class StageConfig
    {
        // ---- カメラ ----
        /// <summary>垂直画角 (deg)。水平画角はアスペクト比から決まる</summary>
        public float phiDeg;
        /// <summary>カメラ高さ。全ワールド寸法の基準スケール（これ自体に見た目上の意味はない）</summary>
        public float yCam;
        /// <summary>
        /// ステージの視点＝カメラ俯角 (deg)。地上面を基準に 0〜90。
        /// 有効域は StageDerive.ThetaRange が判定線の画面位置と phiDeg から算出し、範囲外はクランプされる。
        /// </summary>
        public float thetaDeg;

        // ---- スクリーン空間の権威値 (NDC v) ----
        public float vSkyJudge;
        public float vSkyTop;
        public float vSkyBot;
        /// <summary>層の境界（入力の layer 判定はこの1本のみ）</summary>
        public float vSplit;
        public float vGroundTop;
        public float vGroundBot;
        public float vGroundJudge;
        /// <summary>
        /// ステージ最遠端の位置。「地上判定線から地平線（地平線が画面外なら画面上端）まで」を
        /// 1 とした比率で指定する。両層で共有する単一のワールド奥行き z_far に変換される。
        /// </summary>
        public float farFrac;

        // ---- 横方向 ----
        /// <summary>判定帯の半幅 (NDC u)。0.87 = 画面幅の 87%</summary>
        public float U;
        /// <summary>1層あたりのセル数</summary>
        public int cells;

        // ---- ゲームプレイ ----
        /// <summary>ノーツが最遠端 z_far から判定線 z_j まで来るのにかかる秒数 = 先読み時間</summary>
        public float readAheadSec;
        /// <summary>譜面の BPM（デモ譜面生成用）</summary>
        public float bpm;
        /// <summary>判定ウィンドウ (ms)。※暫定値、未設計項目</summary>
        public float windowPerfect;
        public float windowGood;
        /// <summary>押下中ポインタの層切り替えヒステリシス (NDC v)</summary>
        public float splitHysteresis;
        /// <summary>
        /// 判定オフセット (ms)。音と入力のズレ補正。正の値 = 入力を遅らせて評価する
        /// （実機の出力レイテンシ分、判定を「音が実際に鳴った後」にずらす想定）。
        /// 実機キャリブレーション用の値なので <see cref="OffsetSettings"/> でPlayerPrefsに永続化する。
        /// </summary>
        public float judgeOffsetMs;
        /// <summary>
        /// 描画オフセット (ms)。音とノーツ描画位置のズレ補正。judgeOffsetMsとは独立
        /// （判定の基準時刻と、ノーツを画面上どこに置くかの基準時刻は別物になりうるため）。
        /// </summary>
        public float visualOffsetMs;

        // ---- 見た目 ----
        /// <summary>最遠端をぼかさずはっきり切る</summary>
        public bool hardFarEdge;
        /// <summary>
        /// レーンの収束具合。0 = 画面上で完全な長方形、1 = 現行のワールド平行レーン（画面上は台形）。
        /// </summary>
        public float laneConverge;
        /// <summary>レーン境界線の間引き（層別）。1 = 全部、3 = 3セルごと</summary>
        public int laneLineStepGround;
        public int laneLineStepSky;
        /// <summary>空中面を判定線で切る（判定線より手前＝時間的に以降を描かない）</summary>
        public bool skyFloorFromJudge;
        /// <summary>ステージ面の塗りつぶし不透明度（層別）</summary>
        public float groundFillAlpha;
        public float skyFillAlpha;
        /// <summary>背景色 (#rrggbb)</summary>
        public string bgColor;

        // ---- デバッグ表示 ----
        public bool showBand;
        public bool showJudgeLine;
        public bool showSplitLine;
        public bool showLaneFloor;
        public bool showHorizon;
        public bool showCellIndex;
        public bool showTouchDebug;
        public bool metronome;

        /// <summary>実機でチューニング済みの値。移植元: memory/settings.json（config.ts の DEFAULT_CONFIG より優先する正）</summary>
        public static StageConfig Default() => new StageConfig
        {
            phiDeg = 77f,
            yCam = 8f,
            thetaDeg = 38f,

            vSkyTop = 1f,
            vSkyJudge = 0.15f,
            vSkyBot = -0.1f,
            vSplit = -0.1f,
            vGroundTop = -0.1f,
            vGroundJudge = -0.75f,
            vGroundBot = -1f,
            farFrac = 0.9f,

            U = 0.87f,
            cells = 12,

            readAheadSec = 1.2f,
            bpm = 150f,
            windowPerfect = 40f,
            windowGood = 100f,
            splitHysteresis = 0.05f,
            judgeOffsetMs = 0f,
            visualOffsetMs = 0f,

            hardFarEdge = true,
            laneConverge = 1f,
            laneLineStepGround = 3,
            laneLineStepSky = 12,
            skyFloorFromJudge = true,
            groundFillAlpha = 1f,
            skyFillAlpha = 0.05f,
            bgColor = "#a0b298",

            showBand = false,
            showJudgeLine = true,
            showSplitLine = false,
            showLaneFloor = true,
            showHorizon = false,
            showCellIndex = false,
            showTouchDebug = false,
            metronome = false,
        };
    }

    /// <summary>層の識別子。Ground=0 が地上、Sky=1 が空中。将来 layer=2 を足せる設計</summary>
    public enum Layer
    {
        Ground = 0,
        Sky = 1,
    }

    public static class StageColors
    {
        public const uint Ground = 0x8b5cf6; // 紫（地上）
        public const uint Sky = 0xff3ea5; // マゼンタ（空中）
        public const string GroundCss = "#8b5cf6";
        public const string SkyCss = "#ff3ea5";
        public const uint GridGround = 0x3a2f6b;
        public const uint GridSky = 0x6b2a55;
    }
}
