// 天上の聖域: 浮島（不透明。全インスタンスを1メッシュに焼いたもの）。移植元: sanctuary-r3.js の islandMat / mkVs。
// 頂点: uv0 = (aKind, インスタンス番号)。aKind: 0=岩 1=上面・縁の土 2=大理石 3=基壇の石。
// 自分自身の前後関係が要るので ZWrite On / ZTest LEqual（設計 §2）。Cull は Off（巻き順の問題を避ける。閉じたメッシュなので深度で隠れる）。
Shader "Muses/Background/SanctuaryIsland"
{
    Properties
    {
        _Spin ("Spin", Float) = 0.03
        _Bob ("Bob", Float) = 0.55
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend Off
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/SanctuaryCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Spin;
                float _Bob;
            CBUFFER_END

            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float3 vN : TEXCOORD1;
                float3 vView : TEXCOORD2;
                float3 vLocal : TEXCOORD3;
                float3 misc : TEXCOORD4;   // x=vDist y=vB z=vK
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                int id = (int)(IN.uv.y + 0.5);
                float3 nW;
                float3 wpL = SancInstance(id, IN.positionOS, IN.normalOS, _Spin, _Bob, nW);
                float3 camL = SancCamLab();
                OUT.vLocal = IN.positionOS;
                OUT.vN = normalize(nW);
                OUT.vView = normalize(camL - wpL);
                OUT.misc = float3(length(wpL - camL), _InstSeed[id].w, IN.uv.x);
                float4 clip = BgClampFar(TransformWorldToHClip(SancLabToWorld(wpL)));
                OUT.positionCS = clip;
                OUT.clipXYW = float3(clip.x, clip.y, clip.w);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 vLocal = IN.vLocal;
                float vDist = IN.misc.x, vB = IN.misc.y, vK = IN.misc.z;
                float3 n = normalize(IN.vN);
                float3 L = normalize(float3(-0.35, 0.8, -0.45));
                float l = clamp(dot(n, L) * 0.5 + 0.5, 0.0, 1.0);
                float r = length(vLocal.xz);
                float ang = atan2(vLocal.z, vLocal.x);
                float3 c;
                if (vK < 0.5)
                {
                    // 岩: 上面との境に暗い帯、下へ向かって暗い青紫、地層の筋、先端に雲の照り返し
                    float ry = clamp((-vLocal.y - 0.08) / 1.5, 0.0, 1.0);
                    float lr = clamp(dot(n, L) + 0.30, 0.0, 1.0);   // 下向きの面は光が当たらず暗い
                    float3 rockLit = lerp(float3(0.16, 0.14, 0.34), float3(0.56, 0.42, 0.54), lr * lr);
                    float3 deep = float3(0.025, 0.028, 0.12) * (0.7 + 0.7 * lr);
                    c = lerp(rockLit, deep, smoothstep(0.10, 0.80, ry) * 0.92);
                    float lip = 1.0 - smoothstep(0.0, 0.2, ry);
                    c = lerp(c, float3(0.035, 0.035, 0.13), lip * 0.7);
                    float st = sin(ry * 64.0 + sin(ang * 3.0 + 1.0) * 2.4 + sin(ang * 7.0) * 0.8);
                    c *= 1.0 - 0.16 * smoothstep(0.5, 1.0, st) + 0.10 * smoothstep(0.85, 1.0, -st);
                    c += float3(0.08, 0.045, 0.085) * smoothstep(0.75, 1.0, ry) * (0.3 + l);
                }
                else if (vK < 1.5)
                {
                    if (n.y > 0.6)
                    {
                        // 上面: 草・苔・乾いた土の斑、縁は濃い草＋明るい縁、遺構の足元は影
                        float m = sin(vLocal.x * 6.5 + 2.1 * sin(vLocal.z * 4.3)) * sin(vLocal.z * 7.3 - 1.7 * sin(vLocal.x * 3.1));
                        float3 grass = lerp(float3(0.52, 0.66, 0.62), float3(0.86, 0.90, 0.72), l);
                        float3 moss = lerp(float3(0.34, 0.52, 0.50), float3(0.62, 0.78, 0.60), l);
                        float3 dry = lerp(float3(0.66, 0.68, 0.62), float3(0.95, 0.90, 0.72), l);
                        c = lerp(grass, moss, smoothstep(0.0, 0.6, m));
                        c = lerp(c, dry, smoothstep(0.3, 0.8, -m) * 0.6);
                        c = lerp(c, float3(0.30, 0.46, 0.46), smoothstep(0.80, 0.96, r) * 0.55);
                        c = lerp(c, float3(1.0, 0.98, 0.85), smoothstep(0.955, 0.995, r) * 0.7);
                        float rd = max(abs(vLocal.x + 0.02) - 0.58, abs(vLocal.z - 0.02) * 1.9 - 0.26);
                        c *= 1.0 - 0.30 * exp(-max(rd, 0.0) * 8.0) * step(0.0, rd);
                    }
                    else
                    {
                        // 縁の側面: 上端は草の垂れ、下は土
                        float t = (vLocal.y + 0.08) / 0.16;
                        float3 soil = lerp(float3(0.24, 0.22, 0.42), float3(0.62, 0.52, 0.58), l * l);
                        float3 turf = lerp(float3(0.30, 0.46, 0.46), float3(0.66, 0.80, 0.62), l);
                        c = lerp(soil, turf, smoothstep(0.55, 0.78, t + 0.06 * sin(ang * 23.0)));
                    }
                }
                else if (vK < 2.5)
                {
                    // 大理石: 淡い脈、足元は少し暗い
                    float3 marble = lerp(float3(0.46, 0.44, 0.72), float3(1.0, 0.97, 0.93), l);
                    float vein = smoothstep(0.92, 1.0, sin(vLocal.x * 23.0 + vLocal.y * 11.0 + sin(vLocal.z * 9.0) * 2.0));
                    c = lerp(marble, marble * float3(0.80, 0.80, 0.94), vein * 0.6);
                    c *= 0.84 + 0.16 * smoothstep(0.10, 0.45, vLocal.y);
                }
                else
                {
                    // 基壇の石: 大理石よりやや暗く暖かい
                    c = lerp(float3(0.38, 0.36, 0.60), float3(0.90, 0.85, 0.86), l);
                    c *= 0.95 + 0.05 * sin(vLocal.x * 37.0 + vLocal.z * 29.0);
                }
                c *= 0.9 + 0.1 * vB;
                // 遠方は霞へ。岩の下面は霞を弱める（深い青紫を保つ）
                c = lerp(c, float3(0.80, 0.82, 0.96), clamp(1.0 - exp(-vDist * 0.005), 0.0, 0.6) * (vK < 0.5 ? 0.35 : 1.0));
                c *= 1.0 - 0.9 * museSpawnMask(BgNdc(IN.clipXYW));
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
