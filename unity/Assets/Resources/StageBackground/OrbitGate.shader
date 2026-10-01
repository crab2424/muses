// 神話の軌道都市: 虚空のゲート（スクリーン空間の板1枚）。移植元: design-lab/stage-bg/themes/orbit-r3.js の gateMat。
// r1 のゲート（黒い円盤・縁・外側の2本目・動かない同心リング3本）＋外周の時計盤（主環・内環・金の破線環・36目盛り・光点3つ）。
// 出力は premultiplied（円盤の内側は不透明、外側の光は加算）。設計は memory/game/stage-bg-unity-port.md。
Shader "Muses/Background/OrbitGate"
{
    Properties
    {
        _Gate ("Gate (cx, cy, r, aspect)", Vector) = (0, 0.9, 0.33, 1.43)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/MusesBackground.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Gate;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 ndc : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.ndc = IN.positionOS.xy;          // 頂点 = NDC（y 上向き）
                OUT.positionCS = BgScreenClip(IN.positionOS.xy);
                return OUT;
            }

            // 半径 r の円の線（幅 w は縦単位）。fw でアンチエイリアス
            float ring(float rad, float r, float w, float fw) { return 1.0 - smoothstep(w * 0.5, w * 0.5 + fw, abs(rad - r)); }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 ndc = IN.ndc;
                float t = _MusesBgTime;
                float2 q = (ndc - _Gate.xy) * float2(_Gate.w, 1.0);
                float R = _Gate.z;
                float rad = length(q), ang = atan2(q.y, q.x);
                float fw = fwidth(rad);
                float w = BgRefPx(1.6);
                float disc = 1.0 - smoothstep(-fw, fw, rad - R);
                float quiet = 1.0 - 0.8 * museSpawnMask(ndc);
                float3 cyanC = float3(0.42, 0.62, 0.80), goldC = float3(0.70, 0.60, 0.38);
                float3 glow = 0;
                // r1 のゲート（動かない）
                glow += cyanC * 0.54 * ring(rad, R, w, fw);
                glow += cyanC * 0.21 * ring(rad, R * 1.06, w, fw);
                [unroll] for (int k = 1; k <= 3; k++)
                    glow += cyanC * (0.17 / k) * ring(rad, R * (1.0 - 0.22 * k), w, fw) * quiet;
                // 外周の時計盤
                float Rm = R * 1.16, s = Rm / 15.0;
                float spin = t * 0.035;
                float tk = frac((ang + spin) / 6.28318) * 36.0;
                float tIdx = floor(tk + 0.5);
                float tick = 1.0 - smoothstep(w * 0.5, w * 0.5 + fw, abs(tk - tIdx) / 36.0 * 6.28318 * rad);
                float longT = step(fmod(tIdx, 3.0), 0.5);
                float tickLen = lerp(1.0, 2.4, longT) * s;
                float inTick = step(Rm, rad) * (1.0 - step(Rm + tickLen, rad));
                glow += goldC * lerp(0.24, 0.42, longT) * tick * inTick;
                glow += cyanC * 0.45 * ring(rad, Rm, w, fw);
                glow += cyanC * 0.22 * ring(rad, 13.6 * s, w, fw);
                float dashA = frac((ang - t * 0.02) / 6.28318 * 12.0);
                glow += goldC * 0.26 * ring(rad, 19.0 * s, w, fw) * step(0.18, dashA);
                float bead = abs(frac((ang / 6.28318 - t * 0.016) * 3.0) - 0.5) / 3.0 * 6.28318 * Rm;
                glow += float3(0.75, 0.92, 1.0) * 0.7 * exp(-bead * bead / (0.006 * 0.006)) * ring(rad, Rm, w * 3.0, fw);
                float3 dark = float3(0.0003, 0.0006, 0.0024);
                return half4(dark * disc + glow, disc);
            }
            ENDHLSL
        }
    }
}
