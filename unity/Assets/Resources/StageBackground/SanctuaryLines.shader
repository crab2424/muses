// 天上の聖域: 島・輪に追従する線（画面上で一定幅の帯メッシュ）。移植元: sanctuary-r3.js の instLines（shared/lines.js の頂点シェーダに
// 「自転→配置行列→上下」を差し込んだもの）。Unity では最初からその変換込みで書く。
// 頂点: POSITION=始点A / TEXCOORD0=終点B（ともに島・輪の局所座標、ラボ） / TEXCOORD1=(始点0終点1, 帯の左右) / TEXCOORD2.x=p / TEXCOORD3.x=インスタンス番号 / COLOR=色×強さ(linear)
//   _Kind 0 = 島の線: p は不透明度、島のスケール倍だけカメラ側へずらす（_Bias）、遠方は霞色へ。
//   _Kind 1 = 輪の線: p 1/2 の線分は輪の面内で回す（_RotOn, _RotSpd。1=順方向 2=逆方向×0.6）、遠方は薄く。
// premultiplied（One OneMinusSrcAlpha）で「塗る」。深度テストあり（島の陰・重なりで隠れる）。
Shader "Muses/Background/SanctuaryLines"
{
    Properties
    {
        _WidthPx ("Width px (iPad 11in ref)", Float) = 1.25
        _Opacity ("Opacity", Float) = 1
        _Kind ("Kind (0 island, 1 ring)", Float) = 0
        _Spin ("Spin", Float) = 0.03
        _Bob ("Bob", Float) = 0.55
        _Bias ("Bias toward camera (x island scale)", Float) = 0.03
        _RotOn ("Ring rotate on", Float) = 0
        _RotSpd ("Ring rotate speed", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Shaders/Include/SanctuaryCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _WidthPx;
                float _Opacity;
                float _Kind;
                float _Spin;
                float _Bob;
                float _Bias;
                float _RotOn;
                float _RotSpd;
            CBUFFER_END

            struct Attributes
            {
                float3 a : POSITION;
                float3 b : TEXCOORD0;
                float2 s : TEXCOORD1;
                float2 p : TEXCOORD2;
                float4 ex : TEXCOORD3;
                float4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 clipXYW : TEXCOORD0;
                float4 col : TEXCOORD1;
                float3 wLab : TEXCOORD2;
                float3 misc : TEXCOORD3;   // x=side y=halfW z=margin
                float vP : TEXCOORD4;
            };

            // ラボの xf(): 局所座標 → （輪の目盛りの回転）→ 自転 → 配置行列 → 上下 → カメラ側へずらす。ラボのワールド座標を返す
            float3 LineXf(float3 p, int id, float pFlag)
            {
                if (_RotOn > 0.5 && pFlag > 0.5)
                {
                    float th = _MusesBgTime * _RotSpd * (pFlag < 1.5 ? 1.0 : -0.6);
                    float cz = cos(th), sz = sin(th);
                    p.xy = float2(cz * p.x - sz * p.y, sz * p.x + cz * p.y);
                }
                float3 nDummy;
                float3 wp = SancInstance(id, p, float3(0, 1, 0), _Spin, _Bob, nDummy);
                float3 scaleCol = float3(_InstM0[id].x, _InstM1[id].x, _InstM2[id].x);   // 配置行列の第1列 = x 方向のスケール
                wp += normalize(SancCamLab() - wp) * _Bias * length(scaleCol);
                return wp;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                int id = (int)(IN.ex.x + 0.5);
                float3 wa = LineXf(IN.a, id, IN.p.x);
                float3 wb = LineXf(IN.b, id, IN.p.x);
                BgLineVOut e = BgLineExpand(SancLabToWorld(wa), SancLabToWorld(wb), IN.s, BgRefPx(_WidthPx));
                OUT.positionCS = e.clip;
                OUT.clipXYW = float3(e.clip.x, e.clip.y, e.clip.w);
                OUT.col = IN.color;
                OUT.wLab = IN.s.x < 0.5 ? wa : wb;
                OUT.misc = float3(e.side, e.halfW, e.margin);
                OUT.vP = IN.p.x;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 col = IN.col.rgb;
                float a = _Opacity * BgLineEdge(IN.misc.x, IN.misc.y, IN.misc.z);
                float dist = length(IN.wLab - SancCamLab());
                if (_Kind < 0.5)
                {
                    // 島の線: 色は linear 値。ラボは表示側(sRGB)へ pow して混ぜていたが、Unity は linear 出力なのでそのまま混ぜる
                    float fog = clamp(1.0 - exp(-dist * 0.005), 0.0, 0.6);
                    col = lerp(col, float3(0.80, 0.82, 0.96), fog);
                    a *= IN.vP;
                }
                else
                {
                    // 輪の線: ラボの lines.js は色空間変換なしで出力していた（金の値は表示値そのもの）ので、Unity の linear 出力では 2.2 乗して合わせる
                    col = pow(max(col, 0.0), 2.2);
                    a *= 1.0 - 0.5 * clamp(1.0 - exp(-dist * 0.006), 0.0, 0.6);
                }
                a *= 1.0 - museSpawnMask(BgNdc(IN.clipXYW));
                return half4(col * a, a);
            }
            ENDHLSL
        }
    }
}
