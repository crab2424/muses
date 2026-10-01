// 天上の聖域: 光芒（加算の板をまとめた1メッシュ）。移植元: sanctuary-r3.js の光芒。
// 頂点: POSITION=Unity ワールド座標（C# 側で z 反転済み） / uv0=(横-1..1, 縦0..1) / uv1.x=ray ごとの乱数。ラボの AdditiveBlending = SrcAlpha, One。
Shader "Muses/Background/SanctuaryRays"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/MusesBackground.hlsl"

            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; float2 seed : TEXCOORD1; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float3 uvS : TEXCOORD1;   // xy=uv z=seed
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 p = IN.positionOS;
                p.x += sin(_MusesBgTime * 0.18 + IN.seed.x * 40.0) * 1.2 * IN.uv.y;
                float4 clip = BgClampFar(TransformWorldToHClip(p));
                OUT.positionCS = clip;
                OUT.clipXYW = float3(clip.x, clip.y, clip.w);
                OUT.uvS = float3(IN.uv, IN.seed.x);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 vUv = IN.uvS.xy; float vS = IN.uvS.z;
                float across = 1.0 - smoothstep(0.0, 1.0, abs(vUv.x));
                float along = smoothstep(0.0, 0.25, vUv.y) * (1.0 - smoothstep(0.55, 1.0, vUv.y));
                float breathe = 0.9 + 0.1 * sin(_MusesBgTime * 0.25 + vS * 30.0);
                float streak = 0.80 + 0.20 * sin(vUv.x * 38.0 + vS * 30.0 + vUv.y * 3.0);   // 細い筋
                float a = across * across * along * 0.16 * breathe * streak;
                a *= 1.0 - museSpawnMask(BgNdc(IN.clipXYW));
                float3 rc = lerp(float3(1.0, 0.86, 0.62), float3(1.0, 0.80, 0.86), step(0.5, frac(vS * 7.0)));
                return half4(rc * a, a);
            }
            ENDHLSL
        }
    }
}
