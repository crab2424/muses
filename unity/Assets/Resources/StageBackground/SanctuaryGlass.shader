// 天上の聖域: ガラスの破片（6角の柱状の双錐。全インスタンスを1メッシュに焼いたもの）。移植元: sanctuary-r3.js の glassMat。
// 頂点: uv0 = (0, インスタンス番号) / uv1 = 重心座標（稜線のハイライト用）。半透明、ZWrite Off、島の深度には ZTest LEqual（ラボも depthTest あり）。
// Cull Front: ラボの FrontSide（反時計回り）を z 反転した Unity で再現すると画面上の巻きが「裏」扱いになるため。表裏が逆に見えたら Back にする。
Shader "Muses/Background/SanctuaryGlass"
{
    Properties
    {
        _Spin ("Spin", Float) = 0.12
        _Bob ("Bob", Float) = 0.7
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/SanctuaryCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Spin;
                float _Bob;
            CBUFFER_END

            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; float3 bary : TEXCOORD1; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float3 vN : TEXCOORD1;
                float3 vView : TEXCOORD2;
                float3 vLocal : TEXCOORD3;
                float3 vBary : TEXCOORD4;
                float2 misc : TEXCOORD5;   // x=vDist y=vB
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
                OUT.vBary = IN.bary;
                OUT.misc = float2(length(wpL - camL), _InstSeed[id].w);
                float4 clip = BgClampFar(TransformWorldToHClip(SancLabToWorld(wpL)));
                OUT.positionCS = clip;
                OUT.clipXYW = float3(clip.x, clip.y, clip.w);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float uTime = _MusesBgTime;
                float vDist = IN.misc.x, vB = IN.misc.y;
                float3 n = normalize(IN.vN);
                float f = 1.0 - abs(dot(n, normalize(IN.vView)));
                float ph = f * 1.6 + vB * 2.0 + uTime * 0.02;
                float3 iri = 0.5 + 0.5 * cos(6.2831 * (ph + float3(0.0, 0.33, 0.67)));
                float3 baseC = lerp(float3(0.82, 0.90, 1.0), float3(1, 1, 1), clamp(dot(n, normalize(float3(-0.3, 0.8, -0.5))), 0.0, 1.0));
                // 屈折の縞: 面ごとにずれた細い帯が虹色を強める
                float sp = sin(IN.vLocal.y * 9.0 + vB * 6.0 + f * 4.0 + dot(n, float3(1.0, 0.0, 0.6)) * 3.0);
                float band = smoothstep(0.55, 0.95, sp);
                float3 c = lerp(baseC, iri, 0.32 + 0.25 * f) * 0.95;
                c = lerp(c, iri * 1.05 + 0.10, band * 0.35);
                // 稜線のハイライト（画面上でほぼ一定の細さ）
                float e = min(min(IN.vBary.x, IN.vBary.y), IN.vBary.z);
                float ridge = 1.0 - smoothstep(0.0, max(fwidth(e) * 1.8, 1e-4), e);
                c = lerp(c, float3(1.0, 0.99, 0.96), ridge * 0.75);
                c = lerp(c, float3(0.80, 0.82, 0.96), clamp(1.0 - exp(-vDist * 0.008), 0.0, 0.7));
                float al = clamp(0.55 + 0.35 * f + band * 0.10 + ridge * 0.35, 0.0, 1.0);
                c *= 1.0 - 0.9 * museSpawnMask(BgNdc(IN.clipXYW));
                return half4(c, al);
            }
            ENDHLSL
        }
    }
}
