// 天上の聖域: 金の光輪（トーラス＋周回する光点3つ。全インスタンスを1メッシュに焼いたもの）。移植元: sanctuary-r3.js の ringMat。
// 頂点: uv0 = (aKind, インスタンス番号)。aKind 1 = 光点（輪の面内でゆっくり周回、約2分で1周）。
// 半透明、ZWrite Off、島の深度には ZTest LEqual。Cull Front の理由は SanctuaryGlass と同じ。
Shader "Muses/Background/SanctuaryRing"
{
    Properties
    {
        _Spin ("Spin", Float) = 0.05
        _Bob ("Bob", Float) = 0.3
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

            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float3 vN : TEXCOORD1;
                float3 vView : TEXCOORD2;
                float3 misc : TEXCOORD3;   // x=vDist y=vB z=vK
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                int id = (int)(IN.uv.y + 0.5);
                float3 p = IN.positionOS, n = IN.normalOS;
                if (IN.uv.x > 0.5)
                {
                    float th = _MusesBgTime * 0.05; float cs = cos(th), sn = sin(th);
                    p.xy = float2(cs * p.x - sn * p.y, sn * p.x + cs * p.y);
                    n.xy = float2(cs * n.x - sn * n.y, sn * n.x + cs * n.y);
                }
                float3 nW;
                float3 wpL = SancInstance(id, p, n, _Spin, _Bob, nW);
                float3 camL = SancCamLab();
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
                float vDist = IN.misc.x, vB = IN.misc.y, vK = IN.misc.z;
                float f = 1.0 - abs(dot(normalize(IN.vN), normalize(IN.vView)));
                float3 c = lerp(float3(1.0, 0.80, 0.42), float3(1.0, 0.92, 0.66), f) * (0.85 + 0.15 * vB);
                c = vK > 0.5 ? lerp(float3(1.0, 0.95, 0.78), float3(1.0, 0.99, 0.92), f) : c;
                c = lerp(c, float3(0.98, 0.84, 0.66), clamp(1.0 - exp(-vDist * 0.006), 0.0, 0.5));
                float al = 0.95;
                c *= 1.0 - 0.9 * museSpawnMask(BgNdc(IN.clipXYW));
                return half4(c, al);
            }
            ENDHLSL
        }
    }
}
