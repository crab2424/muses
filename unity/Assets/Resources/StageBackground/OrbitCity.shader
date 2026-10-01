// 神話の軌道都市: 都市（全画面のスクリーン空間の板）。移植元: design-lab/stage-bg/themes/orbit-r3.js の cityMat。
// 視線を BgRayDir で飛ばして都市面 y=_CityY に当て、当たり点をラボ座標（z 反転）にして、模様はラボと同じ式。
Shader "Muses/Background/OrbitCity"
{
    Properties
    {
        _Gate ("Gate (cx, cy, r, aspect)", Vector) = (0, 0.9, 0.33, 1.43)
        _CityY ("City Y", Float) = -34
        _CityC ("City Center (lab x, lab z)", Vector) = (0, 0, 0, 0)
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
                float4 _Gate;
                float _CityY;
                float4 _CityC;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 ndc : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.ndc = IN.positionOS.xy;
                OUT.positionCS = BgScreenClip(IN.positionOS.xy);
                return OUT;
            }

            float h11(float n) { return frac(sin(n * 127.1) * 43758.5453); }
            float h21(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            // 1スケール分の灯り。cs=セル寸法。aa=1ピクセルがセル何個分か
            float3 lights(float2 p, float cs, float aa, float dens, float t)
            {
                float2 q = p / cs, id = floor(q), f = frac(q) - 0.5;
                float h = h21(id), h2 = h21(id + 7.3), h3 = h21(id + 19.1);
                float lit = step(1.0 - dens, h);
                float2 off = (float2(h2, h3) - 0.5) * 0.4;
                float r = 0.05 + 0.09 * h3;
                float soft = max(aa, 0.04);
                float dot_ = (1.0 - smoothstep(r - soft, r + soft, length(f - off))) * lit;
                float sway = 0.72 + 0.28 * sin(t * (0.25 + 0.2 * h2) + h * 40.0);
                float avg = lit * 3.14159 * r * r;
                float fade = smoothstep(0.25, 0.7, aa);
                float a = lerp(dot_, avg, fade) * sway;
                float3 cool = float3(0.30, 0.55, 0.70), gold = float3(0.72, 0.60, 0.36);
                return a * lerp(cool, gold, step(0.78, h3 * 0.6 + h2 * 0.5));
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 ndc = IN.ndc;
                float uTime = _MusesBgTime;
                float3 dir = BgRayDir(ndc);
                float3 col = float3(0.012, 0.022, 0.060);
                if (dir.y < -0.004)
                {
                    float tt = (_CityY - _MusesBgCamPos.y) / dir.y;
                    float3 hit = _MusesBgCamPos + dir * tt;
                    float2 p = BgToLab(hit).xz;                 // ラボの hit.xz
                    float2 dp = fwidth(p);
                    float px = max(dp.x, dp.y);
                    float3 L = lights(p, 4.0, px / 4.0, 0.35, uTime) * 0.6;
                    L += lights(p + 31.7, 1.5, px / 1.5, 0.22, uTime + 5.0) * 0.3;
                    float2 gq = abs(frac(p / 12.0 + 0.5) - 0.5) * 12.0;
                    float gl = 1.0 - smoothstep(0.0, max(px, 0.12) * 1.5, min(gq.x, gq.y));
                    L += float3(0.03, 0.06, 0.10) * gl * (1.0 - smoothstep(0.15, 0.6, px));
                    float aaR = max(px, 0.2);
                    float ax = p.x / 26.0;
                    float ida = floor(ax + 0.5);
                    float da = abs(frac(ax + 0.5) - 0.5) * 26.0;
                    float road = 1.0 - smoothstep(0.0, aaR * 1.6 + 0.10, da);
                    float flow = frac(p.y / 70.0 + uTime * 0.10 + h11(ida) * 3.0);
                    float fq = (flow - 0.5) / 0.06;
                    float packet = exp(-fq * fq);
                    float dash = 0.35 + 2.4 * packet * step(0.30, h11(ida + 3.0));
                    float roadFade = smoothstep(1.0, 4.0, 26.0 / max(px, 0.01)) * step(0.5, abs(ida));
                    float rr = length(p - _CityC.xy);
                    float ringL = abs(frac(rr / 60.0 + 0.5) - 0.5) * 60.0;
                    float ringLine = 1.0 - smoothstep(0.0, aaR * 1.6 + 0.08, ringL);
                    float ringFade = smoothstep(1.0, 4.0, 60.0 / max(px, 0.01));
                    float haze = smoothstep(120.0, 520.0, tt);
                    col += L * (1.0 - 0.8 * haze);
                    col = lerp(col, float3(0.020, 0.040, 0.095), haze * 0.85);
                    col += float3(0.16, 0.30, 0.42) * (road * roadFade * 0.32 * dash + ringLine * ringFade * 0.10) * (1.0 - 0.35 * haze);
                    col += float3(0.004, 0.010, 0.026) * smoothstep(0.2, -1.0, ndc.y);
                }
                float gd = museGateDist(ndc, _Gate);
                col *= lerp(0.35, 1.0, smoothstep(0.0, 0.10, gd));
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
