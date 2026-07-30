// タップ/ホールド/アークの本体。移植元: web-prototype/src/notes.ts のノーツ用 ShaderMaterial。
// 加算合成（AdditiveBlending）。頂点色×state、奥行きの遠端/近端フェード。
Shader "Muses/Note"
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
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Include/NotePlacement.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv0 : TEXCOORD0; // x = aState, y = aNear
                float2 uv1 : TEXCOORD1; // x = aLayerF
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 color : TEXCOORD0;
                float state : TEXCOORD1;
                float depth : TEXCOORD2;
                float near : TEXCOORD3;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float depth;
                float3 os = PlaceNote(IN.positionOS.xyz, IN.uv0, IN.uv1, depth);
                float3 ws = TransformObjectToWorld(os);
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.color = IN.color.rgb;
                OUT.state = IN.uv0.x;
                OUT.depth = depth;
                OUT.near = IN.uv0.y;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (IN.state <= 0.001) discard;
                if (IN.depth > _Far) discard; // 最遠端で切る（両層共通）
                float aFar = _HardFar > 0.5 ? 1.0 : 1.0 - smoothstep(_Far * 0.7, _Far, IN.depth);
                // 手前端でフェードアウト。範囲を狭くして面の終端の先へノーツがはみ出さないようにする
                float aNear = smoothstep(IN.near * 0.90, IN.near, IN.depth);
                float a = IN.state * aFar * aNear;
                if (a <= 0.003) discard;
                return half4(IN.color * (0.7 + 0.6 * IN.state), a);
            }
            ENDHLSL
        }
    }
}
