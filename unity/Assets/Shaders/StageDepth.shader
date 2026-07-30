// 奥行きでカット・フェードするアンリットの半透明シェーダ。
// 移植元: web-prototype/src/stage.ts の stageMaterial()（ShaderMaterial）。
// notes.ts の移植でも同じシェーダを使う想定（ノーツは毎フレーム動くので GPU 側のカットが必須）。
Shader "Muses/StageDepth"
{
    Properties
    {
        _Color   ("Color", Color) = (1,1,1,1)
        _Alpha   ("Alpha", Float) = 1
        _Near    ("Near Depth", Float) = 0
        _Far     ("Far Depth", Float) = 100
        _HardFar ("Hard Far Edge", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float depth : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Alpha;
                float _Near;
                float _Far;
                float _HardFar;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 ws = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.depth = ws.z; // Unity は +z が奥（StageGeometry 参照）
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (IN.depth > _Far || IN.depth < _Near) discard;
                float a = _Alpha * (_HardFar > 0.5 ? 1.0
                        : 1.0 - smoothstep(_Far * 0.55, _Far, IN.depth));
                if (a <= 0.001) discard;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
}
