// 神話の軌道都市: 光の粒（400点）。移植元: orbit-r3.js の pm（THREE.Points）。1粒子 = 4頂点の板、頂点シェーダで上昇＋大きさ。
// メッシュ: POSITION は未使用 / TEXCOORD0 = 板の隅 (±1, ±1) / TEXCOORD1 = aSeed (x, 位相, z(ラボ), 速さ係数)
Shader "Muses/Background/OrbitParticles"
{
    Properties
    {
        _Gate ("Gate (cx, cy, r, aspect)", Vector) = (0, 0.9, 0.33, 1.43)
        _PxScale ("Point size scale (lab uPx)", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend One One // 出力は linear に直した加算量（下の return 参照）
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/MusesBackground.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Gate;
                float _PxScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 corner : TEXCOORD0;
                float4 seed : TEXCOORD1;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float2 corner : TEXCOORD1;
                float2 ag : TEXCOORD2;     // x=vA, y=vG
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float t = _MusesBgTime;
                float4 aSeed = IN.seed;
                float H = 44.0;
                float ph = frac(aSeed.y + t * 0.018 * aSeed.w);
                float3 pLab = float3(aSeed.x + 1.5 * sin(t * 0.2 + aSeed.y * 30.0), -34.0 + ph * H, aSeed.z);
                float vA = smoothstep(0.0, 0.15, ph) * (1.0 - smoothstep(0.65, 1.0, ph));
                float vG = frac(aSeed.y * 91.7);
                float4 clip = TransformWorldToHClip(float3(pLab.x, pLab.y, -pLab.z));
                float depth = max(clip.w, 1e-4);                           // ラボの -mv.z
                float sizePx = _PxScale * clamp(60.0 / depth, 1.5, 3.2);   // 直径（基準 px）
                float2 off = IN.corner * 0.5 * BgRefPx(sizePx);            // 半径（NDC 縦単位）
                clip.xy += off * float2(1.0 / _MusesBgAspect, 1.0) * clip.w;
                clip = BgClampFar(clip);
                OUT.positionCS = clip;
                OUT.clipXYW = float3(clip.xy, clip.w);
                OUT.corner = IN.corner;
                OUT.ag = float2(vA, vG);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 c = IN.corner * 0.5;
                float a = (1.0 - smoothstep(0.15, 0.5, length(c))) * IN.ag.x;
                float3 col = lerp(float3(0.30, 0.52, 0.66), float3(0.72, 0.62, 0.40), step(0.8, IN.ag.y));
                a *= 0.55;
                float2 ndc = BgNdc(IN.clipXYW);
                a *= 1.0 - museSpawnMask(ndc);
                a *= smoothstep(0.0, 0.08, museGateDist(ndc, _Gate));
                // ラボはこの値を色空間変換なしの表示値（sRGB）として SrcAlpha/One で加算していた（暗い背景の上で表示値 col·a²）。
                // Unity はリニア出力なので、暗い背景の上で同じ見え方になるよう表示値 col·a² を linear へ変換して加算する。
                float3 disp = saturate(col * a * a);
                return half4(pow(disp, 2.2), 0.0);
            }
            ENDHLSL
        }
    }
}
