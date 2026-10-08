// ノーツ本体。スキン（r3、design-lab/note-skin）をキーワードで切り替える:
//   既定            … スキン「ネオン」（Include/NoteSkinNeon.hlsl）
//   _SKIN_KEYCAP    … スキン「キーキャップ」（Include/NoteSkinKeycap.hlsl）
// NoteView が NoteSkin に応じてマテリアルのキーワードを切り替え、メッシュも作り直す（Tap のメッシュがスキンで違うため）。
// 方針・経緯は memory/game/note-design-lab-r3.md。共通部品（頂点入出力・∧・白線・Tap の中心）は Include/NoteSkinCommon.hlsl。
//
// ∧ の位相は **実時間（_SongTime − ノーツの実時刻）** 基準。スクロールグループの停止・逆走（note-spec.md §5.5）に
// 関係なく常に一定の速さで流れ、判定時刻に先端が到達点へ届く。
// 出力はすべて premultiplied（Blend One OneMinusSrcAlpha、ラボの museOut と同じ「通常合成 α ＋ 加算ぶん」）。
// ラボ(three.js)は sRGB 空間でブレンドしていたが、Unity は Linear 空間でブレンドする。
//
// ZTest Always: ノーツは地面からごくわずか(zJudge*0.002)しか浮かせておらず、遠距離では
// デプスバッファの精度不足で地面とのZファイティングが起きる。描画順は renderQueue
// （NoteView.cs）とメッシュ内の頂点順（NoteDrawOrder）で保証しているのでデプステスト自体が不要。
// キーキャップの Tap の立体は凸形なので、背面を捨てるだけで前後関係が崩れない（Cull は帯などのため Off のまま、
// フラグメントで背面を discard する）。
Shader "Muses/Note"
{
    Properties
    {
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
        _ThicknessExp ("Thickness Exp", Float) = 0.75
        _TanHalfPhi ("Tan Half Phi", Float) = 1
        // note-visual-r1.md §3.2: 空中ノーツの画面上の厚みを地上と揃えるための層依存係数。
        _SkyThicknessMul ("Sky Thickness Mul", Float) = 1.96

        // r3 §9/§10: 白線の下限(px)と全体倍率。NoteView の edgeMinPx / edgeScale から設定する。
        _EdgeMinPx ("Edge Min Px", Float) = 0.5
        _EdgeScale ("Edge Scale", Float) = 1

        // gameplay-feel-r1.md §5.3。NoteView がノーツメッシュを3つのサブメッシュ（地上帯/空中帯/その他）に分け、
        // それぞれ別マテリアルで設定する（地上帯だけステンシルで重なりを4枚までに制限）。
        [HideInInspector] _StencilRef ("Stencil Ref", Float) = 0
        [HideInInspector] _StencilComp ("Stencil Comp", Float) = 8 // Always
        [HideInInspector] _StencilPass ("Stencil Pass", Float) = 0 // Keep
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
            }
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 両スキンのバリアントをビルドに含める（実行中に設定で切り替えるため multi_compile）
            #pragma multi_compile_local _ _SKIN_KEYCAP

            #if defined(_SKIN_KEYCAP)
            #include "Include/NoteSkinKeycap.hlsl"
            #else
            #include "Include/NoteSkinNeon.hlsl"
            #endif

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                int group = clamp((int)(IN.uv2.x + 0.5), 0, MUSES_MAX_SCROLL_GROUPS - 1);
                float groupX = _GroupX[group];
                float tag = IN.uv3.y;
                float layerF = IN.uv1.x;
                float nearD = IN.uv0.y;
                float depth, scale;
                float3 os;
                OUT.tapB = float3(1, 1, 1);

                if (tag > 3.5)
                {
                    float layer, m;
                    os = ChevronPlace(IN.positionOS.xyz, layerF, IN.uv4, groupX, SKIN_CHEV_TH, SKIN_CHEV_SL, layer, m, depth, scale);
                    nearD = lerp(_GroundNear, _SkyNear, saturate(layer));
                    OUT.extra = float4(m, IN.uv4.y, 0, 0);
                }
                else if (tag > 0.5 && tag < 2.5)
                {
                    os = SkinTapPlace(IN, groupX, OUT, depth, scale);
                }
                else
                {
                    float d0 = _ZJudge + (IN.positionOS.z - groupX) * _Speed;
                    os = PlaceNoteCore(IN.positionOS.x, IN.positionOS.y, IN.positionOS.z, layerF,
                        IN.uv1.y * MusesHalfThickness(d0, layerF), groupX, depth, scale);
                    OUT.extra = float4(IN.uv4.xy, layerF, 0);
                }

                OUT.positionCS = TransformWorldToHClip(TransformObjectToWorld(os));
                OUT.color = IN.color;
                OUT.sdns = float4(IN.uv0.x, depth, nearD, IN.uv1.y);
                OUT.misc = float4(IN.uv3.x, tag, IN.uv2.y, scale);
                return OUT;
            }

            half4 frag(Varyings IN, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float state = IN.sdns.x, depth = IN.sdns.y, nearD = IN.sdns.z;
                float tag = IN.misc.y;
                bool isTap = tag > 0.5 && tag < 2.5;

                if (state <= 0.001) discard;
                // Tap 系は立体・板がノーツ中心から前後に広がるので、最遠端はノーツ中心の奥行きで判定する（ノーツ単位で消える）
                float farDepth = isTap ? IN.tapC.w : depth;
                if (farDepth > _Far) discard;
                float aFar = _HardFar > 0.5 ? 1.0 : 1.0 - smoothstep(_Far * 0.7, _Far, farDepth);
                // 手前端でフェードアウト。範囲を狭くして面の終端の先へノーツがはみ出さないようにする
                float aNear = smoothstep(nearD * 0.90, nearD, depth);

                // ipad-test-findings-r1.md §④ / gameplay-feel-r1.md §5.4: 判定線を通過した Slide 区間（とその中継点マーカー）を
                // 「食べる」。fwidth は分岐の外で必ず計算する。
                float eat = saturate((depth - _ZJudge) / max(fwidth(depth), 1e-5));
                float fade = state * aFar * aNear * lerp(1.0, eat, IN.misc.z);
                if (fade <= 0.003) discard;

                float3 rgb;
                float alpha, add;
                SkinFrag(IN, IS_FRONT_VFACE(face, true, false), rgb, alpha, add);

                alpha *= fade;
                add *= fade;
                if (alpha + add <= 0.003) discard;
                return half4(rgb * (alpha + add), alpha);
            }
            ENDHLSL
        }
    }
}
