// ノーツ本体（スキン「ネオン」、既定スキン）。移植元: design-lab/note-skin/skins/neon.js（r3 §7〜§11 で確定）。
// 方針・経緯は memory/game/note-design-lab-r3.md。
//   - Tap / Ex Tap / Flick: 板1枚のインポスターで描くネオンドーム。頂点シェーダでノーツの中心と倍率を求め、
//     カメラをノーツのローカル空間へ移し、フラグメントでレイ vs 楕円柱＋楕円体（Flick は楕円錐）を解く。
//   - Slide: 白い芯＋色ハローの縁を持つ発光帯（刻み・パルスなし）。始点・Visible 中継点はドーム状のマーカー。
//   - Riser / Diver: 発光壁＋分厚い ∧（∨）1枚が流れる。∧ は腕ごとのクアッドで、層位置を頂点シェーダで決める。
//     ∧ の位相は **実時間（_SongTime − ノーツの実時刻）** 基準。スクロールグループの停止・逆走
//     （note-spec.md §5.5）に関係なく常に一定の速さで流れ、判定時刻に先端が到達点へ届く。
//   - 白線（縁・レール・輪郭）は「判定線上での幅 × 見かけの倍率」で細くなり、下限 _EdgeMinPx・全体倍率 _EdgeScale（MusesEdge）。
//
// 出力はすべて premultiplied（Blend One OneMinusSrcAlpha、ラボの museOut と同じ「通常合成 α ＋ 加算ぶん」）。
// ラボ(three.js)は sRGB 空間でブレンドしていたが、Unity は Linear 空間でブレンドするので、半透明・発光の重なりの
// 明るさは実機で再確認すること（r3 §11）。
//
// ZTest Always: ノーツは地面からごくわずか(zJudge*0.002)しか浮かせておらず、遠距離では
// デプスバッファの精度不足で地面とのZファイティングが起きる。描画順は renderQueue
// （NoteView.cs）とメッシュ内の頂点順（NoteDrawOrder）で保証しているのでデプステスト自体が不要。
Shader "Muses/Note"
{
    Properties
    {
        _ZJudge ("Z Judge", Float) = 0
        _Speed ("Speed", Float) = 1
        _Far ("Far", Float) = 100
        _HardFar ("Hard Far", Float) = 1
        _YCam ("Y Cam", Float) = 8
        _SkyHeight ("Sky Height", Float) = 6
        _SinTheta ("Sin Theta", Float) = 0
        _CosTheta ("Cos Theta", Float) = 1
        _LaneK ("Lane K", Float) = 1
        _LaneConverge ("Lane Converge", Float) = 1
        _ZcFarGround ("Zc Far Ground", Float) = 1
        _ThicknessFrac ("Thickness Frac", Float) = 0.025
        _ThicknessMinFrac ("Thickness Min Frac", Float) = 0.004
        _TanHalfPhi ("Tan Half Phi", Float) = 1
        // note-visual-r1.md §3.2: 空中ノーツの画面上の厚みを地上と揃えるための層依存係数。
        _SkyThicknessMul ("Sky Thickness Mul", Float) = 1.96

        // r3 §9/§10: 白線の下限(px)と全体倍率。NoteView の edgeMinPx / edgeScale から設定する。
        _EdgeMinPx ("Edge Min Px", Float) = 0.5
        _EdgeScale ("Edge Scale", Float) = 1

        // gameplay-feel-r1.md §5.3。NoteView がノーツメッシュを3つのサブメッシュ（地上帯/空中帯/その他）に分け、
        // それぞれ別マテリアルで設定する（地上帯だけステンシルで重なりを4枚までに制限）。
        [HideInInspector] _StencilRef ("Stencil Ref", Float) = 0
        [HideInInspector] _StencilComp ("Stencil Comp", Float) = 8 // Always
        [HideInInspector] _StencilPass ("Stencil Pass", Float) = 0 // Keep
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
            }
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Include/NotePlacement.hlsl"

            // NoteView が毎フレーム/ビルド時に設定する（NotePlacement.hlsl の CBUFFER の外。既存の _GroupX と同じ扱い）
            float _SongTime;   // 実時間（スクロールグループの X(t) を通さない）。∧ の位相に使う
            float _GroundNear; // 層ごとの手前端（∧ の頂点は層が動くので、手前端をシェーダで求める）
            float _SkyNear;
            float _EdgeMinPx;
            float _EdgeScale;

            // ---- ∧ の寸法（design-lab/note-skin/skins/shared/chevron.js の CHEVRON_DEFAULTS）----
            // TH・SL は NoteGeometry.cs の ChevronTh / ChevronSl と一致させること（腕の頂点のずれは C# 側で焼く）。
            #define CHEV_TH 0.32      // 厚み（層）
            #define CHEV_SL 0.30      // 傾き（層）。中央→端での下がり量
            #define CHEV_PAD 0.05     // 周期の余白（層）
            #define CHEV_CYCLE 0.5    // 1周期の秒数（Riser の高さに依らず共通、r3 §10）

            // ---- Tap 系インポスター（neon.js の HR / EXT_NEAR）----
            #define TAP_HR 1.5        // 高さ/半奥行
            #define TAP_EXT_NEAR 0.15 // 手前側へ伸ばす量

            // 種別タグ（localUv.y、NoteGeometry.NoteMeshData.localUv 参照）
            // 0=Slide帯 / 1=Tap・ExTap / 2=Flick / 3=Slideマーカー / 4=∧の腕 / -1=Riser壁

            struct Attributes
            {
                float4 positionOS : POSITION;   // (u, y, X(ノーツ時刻))
                float4 color : COLOR;           // リニア
                float2 uv0 : TEXCOORD0;         // x = state, y = near
                float2 uv1 : TEXCOORD1;         // x = layerF, y = side
                float2 uv2 : TEXCOORD2;         // x = scrollGroup, y = slideEatable
                float2 uv3 : TEXCOORD3;         // x = 横のローカル座標(0..1)、∧は腕の断面(-1/+1) / y = 種別タグ
                float4 uv4 : TEXCOORD4;         // 種別ごとの追加データ（NoteGeometry.NoteMeshData.extra 参照）
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : TEXCOORD0;
                float4 sdns : TEXCOORD1;   // state, depth, near, side
                float4 misc : TEXCOORD2;   // localX, tag, slideEatable, scale（見かけの倍率）
                float4 extra : TEXCOORD3;  // 壁: (k, span) / ∧: (m, span) / Tap: (a, b, H, T)
                float3 tapRo : TEXCOORD4;  // Tap: ローカル空間のカメラ位置
                float3 tapS : TEXCOORD5;   // Tap: ローカル→ワールドの倍率
                float4 tapQ : TEXCOORD6;   // Tap: (板上の点 x, z, ExTap(0/1), 中心の奥行き)
            };

            // ラボの色（sRGB の16進）をリニアへ。定数なのでコンパイル時に畳まれる。
            float3 SrgbToLinear3(float3 c)
            {
            #ifdef UNITY_COLORSPACE_GAMMA
                return c;
            #else
                return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, (float3)0.04045));
            #endif
            }

            // design-lab/note-skin/skins/shared/edge.js の museEdge。
            // basePx = 判定線上（scale=1）での白線の幅(px)、scale = その地点の見かけの倍率。
            // 戻り値 x = AA 用に 1px 以上に丸めた幅、y = 濃さ（実際の幅が 1px 未満ならその割合で薄くする）。
            float2 MusesEdge(float basePx, float scale)
            {
                float w = max(basePx * _EdgeScale * scale, _EdgeMinPx);
                return float2(max(w, 1.0), saturate(w));
            }

            // ---------------- 頂点: ∧ の腕（chevron.js の chevronTipM / chevronPlace）----------------
            float ChevronTipM(float noteT, float span)
            {
                float P = span + (CHEV_TH + CHEV_SL) + CHEV_PAD;
                // 判定時刻（_SongTime == noteT）に先端が到達点（m = span）へ届く。GLSL の mod と同じ floor 基準で折り返す
                float x = (_SongTime - noteT) / CHEV_CYCLE * P + span + CHEV_PAD;
                return x - P * floor(x / P) - (CHEV_TH * 0.5 + CHEV_PAD);
            }

            // 全頂点で共通の基準層 Lc（先端中心の層を壁の範囲にクランプ）で壁上の位置と層方向の微分を取り、
            // 頂点ごとの層差ぶんその方向へずらす（∧ 全体が一緒に動き、腕は画面上で直線のまま。r3 §10）。
            float3 ChevronPlace(float3 pos, float baseLayer, float4 c, float groupX,
                out float layer, out float m, out float depth, out float scale)
            {
                float offsetM = c.x, span = c.y, dirSign = c.z, noteT = c.w;
                float yUp = pos.y - baseLayer * _SkyHeight;
                float tipM = ChevronTipM(noteT, span);
                float Lc = baseLayer + dirSign * clamp(tipM, 0.0, span);
                m = tipM + offsetM;
                layer = baseLayer + dirSign * m;
                float d1, s1;
                float3 p0 = PlaceNoteCore(pos.x, Lc * _SkyHeight + yUp, pos.z, Lc, 0.0, groupX, depth, scale);
                float3 pL = PlaceNoteCore(pos.x, (Lc + 0.01) * _SkyHeight + yUp, pos.z, Lc + 0.01, 0.0, groupX, d1, s1);
                float3 p = p0 + (layer - Lc) * (pL - p0) / 0.01;
                depth = p.z;
                return p;
            }

            // ---------------- 頂点: Tap 系インポスター（neon.js の TAP_VERT ＋ note-tap/stage.js の placeNote）----------------
            // ラボはノーツ1個ずつ CPU で「中心へ平行移動＋(sx, sx, sz) 倍」していた。ここでは同じ中心と倍率を頂点シェーダで求める。
            // ローカル座標はラボと同じ: x = 横、y = 上、z = 手前が +（Unity のワールド z とは逆向き）。
            float3 TapPlace(Attributes IN, float groupX, out float depth, out float scale,
                out float3 ro, out float3 S, out float2 q, out float4 dims, out float centerDepth)
            {
                float L = IN.uv1.x;
                float uL = IN.uv4.x, uR = IN.uv4.y;
                float d0 = _ZJudge + (IN.positionOS.z - groupX) * _Speed;
                float htNom = _ZJudge * _ThicknessFrac;
                float ht = MusesHalfThickness(d0, L);
                float dn = MusesRemapDepth(d0 - ht, L);
                float df = MusesRemapDepth(d0 + ht, L);
                float dc = 0.5 * (dn + df);
                float zcJG = MusesZcJudgeGround();
                float zcMix = MusesZcMix(L, dc);
                float sx = zcMix / zcJG;
                float sz = max((df - dn) / (2.0 * htNom), 1e-4);
                float3 center = float3((uL + uR) * 0.5 * _LaneK * zcMix, IN.positionOS.y, dc);

                float a = (uR - uL) * 0.5 * _LaneK * zcJG; // 半長（判定線上・地上のワールド単位）
                float b = htNom;                           // 半奥行
                float H = b * TAP_HR;                      // 高さ
                dims = float4(a, b, H, min(b * 2.4, a));   // w = Flick の尖り長

                S = float3(sx, sx, sz);
                float3 cam = TransformWorldToObject(_WorldSpaceCameraPos);
                ro = float3((cam.x - center.x) / sx, (cam.y - center.y) / sx, -(cam.z - center.z) / sz);

                // 板は奥側（side=+1）へ伸ばし、横にも広げて、ドームの側面・天面が板からはみ出さないようにする
                float lx = (IN.uv3.x * 2.0 - 1.0) * a;
                float lz;
                if (IN.uv1.y > 0.0)
                {
                    float den = max(ro.y - H, 0.05 * b);
                    float ext = max(ro.z, 0.0) * H / den;
                    lz = -min(max(ext * 1.08 + 0.1 * b, b * 1.05), b * 6.0);
                    lx = ro.x + (lx - ro.x) * (1.0 + 1.1 * H / den);
                }
                else
                {
                    lz = b * (1.0 + TAP_EXT_NEAR);
                }
                q = float2(lx, lz);
                scale = sx;
                centerDepth = dc;
                depth = dc - lz * sz;
                return float3(center.x + lx * sx, center.y, depth);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                int group = clamp((int)(IN.uv2.x + 0.5), 0, MUSES_MAX_SCROLL_GROUPS - 1);
                float groupX = _GroupX[group];
                float tag = IN.uv3.y;
                float layerF = IN.uv1.x;
                float nearD = IN.uv0.y;
                float depth, scale;
                float3 os;
                OUT.extra = IN.uv4;
                OUT.tapS = float3(1, 1, 1);

                if (tag > 3.5)
                {
                    float layer, m;
                    os = ChevronPlace(IN.positionOS.xyz, layerF, IN.uv4, groupX, layer, m, depth, scale);
                    nearD = lerp(_GroundNear, _SkyNear, saturate(layer));
                    OUT.extra = float4(m, IN.uv4.y, 0, 0);
                }
                else if (tag > 0.5 && tag < 2.5)
                {
                    float3 ro, S; float2 q; float4 dims; float dc;
                    os = TapPlace(IN, groupX, depth, scale, ro, S, q, dims, dc);
                    OUT.tapRo = ro;
                    OUT.tapS = S;
                    OUT.tapQ = float4(q, IN.uv4.z, dc);
                    OUT.extra = dims;
                }
                else
                {
                    float d0 = _ZJudge + (IN.positionOS.z - groupX) * _Speed;
                    os = PlaceNoteCore(IN.positionOS.x, IN.positionOS.y, IN.positionOS.z, layerF,
                        IN.uv1.y * MusesHalfThickness(d0, layerF), groupX, depth, scale);
                }

                OUT.positionCS = TransformWorldToHClip(TransformObjectToWorld(os));
                OUT.color = IN.color;
                OUT.sdns = float4(IN.uv0.x, depth, nearD, IN.uv1.y);
                OUT.misc = float4(IN.uv3.x, tag, IN.uv2.y, scale);
                return OUT;
            }

            // ---------------- フラグメント: Tap 系（neon.js の TAP_FRAG）----------------
            float HitEllipsoid(float3 ro, float3 rd, float3 c, float3 r)
            {
                float3 o = (ro - c) / r, d = rd / r;
                float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
                float D = B * B - A * C;
                if (D < 0.0) return -1.0;
                return (-B - sqrt(D)) / A;
            }

            // 端の尖り: 楕円錐 (z/b)^2+(y/H)^2 = ((T-xs)/T)^2, xs=端の始まりからの距離(0..T), y>=0。先端は地面の1点。
            float HitCone(float3 o, float3 d, float T, float b, float H)
            {
                float b2 = b * b, h2 = H * H, t2 = T * T;
                float A = d.z * d.z / b2 + d.y * d.y / h2 - d.x * d.x / t2;
                float B = o.z * d.z / b2 + o.y * d.y / h2 + (T - o.x) * d.x / t2;
                float C = o.z * o.z / b2 + o.y * o.y / h2 - (T - o.x) * (T - o.x) / t2;
                if (abs(A) < 1e-9) return -1.0;
                float D = B * B - A * C;
                if (D < 0.0) return -1.0;
                float s = sqrt(D);
                float t1 = (-B - s) / A, t2r = (-B + s) / A;
                if (t1 > t2r) { float tmp = t1; t1 = t2r; t2r = tmp; }
                float3 p1 = o + d * t1, p2 = o + d * t2r;
                if (t1 > 0.0 && p1.x >= 0.0 && p1.x <= T && p1.y >= 0.0) return t1;
                if (t2r > 0.0 && p2.x >= 0.0 && p2.x <= T && p2.y >= 0.0) return t2r;
                return -1.0;
            }

            // ドームの断面（角度 0,22,45,66,82 度＋天頂）の法線。neon.js の RINGS（HR=1.5）を展開した値。
            static const float kRingD[6] = { 1.00000, 0.92718, 0.70711, 0.40674, 0.13917, 0.00000 };
            static const float kRingNH[6] = { 1.00000, 0.96559, 0.83205, 0.55538, 0.20628, 0.00000 };
            static const float kRingNY[6] = { 0.00000, 0.26008, 0.55470, 0.83160, 0.97849, 1.00000 };

            // 戻り値 rgb、hit = 形に当たったか
            float3 FragTap(Varyings IN, out bool hit)
            {
                float uA = IN.extra.x, uB = IN.extra.y, uH = IN.extra.z;
                bool fl = IN.misc.y > 1.5;
                bool ex = IN.tapQ.z > 0.5;
                float3 uColor = IN.color.rgb;
                float3 uDeep, uCore, uRim;
                if (fl)      { uDeep = SrgbToLinear3(float3(0x8f, 0x14, 0x24) / 255.0); uCore = SrgbToLinear3(float3(0xff, 0xd6, 0xd6) / 255.0); uRim = float3(1.0, 0.62, 0.66); }
                else if (ex) { uDeep = SrgbToLinear3(float3(0x8a, 0x5d, 0x05) / 255.0); uCore = SrgbToLinear3(float3(0xff, 0xf3, 0xc2) / 255.0); uRim = float3(1.0, 0.88, 0.5); }
                else         { uDeep = SrgbToLinear3(float3(0x0d, 0x3a, 0x8f) / 255.0); uCore = SrgbToLinear3(float3(0xd6, 0xf0, 0xff) / 255.0); uRim = float3(0.55, 0.8, 1.0); }

                float T = min(IN.extra.w, uA);
                float cx = fl ? max(uA - T, 0.0) : max(uA - uB, 0.0);
                float3 ro = IN.tapRo;
                float3 qp = float3(IN.tapQ.x, 0.0, IN.tapQ.y);
                float3 rd = normalize(qp - ro);

                float tBest = 1e9;
                {
                    float2 o = float2(ro.z / uB, ro.y / uH), d = float2(rd.z / uB, rd.y / uH);
                    float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
                    float D = B * B - A * C;
                    if (D >= 0.0)
                    {
                        float t = (-B - sqrt(D)) / A;
                        float3 p = ro + rd * t;
                        if (abs(p.x) <= cx && p.y >= 0.0 && t > 0.0) tBest = t;
                    }
                }
                [unroll] for (int i = 0; i < 2; i++)
                {
                    float sg = i == 0 ? 1.0 : -1.0;
                    float t = fl
                        ? HitCone(float3(ro.x * sg - cx, ro.y, ro.z), float3(rd.x * sg, rd.y, rd.z), T, uB, uH)
                        : HitEllipsoid(ro, rd, float3(sg * cx, 0.0, 0.0), float3(uB, uH, uB));
                    if (t > 0.0)
                    {
                        float3 p = ro + rd * t;
                        if (p.x * sg >= cx - 1e-5 && p.y >= 0.0 && t < tBest) tBest = t;
                    }
                }
                hit = tBest < 1e8;
                float3 p = ro + rd * (hit ? tBest : 0.0);

                float vC; float3 nl;
                if (fl)
                {
                    float xs = max(abs(p.x) - cx, 0.0);
                    float g = clamp((T - xs) / T, 0.02, 1.0);
                    vC = hit ? saturate(1.0 - abs(p.z) / (uB * g)) : 0.0;
                    if (xs > 0.0) nl = float3(sign(p.x) * 2.0 * (T - xs) / (T * T), 2.0 * p.y / (uH * uH), 2.0 * p.z / (uB * uB));
                    else nl = float3(0.0, p.y / (uH * uH), p.z / (uB * uB));
                    nl = normalize(nl);
                }
                else
                {
                    float exx = p.x - clamp(p.x, -cx, cx);
                    float2 hz = float2(exx, p.z);
                    float dist = length(hz);
                    vC = hit ? saturate(1.0 - dist / uB) : 0.0;
                    float2 hdir = dist > 1e-5 ? hz / dist : float2(1.0, 0.0);
                    float dn = saturate(dist / uB);
                    float nh = kRingNH[5], ny = kRingNY[5];
                    [unroll] for (int k = 0; k < 5; k++)
                    {
                        if (dn <= kRingD[k] && dn >= kRingD[k + 1])
                        {
                            float f = (dn - kRingD[k + 1]) / (kRingD[k] - kRingD[k + 1]);
                            nh = lerp(kRingNH[k + 1], kRingNH[k], f);
                            ny = lerp(kRingNY[k + 1], kRingNY[k], f);
                        }
                    }
                    nl = normalize(float3(nh * hdir.x, ny, nh * hdir.y));
                }
                float3 vS = IN.tapS;
                float3 N = normalize(nl / (vS * vS));
                float3 V = normalize((ro - p) * vS);

                float3 Lt = normalize(float3(-0.4, 0.8, 0.45));
                float ndv = saturate(dot(N, V));
                float fres = pow(1.0 - ndv, 2.2);
                // 遠方（ノーツの画面上の高さが小さい）ほど、白い縁・芯・リム・ハイライトを弱めて本体色を主にする
                float fw = max(fwidth(vC), 1e-5);
                float dPx = vC / fw, bPx = 1.0 / fw;   // bPx = 半奥行の画面上ピクセル数
                float nearF = smoothstep(4.0, 14.0, bPx);
                // r3 §13: 手前の縁（深色）が強く全体に暗い、の指摘で深色を本体色へ寄せ・縁の幅を狭め・陰影の下限を上げた
                float body = smoothstep(0.0, 0.45, vC);
                float3 rgb = lerp(lerp(uDeep, uColor, 0.45), uColor, body);
                rgb *= 0.92 + 0.25 * max(dot(N, Lt), 0.0);
                float core = smoothstep(0.62, 1.0, vC);
                rgb = lerp(rgb, uCore, core * 0.85 * lerp(0.25, 1.0, nearF));
                rgb += uRim * fres * 0.55 * lerp(0.3, 1.0, nearF);
                float3 Hh = normalize(Lt + V);
                rgb += pow(max(dot(N, Hh), 0.0), 60.0) * 0.6 * nearF;
                // 遠方はドームの陰影をやめ、本体色（やや明るめ）でほぼ均一に塗る
                rgb = lerp(min(uColor * 1.12 + 0.03, (float3)1.0), rgb, nearF);
                float2 e = MusesEdge(1.3, vS.x);   // 輪郭: 判定線上 1.3px × 見かけの倍率
                float ol = (1.0 - smoothstep(e.x - 0.5, e.x + 0.5, dPx)) * e.y;
                // 縁は近くでは白、遠くでは本体色の明るい版へ寄せる（細く・弱く）
                float3 olC = lerp(min(uColor * 1.25 + 0.08, (float3)1.0), float3(0.95, 0.98, 1.0), nearF);
                rgb = lerp(rgb, olC, ol * lerp(0.5, 1.0, nearF));
                return rgb;
            }

            float RoundedBoxSDF(float2 p, float2 b, float r)
            {
                float2 q = abs(p) - b + r;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float state = IN.sdns.x, depth = IN.sdns.y, nearD = IN.sdns.z, side = IN.sdns.w;
                float localX = IN.misc.x, tag = IN.misc.y, scale = IN.misc.w;
                bool isTap = tag > 0.5 && tag < 2.5;

                if (state <= 0.001) discard;
                // Tap 系は板を奥へ伸ばしているので、最遠端はノーツ中心の奥行きで判定する（ラボと同じくノーツ単位で消える）
                float farDepth = isTap ? IN.tapQ.w : depth;
                if (farDepth > _Far) discard;
                float aFar = _HardFar > 0.5 ? 1.0 : 1.0 - smoothstep(_Far * 0.7, _Far, farDepth);
                // 手前端でフェードアウト。範囲を狭くして面の終端の先へノーツがはみ出さないようにする
                float aNear = smoothstep(nearD * 0.90, nearD, depth);

                // ipad-test-findings-r1.md §④ / gameplay-feel-r1.md §5.4: 判定線を通過した Slide 区間（とその中継点マーカー）を
                // 「食べる」。fwidth は分岐の外で必ず計算する。
                float eat = saturate((depth - _ZJudge) / max(fwidth(depth), 1e-5));
                float fade = state * aFar * aNear * lerp(1.0, eat, IN.misc.z);
                if (fade <= 0.003) discard;

                float3 base = IN.color.rgb;
                float3 rgb;
                float alpha, add; // 出力 = (rgb * (alpha + add), alpha)。ラボの museOut と同じ

                if (isTap)
                {
                    bool hit;
                    rgb = FragTap(IN, hit);
                    if (!hit) discard;
                    alpha = 1.0; add = 0.0;
                }
                else if (tag > 3.5)
                {
                    // ---- ∧ の腕: 範囲外（根元より下・到達点より上）は捨てる ----
                    float m = IN.extra.x, span = max(IN.extra.y, 0.05);
                    float fm = max(fwidth(m), 1e-5);
                    float inR = smoothstep(0.0, fm, m) * smoothstep(0.0, fm, span - m);
                    if (inR <= 0.003) discard;
                    float armSide = localX; // ∧ は localUv.x に腕の断面(-1/+1)を入れている
                    float fs = max(fwidth(armSide), 1e-5);
                    float2 eA = MusesEdge(2.0, scale);
                    float edge = smoothstep(1.0 - eA.x * fs, 1.0, abs(armSide)) * eA.y; // 腕の上下の縁だけ白く
                    rgb = lerp(base, (float3)1.0, 0.12 + 0.6 * edge);
                    alpha = 0.75 * inR; add = 0.30 * inR;
                }
                else if (tag > 2.5)
                {
                    // ---- Slide マーカー: ドーム状の発光（Tap と同素材・帯の色）----
                    float2 uv = float2(localX, side * 0.5 + 0.5);
                    float2 p = uv - 0.5;
                    float2 duv = float2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
                    float2 pPx = p / duv, bPx = 0.5 / duv;
                    float rPx = min(0.5 / max(duv.x, duv.y) * 0.5, min(bPx.x, bPx.y) * 0.98);
                    float dist = RoundedBoxSDF(pPx, bPx, rPx);
                    float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
                    if (shapeA <= 0.003) discard;
                    float2 eM = MusesEdge(1.5, scale);
                    float w = min(eM.x, max(0.0, bPx.y - 0.75) * 0.5);
                    float outline = smoothstep(-w - 0.5, -w + 0.5, dist) * eM.y;
                    float depthPx = saturate(-dist / max(bPx.y, 1.0));
                    float dome = sqrt(1.0 - (1.0 - depthPx) * (1.0 - depthPx));
                    float rim = pow(1.0 - dome, 2.5);
                    rgb = base * (0.55 + 0.7 * dome);
                    rgb = lerp(rgb, (float3)1.0, smoothstep(0.75, 1.0, dome) * 0.55);
                    rgb += base * rim * 0.6;
                    rgb = lerp(rgb, (float3)1.0, outline);
                    alpha = 0.95 * shapeA; add = 0.25 * shapeA;
                }
                else if (tag > -0.5)
                {
                    // ---- Slide 帯（横線の刻みなし）: 左右の白い芯＋色ハロー、中央の芯線 ----
                    float du = max(fwidth(localX), 1e-5);
                    float xe = min(localX, 1.0 - localX);
                    float haloPx = 6.0 * scale;
                    float haloW = min(haloPx * du, 0.3);
                    float halo = exp(-xe / max(haloW, 1e-4) * 2.2) * saturate(haloPx);
                    float2 eE = MusesEdge(1.4, scale);
                    float core = (1.0 - smoothstep(eE.x - 0.5, eE.x + 0.5, xe / du)) * eE.y;
                    float xc = abs(localX - 0.5);
                    float2 eC = MusesEdge(1.0, scale);
                    float cCore = (1.0 - smoothstep(eC.x - 0.5, eC.x + 0.5, xc / du)) * eC.y;
                    float cHaloPx = 3.0 * scale;
                    float cHalo = exp(-xc / max(min(cHaloPx * du, 0.1), 1e-4) * 2.0) * 0.35 * saturate(cHaloPx);
                    float glow = 0.10 + halo * 0.55 + cHalo;
                    rgb = lerp(base, (float3)1.0, saturate(core * 0.9 + cCore * 0.85));
                    alpha = 0.10;
                    add = clamp(glow + core * 0.6 + cCore * 0.6, 0.0, 1.6) * 0.9;
                }
                else
                {
                    // ---- Riser / Diver の壁: 薄い発光面＋左右の白い芯とハロー＋到達点の白線 ----
                    float k = IN.extra.x;
                    float du = max(fwidth(localX), 1e-5);
                    float xe = min(localX, 1.0 - localX);
                    float haloPx = 7.0 * scale;
                    float haloW = min(haloPx * du, 0.3);
                    float halo = exp(-xe / max(haloW, 1e-4) * 2.2) * saturate(haloPx);
                    float2 eE = MusesEdge(1.5, scale);
                    float core = (1.0 - smoothstep(eE.x - 0.5, eE.x + 0.5, xe / du)) * eE.y;
                    float dk = max(fwidth(k), 1e-5);
                    float2 eG = MusesEdge(1.6, scale);
                    float goal = (1.0 - smoothstep(eG.x - 0.5, eG.x + 0.5, (1.0 - k) / dk)) * eG.y;
                    float gHaloPx = 8.0 * scale;
                    float goalHalo = exp(-(1.0 - k) / max(min(gHaloPx * dk, 0.25), 1e-4) * 2.2) * 0.5 * saturate(gHaloPx);
                    float wall = 0.10 * lerp(0.55, 1.0, k);
                    rgb = lerp(base, (float3)1.0, saturate(core * 0.9 + goal * 0.9));
                    alpha = 0.05;
                    add = clamp(wall + halo * 0.55 + goalHalo + core * 0.6 + goal * 0.8, 0.0, 1.6);
                }

                alpha *= fade;
                add *= fade;
                if (alpha + add <= 0.003) discard;
                return half4(rgb * (alpha + add), alpha);
            }
            ENDHLSL
        }
    }
}
