// 拍線（地上のみ）。移植元: web-prototype/src/notes.ts の beatLines 用 ShaderMaterial。
// ZTest Always: Note.shaderと同じ理由（地面からの浮きが極小でZファイティングするため、
// renderQueueで保証済みの描画順に任せてデプステストを無効化）。
Shader "Muses/NoteBeatLine"
{
    Properties
    {
        _SongTime ("Song Time", Float) = 0
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
    }
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
            #include "Include/NotePlacement.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0; // y = aNear（xは未使用）
                float2 uv1 : TEXCOORD1; // x = aLayerF
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float depth : TEXCOORD0;
                float near : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float depth;
                float3 os = PlaceNote(IN.positionOS.xyz, IN.uv0, IN.uv1, depth);
                float3 ws = TransformObjectToWorld(os);
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.depth = depth;
                OUT.near = IN.uv0.y;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (IN.depth > _Far || IN.depth < IN.near) discard;
                float a = 0.22 * (_HardFar > 0.5 ? 1.0 : 1.0 - smoothstep(_Far * 0.55, _Far, IN.depth));
                if (a <= 0.003) discard;
                return half4(0.55, 0.65, 0.95, a);
            }
            ENDHLSL
        }
    }
}
