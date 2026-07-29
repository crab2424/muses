using System;

namespace Muses.Stage
{
    /// <summary>
    /// スクリーン空間 (NDC) → 3D の導出。
    ///
    /// 中核原理: 透視投影では画面位置が y_c / z_c の比のみで決まるため、
    /// 画面の1行 v はカメラから見た1つの俯角 psi に対応する。
    ///
    ///   psi(v) = theta - atan(v * tan(phi/2))
    ///   面 y = y_p に当たる水平奥行き  d = (y_cam - y_p) / tan psi
    ///   地平線                        v_horizon = tan theta / tan(phi/2)
    ///
    /// ワールド座標系: カメラは (0, yCam, 0) にあり、俯角 theta で -z 方向を向く。
    /// 「奥行き d」は水平距離であり、ワールド z = -d に対応する。
    ///
    /// 移植元: web-prototype/src/derive.ts（実機検証済みの数式。ロジックは変更せずそのまま移植）
    /// </summary>
    public struct Derived
    {
        public float aspect;
        public float tanHalfPhi;
        /// <summary>カメラ俯角 (rad)。cfg.thetaDeg を有効域にクランプしたもの</summary>
        public float theta;
        /// <summary>theta の有効域 (deg)。判定線2本の画面位置と phi から決まる</summary>
        public float thetaMinDeg;
        public float thetaMaxDeg;
        /// <summary>cfg.thetaDeg が有効域外でクランプされたか</summary>
        public bool thetaClamped;
        /// <summary>地平線の画面位置 (NDC v)。theta からの導出値。1 を超えると画面外</summary>
        public float vHorizon;
        /// <summary>最遠端の画面位置 (NDC v)。farFrac からの導出値。※これは地上面での位置</summary>
        public float vFar;

        /// <summary>判定線の奥行き（層に依らない単一の値。ノーツのタイミングはこれだけで決まる）</summary>
        public float zJudge;
        /// <summary>空中面の高さ</summary>
        public float skyHeight;

        /// <summary>各層の判定帯が対応する奥行き範囲（※タイミングとは無関係。参考値）</summary>
        public float groundBandNear;
        public float groundBandFar;
        public float skyBandNear;
        public float skyBandFar;

        /// <summary>判定線上での面の全幅</summary>
        public float groundWidth;
        public float skyWidth;
        /// <summary>判定線上でのセル1個の幅</summary>
        public float groundCellWidth;
        public float skyCellWidth;

        /// <summary>cfg.thetaDeg のクランプ後の sin/cos。laneX の計算に使う</summary>
        public float sinTheta;
        public float cosTheta;
        /// <summary>レーン x = u_k * laneK * zc(z) の係数（U * aspect * tan(phi/2)）</summary>
        public float laneK;
        /// <summary>地上面の最遠端における視線方向距離。層別の収束率補正の基準（LaneX 参照）</summary>
        public float zcFarGround;

        /// <summary>最遠端の奥行き。両層で共有する単一の値</summary>
        public float zFar;
        /// <summary>空中面の最遠端が画面上のどこに来るか（参考値）</summary>
        public float vFarSky;
        /// <summary>各層の面・ノーツが消える手前側の奥行き</summary>
        public float groundNear;
        public float skyNear;

        /// <summary>ワールド単位のスクロール速度。z_j も z_far も層共通なので速度も層共通</summary>
        public float speed;
        /// <summary>先読み時間 (秒)。両層とも同じ</summary>
        public float readAhead;

        /// <summary>カメラの far クリップに使う値</summary>
        public float drawFar;
    }

    public static class StageDerive
    {
        private static float Deg(float d) => d * MathF.PI / 180f;
        private static float Rad(float r) => r * 180f / MathF.PI;

        /// <summary>NDC の v に対応する俯角 psi (rad)</summary>
        public static float Psi(float theta, float tanHalfPhi, float v)
        {
            return theta - MathF.Atan(v * tanHalfPhi);
        }

        /// <summary>
        /// NDC の v と面の高さ y_p から、その面に当たる水平奥行き d を求める。
        ///
        /// psi >= 90 度は「その画面行がカメラ真下より後ろを向いている」状態で、面との交点は
        /// カメラ背後になる。手前端の指定に使われるので 0（＝手前は切らない）を返す。
        /// </summary>
        public static float DepthAt(float yCam, float yPlane, float psiRad)
        {
            if (psiRad <= 1e-6f) return float.PositiveInfinity; // 地平線より上 → 面に当たらない
            if (psiRad >= MathF.PI / 2f - 1e-6f) return 0f; // 真下より後ろ
            return (yCam - yPlane) / MathF.Tan(psiRad);
        }

        /// <summary>カメラ空間での視線方向距離 z_c。横幅の計算に使う</summary>
        public static float ViewDist(float yCam, float theta, float yPlane, float depth)
        {
            return (yCam - yPlane) * MathF.Sin(theta) + depth * MathF.Cos(theta);
        }

        /// <summary>視線方向距離 zc における、NDC u=1 に対応するワールド半幅</summary>
        private static float HalfWidthAt(float zc, float aspect, float tanHalfPhi)
        {
            return zc * aspect * tanHalfPhi;
        }

        /// <summary>
        /// レーンの x 座標。奥行き z における画面 u=u_k の位置を、
        /// cfg.laneConverge で「画面上の長方形 (0)」〜「ワールド平行 = 現行の台形 (1)」の間で連続的に補間する。
        /// 詳細な導出は derive.ts のコメントを参照（数式は変更せず移植）。
        /// </summary>
        public static float LaneX(StageConfig cfg, in Derived d, float u, float layerF, float z)
        {
            float yPlane = layerF * d.skyHeight;
            float a = (cfg.yCam - yPlane) * d.sinTheta;
            float zc = a + z * d.cosTheta;
            float zcJudge = a + d.zJudge * d.cosTheta;
            float zcFar = a + d.zFar * d.cosTheta;
            float c = MathF.Min(1f, MathF.Max(0f, cfg.laneConverge * (zcFar / d.zcFarGround)));
            float zcMix = zc + (zcJudge - zc) * c;
            return u * d.laneK * zcMix;
        }

        /// <summary>
        /// theta の有効域。判定線が地平線を越えず、かつ視野の裏側に回らない範囲。
        /// 端ちょうどでは奥行きが発散するのでマージン (psi in [1.5, 88.5] deg) を取る。
        /// </summary>
        public static (float min, float max) ThetaRange(StageConfig cfg, float tanHalfPhi)
        {
            float vTop = MathF.Max(cfg.vSkyJudge, cfg.vGroundJudge);
            float vBot = MathF.Min(cfg.vSkyJudge, cfg.vGroundJudge);
            return (
                Rad(MathF.Atan(vTop * tanHalfPhi) + Deg(1.5f)),
                Rad(MathF.Atan(vBot * tanHalfPhi) + Deg(88.5f))
            );
        }

        public static Derived Derive(StageConfig cfg, float aspectIn)
        {
            // アスペクト比が 0 や NaN だと全ワールド座標が NaN に伝播するので吸収しておく
            float aspect = float.IsFinite(aspectIn) && aspectIn > 0f ? aspectIn : 1f;
            float tanHalfPhi = MathF.Tan(Deg(cfg.phiDeg) / 2f);

            // 視点（俯角）が入力。地平線はここからの導出値
            var (thetaMinDeg, thetaMaxDeg) = ThetaRange(cfg, tanHalfPhi);
            float thetaDeg = MathF.Min(thetaMaxDeg, MathF.Max(thetaMinDeg, cfg.thetaDeg));
            float theta = Deg(thetaDeg);
            float P(float v) => Psi(theta, tanHalfPhi, v);
            float vHorizon = MathF.Tan(theta) / tanHalfPhi;

            // 判定線の奥行きは地上判定線の指定から決まる（地上面は y=0）
            float zJudge = DepthAt(cfg.yCam, 0f, P(cfg.vGroundJudge));

            // 空中面の高さは「空中判定線が同じ奥行き zJudge に来る」条件から決まる
            float skyHeight = cfg.yCam - zJudge * MathF.Tan(P(cfg.vSkyJudge));

            float gbFar = DepthAt(cfg.yCam, 0f, P(cfg.vGroundTop));
            float gbNear = DepthAt(cfg.yCam, 0f, P(cfg.vGroundBot));
            float sbFar = DepthAt(cfg.yCam, skyHeight, P(cfg.vSkyTop));
            float sbNear = DepthAt(cfg.yCam, skyHeight, P(cfg.vSkyBot));

            float zcGround = ViewDist(cfg.yCam, theta, 0f, zJudge);
            float zcSky = ViewDist(cfg.yCam, theta, skyHeight, zJudge);

            float halfGround = HalfWidthAt(zcGround, aspect, tanHalfPhi) * cfg.U;
            float halfSky = HalfWidthAt(zcSky, aspect, tanHalfPhi) * cfg.U;

            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);
            float laneK = cfg.U * aspect * tanHalfPhi;

            // 最遠端。「地上判定線 → 地平線（画面外なら画面上端）」の何割かで指定する。
            // 地平線ちょうどでは奥行きが発散するのでマージンを取る。
            float vCeil = MathF.Min(vHorizon - 0.02f, 1f);
            float frac = MathF.Min(0.995f, MathF.Max(0.02f, cfg.farFrac));
            float vFar = cfg.vGroundJudge + frac * (vCeil - cfg.vGroundJudge);
            // これを単一のワールド奥行きに変換し、空中面も同じ奥行きで切る。
            // → 最遠端の断面は奥行き一定の垂直な切り口になる。
            float zFar = MathF.Min(DepthAt(cfg.yCam, 0f, P(vFar)), zJudge * 200f);
            // 共有 zFar における空中面の画面位置（描画には使わない。ズレ量を GUI で見るための参考値）
            float vFarSky = MathF.Tan(theta - MathF.Atan((cfg.yCam - skyHeight) / zFar)) / tanHalfPhi;

            // 層別の収束率補正の基準（LaneX 参照）。zFar が確定してから求める
            float zcFarGround = ViewDist(cfg.yCam, theta, 0f, zFar);

            // 手前側の消える位置。空中は判定線で切ると見やすい（ユーザー要望）
            float groundNear = gbNear;
            float skyNear = cfg.skyFloorFromJudge ? zJudge : sbNear;

            // 最遠端も判定線も層共通の奥行きなので、速度も層共通で先読み時間は自動的に一致する。
            float readAhead = MathF.Max(cfg.readAheadSec, 0.05f);
            float speed = (zFar - zJudge) / readAhead;

            return new Derived
            {
                aspect = aspect,
                tanHalfPhi = tanHalfPhi,
                theta = theta,
                thetaMinDeg = thetaMinDeg,
                thetaMaxDeg = thetaMaxDeg,
                thetaClamped = MathF.Abs(thetaDeg - cfg.thetaDeg) > 1e-6f,
                vHorizon = vHorizon,
                vFar = vFar,
                zJudge = zJudge,
                skyHeight = skyHeight,
                groundBandNear = gbNear,
                groundBandFar = gbFar,
                skyBandNear = sbNear,
                skyBandFar = sbFar,
                groundWidth = halfGround * 2f,
                skyWidth = halfSky * 2f,
                groundCellWidth = (halfGround * 2f) / cfg.cells,
                skyCellWidth = (halfSky * 2f) / cfg.cells,
                sinTheta = sinTheta,
                cosTheta = cosTheta,
                laneK = laneK,
                zcFarGround = zcFarGround,
                zFar = zFar,
                vFarSky = vFarSky,
                groundNear = groundNear,
                skyNear = skyNear,
                speed = speed,
                readAhead = readAhead,
                drawFar = zFar * 1.15f,
            };
        }
    }
}
