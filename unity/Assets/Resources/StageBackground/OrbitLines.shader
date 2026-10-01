// 神話の軌道都市: 線（神殿・浮遊足場）。移植元: shared/lines.js の buildLines ＋ orbit-r3.js の神殿 frag フック。
// 加算合成（lines.js の AdditiveBlending を、表示値→linear に直して Blend One One で再現）。_Hook=1 で神殿の光（列柱 vP≈1 / 梁・棟 vP≈2）。
Shader "Muses/Background/OrbitLines"
{
    Properties
    {
        _WidthPx ("Width (ref px)", Float) = 1.6
        _Opacity ("Opacity", Float) = 0.55
        _Hook ("Temple light hooks (0/1)", Float) = 0
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
                float _WidthPx;
                float _Opacity;
                float _Hook;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 bOS : TEXCOORD0;
                float2 s : TEXCOORD1;
                float2 p : TEXCOORD2;
                float4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float3 col : TEXCOORD1;
                float4 info : TEXCOORD2;   // x=side, y=halfW, z=margin, w=vP
                float3 wLab : TEXCOORD3;   // ラボ座標のワールド位置
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 aWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 bWS = TransformObjectToWorld(IN.bOS);
                BgLineVOut o = BgLineExpand(aWS, bWS, IN.s, BgRefPx(_WidthPx));
                OUT.positionCS = o.clip;
                OUT.clipXYW = float3(o.clip.xy, o.clip.w);
                OUT.col = IN.color.rgb;
                OUT.info = float4(o.side, o.halfW, o.margin, IN.p.x);
                OUT.wLab = BgToLab(IN.s.x < 0.5 ? aWS : bWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float t = _MusesBgTime;
                float vP = IN.info.w;
                float3 vW = IN.wLab;
                float3 col = IN.col;
                float a = _Opacity;
                a *= BgLineEdge(IN.info.x, IN.info.y, IN.info.z);
                if (_Hook > 0.5)
                {
                    // 列柱: 光の帯が下から上へなぞる
                    if (vP > 0.5 && vP < 1.5)
                    {
                        float ph = frac((vW.y + 12.0) * 0.045 - t * 0.06 + vW.z * 0.037 + vW.x * 0.021);
                        float e = (ph - 0.86) / 0.06;
                        float pu = exp(-e * e);
                        col = lerp(col * (1.0 + 0.4 * pu), float3(0.62, 0.82, 1.0) * 0.9, pu * 0.55);
                    }
                    // 梁・棟: 小さな光が奥へ走る
                    if (vP > 1.5)
                    {
                        float ph = frac(vW.z * 0.012 + t * 0.03 + vW.x * 0.02);
                        float e = (ph - 0.5) / 0.035;
                        float pu = exp(-e * e);
                        col = lerp(col, float3(0.95, 0.85, 0.6), pu * 0.55);
                    }
                }
                float2 ndc = BgNdc(IN.clipXYW);
                a *= 1.0 - museSpawnMask(ndc);
                // ラボはこの値を色空間変換なしの表示値（sRGB）として SrcAlpha/One で加算していた（暗い背景の上で表示値 col·a²）。
                // Unity はリニア出力なので、暗い背景の上で同じ見え方になるよう表示値 col·a² を linear へ変換して加算する。
                float3 disp = saturate(col * a * a);
                return half4(pow(disp, 2.2), 0.0);
            }
            ENDHLSL
        }
    }
}
