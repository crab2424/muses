// 天上の聖域: 雲海（スクリーン空間の板1枚）。移植元: sanctuary-r3.js の cloud。
// レイキャストは BgRayDir、模様の計算はラボと同じ式にするためラボ座標（z 反転）の方向ベクトルで行う。
Shader "Muses/Background/SanctuaryCloud"
{
    Properties
    {
        _StageHalf ("Stage half width (a, b): half(z) = a + b*z", Vector) = (7, 0.05, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/MusesBackground.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _StageHalf;
            CBUFFER_END
            float4 _Isl[6];   // 浮島の落ち影: (x, z, 半径^2*0.8, 0)（ラボ座標の xz）

            #define CLOUD_Y -9.0

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 ndc : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.ndc = IN.positionOS.xy;
                OUT.positionCS = BgScreenClip(IN.positionOS.xy);
                return OUT;
            }

            float h21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vn(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(h21(i), h21(i + float2(1, 0)), f.x), lerp(h21(i + float2(0, 1)), h21(i + float2(1, 1)), f.x), f.y);
            }
            float fbm(float2 p)
            {
                float a = 0.5, s = 0.0;
                [unroll] for (int i = 0; i < 4; i++) { s += a * vn(p); p = p * 2.03 + float2(17.1, 9.2); a *= 0.5; }
                return s;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 ndc = IN.ndc;
                float uTime = _MusesBgTime;
                float3 dirU = BgRayDir(ndc);
                float3 dir = float3(dirU.x, dirU.y, -dirU.z);          // ラボ座標の視線方向
                float camY = _MusesBgCamPos.y;
                float2 camXZ = float2(_MusesBgCamPos.x, -_MusesBgCamPos.z);
                float3 hazeB = float3(0.70, 0.78, 0.96), hazeW = float3(0.98, 0.76, 0.74);

                // 色の層: 大きな尺度の低周波ノイズがゆっくり移ろう（桃・薄紫・琥珀）
                float dyc = min(dir.y, -0.02);
                float band = vn(dir.xz * (-8.0 / dyc) * 0.022 + float2(uTime * 0.0025, 3.0 - uTime * 0.0015));
                float band2 = vn(dir.xz * (-8.0 / dyc) * 0.05 + float2(9.0, uTime * 0.003));
                float3 haze = lerp(hazeB, hazeW, smoothstep(0.35, 0.75, band));

                // 空（水平線より上）
                float3 skyC = lerp(haze, float3(0.55, 0.72, 0.93), clamp(dir.y * 6.0 + 0.2, 0.0, 1.0));

                // 雲海（以下は微分を使うので分岐の外で常に計算し、最後に選ぶ）
                float t = (CLOUD_Y - camY) / dyc;
                float2 wp = dir.xz * t + camXZ;
                float2 q = wp * 0.028 + float2(uTime * 0.010, uTime * 0.004);
                float n = fbm(q + fbm(q * 0.6 + 3.1) * 0.9);
                float dens = smoothstep(0.38, 0.68, n);
                float n2 = fbm(q + float2(0.0, -0.05) + fbm(q * 0.6 + 3.1) * 0.9);
                float litF = clamp(0.5 + (n - n2) * 14.0, 0.0, 1.0);
                // 淡い色の層: 遠いほど暖色（桃）、近いほど薄紫。琥珀は明るい縁だけ
                float farK = smoothstep(60.0, 300.0, t);
                float3 peach = float3(1.0, 0.72, 0.66), lav = float3(0.74, 0.68, 0.96), amber = float3(1.0, 0.82, 0.52);
                float3 tint = lerp(lav, peach, clamp(band * 1.5 + farK * 0.6 - 0.45, 0.0, 1.0));
                tint = lerp(tint, amber, smoothstep(0.55, 0.9, band2) * 0.7);
                float3 cWhite = lerp(float3(0.98, 0.98, 1.0), tint, 0.9);
                float3 cShade = lerp(float3(0.50, 0.60, 0.84), float3(0.47, 0.44, 0.76), band);      // 影は青紫寄り
                float3 cGap = lerp(float3(0.36, 0.50, 0.82), float3(0.44, 0.38, 0.72), smoothstep(0.3, 0.8, band2));
                float3 c = lerp(cGap, lerp(cShade, cWhite, litF * 0.75 + 0.25), dens);
                // 層の間の影 = 雲の縁のすぐ外側（隙間側）を青紫に沈める
                float ao = smoothstep(0.18, 0.40, n) * (1.0 - dens);
                c = lerp(c, c * float3(0.62, 0.62, 0.84), ao * 0.75);
                // 雲の縁のリムライト（琥珀白）。光が当たる側（litF）で強く
                float rim = smoothstep(0.44, 0.52, n) * (1.0 - smoothstep(0.52, 0.64, n));
                c += lerp(float3(1.0, 0.90, 0.80), tint, 0.25) * rim * (0.22 + 0.55 * litF) * 0.46;
                // 雲の層の筋（n の等高線を細く。近すぎて筋が太る所は fade で消す）
                float nl = n * 8.0, isoW = fwidth(nl);
                float isoL = (1.0 - smoothstep(0.0, isoW * 1.3 + 0.015, abs(frac(nl + 0.5) - 0.5))) * (1.0 - smoothstep(0.35, 0.9, isoW));
                c = lerp(c, lerp(float3(1, 1, 1), tint, 0.4) * (0.9 + 0.2 * litF), isoL * 0.30 * smoothstep(0.30, 0.6, dens + 0.15) * (0.4 + 0.6 * litF));
                // 浮島の影。島から光の向きへずらした位置に、高さに応じてぼける
                float shd = 0.0;
                [unroll] for (int i = 0; i < 6; i++) { float2 dd = wp - _Isl[i].xy; shd += exp(-dot(dd, dd) / _Isl[i].z); }
                c = lerp(c, float3(0.34, 0.34, 0.62), min(shd, 1.0) * 0.30);
                float fade = 1.0 - exp(-t * 0.0045);
                c = lerp(c, haze, clamp(fade * 1.15, 0.0, 1.0));
                // ステージの左右すぐ脇を青紫に沈める
                float t0 = -camY / dyc;
                float2 p0 = dir.xz * t0;
                float hw0 = _StageHalf.x + _StageHalf.y * (-p0.y);
                float dx = max(abs(p0.x) - hw0, 0.0);
                float sink = exp(-dx / (3.5 + 0.07 * t0));
                c = lerp(c, float3(0.26, 0.30, 0.52), 0.62 * sink);
                // 雲海の波紋（世界座標 z 方向に等間隔で、雲の密度でゆらぐ細い筋）。明るい筋のすぐ下に暗い筋を添える
                float wl = wp.y * 0.30 + n * 3.5 + sin(wp.x * 0.11 + n * 5.0) * 0.35 - uTime * 0.012;
                float wW = fwidth(wl), wFade = 1.0 - smoothstep(0.25, 0.7, wW);
                float wHi = (1.0 - smoothstep(0.0, wW * 1.3 + 0.014, abs(frac(wl + 0.5) - 0.5))) * wFade;
                float wLo = (1.0 - smoothstep(0.0, wW * 1.3 + 0.014, abs(frac(wl + 0.5 - 0.07) - 0.5))) * wFade;
                c = lerp(c, float3(0.94, 0.92, 1.0), wHi * 0.20 * (0.5 + 0.5 * litF));
                c = lerp(c, float3(0.30, 0.32, 0.60), wLo * 0.16);
                float wl2 = wp.y * 0.93 + n * 8.0 + sin(wp.x * 0.23 + n * 9.0) * 0.4 - uTime * 0.02;
                float wW2 = fwidth(wl2);
                float wHi2 = (1.0 - smoothstep(0.0, wW2 * 1.3 + 0.02, abs(frac(wl2 + 0.5) - 0.5))) * (1.0 - smoothstep(0.25, 0.6, wW2));
                c = lerp(c, float3(0.94, 0.92, 1.0), wHi2 * 0.11 * (0.4 + 0.6 * litF));

                c = dir.y > -0.02 ? skyC : c;

                // 消失点まわりを暗い霞に（モノリスが立つ奥）
                float2 e = (ndc - float2(0.0, 0.93)) / float2(0.42, 0.34);
                float hole = 1.0 - smoothstep(0.0, 1.0, length(e));
                c = lerp(c, float3(0.045, 0.06, 0.12), 0.7 * hole);
                c *= 1.0 - 0.6 * museSpawnMask(ndc);
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
