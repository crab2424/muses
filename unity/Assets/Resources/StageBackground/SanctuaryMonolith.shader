// 天上の聖域: モノリス（消失点の黒い逆三角。距離150のワールドに置いたメッシュ）。移植元: sanctuary-r3.js の monolith。
// 頂点: POSITION=Unity ワールド座標 / uv0=色(rgba, linear) / uv1.x=aFx（0=黒 1=縁の光（波打つ） 2=刻線（暗部内では消える））。
// 遠いので頂点出力は BgClampFar（MusesBackground.hlsl の BgClampFar）を通す。
Shader "Muses/Background/SanctuaryMonolith"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/MusesBackground.hlsl"

            struct Attributes { float3 positionOS : POSITION; float4 col : TEXCOORD0; float2 fx : TEXCOORD1; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float4 col : TEXCOORD1;
                float fx : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float4 clip = BgClampFar(TransformWorldToHClip(IN.positionOS));
                OUT.positionCS = clip;
                OUT.clipXYW = float3(clip.x, clip.y, clip.w);
                OUT.col = IN.col;
                OUT.fx = IN.fx.x;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 ndc = BgNdc(IN.clipXYW);
                float3 c = IN.col.rgb; float a = IN.col.a;
                if (IN.fx > 0.5 && IN.fx < 1.5) c *= 1.0 + 0.16 * sin(ndc.y * 24.0 - _MusesBgTime * 0.5 + ndc.x * 3.0);   // 縁の光がゆっくり波打つ
                if (IN.fx > 1.5) a *= 1.0 - 0.92 * museSpawnMask(ndc);                                                    // 刻線は暗部内では消す
                return half4(c, a);
            }
            ENDHLSL
        }
    }
}
