// タップ/ホールド/アークの本体。移植元: web-prototype/src/notes.ts のノーツ用 ShaderMaterial。
// 通常のアルファブレンド。頂点色×state、奥行きの遠端/近端フェード。
// Web版は加算合成(AdditiveBlending)だが、Unity側の実機チューニング設定は明背景(#a0b298)+
// 不透明なステージ面が前提のため、加算だと白飛びしてコントラストが低く見えていた
// （NoteBeatLine.shaderと同じ通常アルファブレンドに揃えて解消）。
// ZTest Always: ノーツは地面からごくわずか(zJudge*0.002)しか浮かせておらず、遠距離では
// デプスバッファの精度不足で地面とのZファイティングが起きる。描画順は既にrenderQueue
// （NoteView.csのNotesRenderQueue=3010、StageViewの3000番台より後）で保証済みなので
// デプステスト自体が不要。ONにしたままだとタップノーツのように小さい面積のノーツは
// フレームごとに丸ごと表示/非表示が切り替わり「途切れ途切れ」に見える
// （ホールドは面積が大きく一部ピクセルが負けても目立たないため気づきにくかった）。
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
        _TanHalfPhi ("Tan Half Phi", Float) = 1
        // note-visual-r1.md §3.2: 空中ノーツの画面上の厚みを地上と揃えるための層依存係数。
        // 既定1.96 = 1/0.509（奥行き再マップ後の空中/地上の画面厚み比の逆数）。
        _SkyThicknessMul ("Sky Thickness Mul", Float) = 1.96

        // gameplay-feel-r1.md §5.3。NoteView がノーツメッシュを3つのサブメッシュ（地上帯/空中帯/その他）に分け、
        // それぞれ別マテリアルでこれらを設定する。既定値は従来どおりの通常アルファ合成・ステンシル無し。
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 5      // SrcAlpha
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 10     // OneMinusSrcAlpha
        [HideInInspector] _StencilRef ("Stencil Ref", Float) = 0
        [HideInInspector] _StencilComp ("Stencil Comp", Float) = 8 // Always
        [HideInInspector] _StencilPass ("Stencil Pass", Float) = 0 // Keep
        // 1 = 地上帯の「重なるほど明るく」モード（premultiplied 出力、_SrcBlend=One と組で使う）
        [HideInInspector] _BandPremul ("Band Premul", Float) = 0
        _BandAlpha ("Band Alpha (normal part)", Float) = 0.30
        _BandAdd ("Band Add (additive part)", Float) = 0.22
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            // gameplay-feel-r1.md §5.3: 地上帯のマテリアルだけ Ref 4 / Greater / IncrSat を設定し、
            // 1ピクセルに重なる帯を最大4枚に制限する（5枚目以降はそのピクセルに描かない）。
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
            #include "Include/NotePlacement.hlsl"

            float _BandPremul;
            float _BandAlpha;
            float _BandAdd;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv0 : TEXCOORD0; // x = aState, y = aNear
                float2 uv1 : TEXCOORD1; // x = aLayerF, y = aSide
                float2 uv2 : TEXCOORD2; // x = aScrollGroup（note-spec.md §5.5、_GroupX[] のインデックス）
                                        // y = aSlideEatable（ipad-test-findings-r1.md §④。Slide帯のみ使用。
                                        // 1=判定線で食べる(Hit) / 0=そのまま通り過ぎる(既定、Tap等は常に0のまま無視)）
                float2 uv3 : TEXCOORD3; // note-visual-r1.md §3-3/§8-3: SDF描画用ローカルUV（NoteGeometry.NoteMeshData.localUvのコメント参照）
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : TEXCOORD0; // riser-r2.md §3.1: alphaも運ぶ（従来はfloat3でrgbのみ）
                float state : TEXCOORD1;
                float depth : TEXCOORD2;
                float near : TEXCOORD3;
                float side : TEXCOORD4;
                float2 localUv : TEXCOORD5;
                float slideEatable : TEXCOORD6;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float depth;
                int group = clamp((int)(IN.uv2.x + 0.5), 0, MUSES_MAX_SCROLL_GROUPS - 1);
                float3 os = PlaceNote(IN.positionOS.xyz, IN.uv0, IN.uv1, _GroupX[group], depth);
                float3 ws = TransformObjectToWorld(os);
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.color = IN.color;
                OUT.state = IN.uv0.x;
                OUT.depth = depth;
                OUT.near = IN.uv0.y;
                OUT.side = IN.uv1.y;
                OUT.localUv = IN.uv3;
                OUT.slideEatable = IN.uv2.y;
                return OUT;
            }

            // note-visual-r1.md §2.2/§4/§9-6: 角丸矩形の符号付き距離関数（uvのローカル空間）。
            // p は中心を原点とした座標、b は半サイズ、r は角丸半径。標準的な定式化。
            float RoundedBoxSDF(float2 p, float2 b, float r)
            {
                float2 q = abs(p) - b + r;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            // gameplay-feel-r1.md §5.1: Flick の `< >`。長方形の左右端を長さ L で尖らせた横長の六角形。
            // 斜辺 |x| + k|y| = b.x（k = L/b.y）までの距離と上下辺までの距離の max（内側は厳密、外側は近似）。
            float PointedBoxSDF(float2 p, float2 b, float L)
            {
                float2 a = abs(p);
                float k = L / max(b.y, 1e-4);
                float slant = (a.x + k * a.y - b.x) * rsqrt(1.0 + k * k);
                return max(a.y - b.y, slant);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (IN.state <= 0.001) discard;
                if (IN.depth > _Far) discard; // 最遠端で切る（両層共通）
                float aFar = _HardFar > 0.5 ? 1.0 : 1.0 - smoothstep(_Far * 0.7, _Far, IN.depth);
                // 手前端でフェードアウト。範囲を狭くして面の終端の先へノーツがはみ出さないようにする
                float aNear = smoothstep(IN.near * 0.90, IN.near, IN.depth);

                // ipad-test-findings-r1.md §④: 判定線を通過した区間を「食べる/そのまま通り過ぎる」で分岐する。
                // 区間ごとの判定結果は NoteView.SetSlideSegmentEatable が uv2.y に書き込む（Judge.UpdateSlide:
                // 押さえていればtrue、それ以外はfalse=既定）。1プリミティブ内の全頂点が同じ値を持つ
                // （NoteGeometry.PushSlideBandが区間境界で頂点を分けて生成しているため）。
                // 食べる区間だけ depth < _ZJudge（判定線より手前=通過済み）で削る。fwidth(depth)で割ることで
                // 遠近に依らず画面上1px幅のAAになる。Tap等は uv2.y が常に0なので影響しない。
                // gameplay-feel-r1.md §5.4: Visible中継点マーカー(タグ3)も同じフラグで食べる（従来は浮いていた）。
                // fwidth は分岐の外で必ず計算する（分岐内の微分値を避ける）。
                float eat = saturate((IN.depth - _ZJudge) / max(fwidth(IN.depth), 1e-5));
                float eatMul = lerp(1.0, eat, IN.slideEatable);

                // 状態・遠近フェード・食べる、の合成（頂点色alphaを掛ける前）。地上帯の premultiplied 出力で使う。
                float fade = IN.state * aFar * aNear * eatMul;
                // riser-r2.md §3.1: 頂点色のalphaも乗せる。既存ノーツは全てalpha=1なので回帰なし。
                float a = fade * IN.color.a;
                if (a <= 0.003) discard;

                // note-visual-r1.md 実機フィードバック(2026-08-07): 従来は常時state=1で
                // rgb*1.3の固定オーバーブライトになっており、彩度の高い色ほどクリップして
                // エディタ側(NoteColors生値)より薄く見えていた。倍率をやめ生値をそのまま使う。
                float3 rgb = IN.color.rgb;
                const float3 kOutlineColor = float3(1, 1, 1); // note-visual-r1.md §2.2/§9-6: 白固定

                // 種別の判定は localUv.y（プリミティブ内で定数のタグ）で行う。
                // 2026-08-07: 以前は abs(IN.side)>0.5 で判定していたが、side は厚みを付けるための
                // 座標で面の内部では -1→+1 に補間されるため、タップの中央50%がSlide帯の分岐へ
                // 落ちていた（NoteGeometry.NoteMeshData.localUv のコメント参照）。
                if (IN.localUv.y > 0.5)
                {
                    // ---- タップ形状（Tap/ExTap/Flick/Slideマーカー）: スクリーン空間一定幅の
                    // 白い輪郭線つき SDF（note-visual-r1.md §2.2）。
                    // 座標を軸ごとにpx単位へ変換してから距離を測る（ノーツは横に広く縦に薄いので、
                    // スカラーの fwidth(dist) だと薄い方の軸がaaを支配して輪郭が塗りを飲み込む）。
                    // 変換後は「1=1px」なので aa・輪郭幅は定数でよい。
                    // 厚み方向の座標は side から導く（QuadThinの4頂点で -1/+1）。
                    //
                    // gameplay-feel-r1.md §5.1: 形はタグで分ける（localUv.y）。
                    //   1 = Tap/ExTap `( )` : 左右端を半円に（カプセル）
                    //   2 = Flick `< >`     : 左右端を尖らせる
                    //   3 = 中継点マーカー  : 従来の角丸矩形（Tapと同じ形だと「叩く」と誤読されるため）
                    float2 uv = float2(IN.localUv.x, IN.side * 0.5 + 0.5);
                    float2 p = uv - 0.5;
                    float2 duv = float2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
                    float2 pPx = p / duv;
                    float2 bPx = float2(0.5, 0.5) / duv;      // 画面上の半サイズ(px) x=幅方向 / y=厚み方向

                    float dist;
                    if (IN.localUv.y > 2.5)
                    {
                        float rPx = min(0.16 / max(duv.x, duv.y), min(bPx.x, bPx.y) * 0.9); // SDFの定義上 r<=min(b) が要る
                        dist = RoundedBoxSDF(pPx, bPx, rPx);
                    }
                    else if (IN.localUv.y > 1.5)
                    {
                        float tip = min(bPx.y * 1.3, bPx.x * 0.45); // 尖りの長さ（厚みの1.3倍、幅の45%まで）
                        dist = PointedBoxSDF(pPx, bPx, tip);
                    }
                    else
                    {
                        dist = RoundedBoxSDF(pPx, bPx, min(bPx.x, bPx.y)); // 半径=厚みの半分 → カプセル
                    }
                    float shapeAlpha = 1.0 - smoothstep(0.0, 1.0, dist);
                    if (shapeAlpha <= 0.003) discard;

                    // 輪郭線の太さ（2026-08-07: 両軸1.5px固定だと遠方で白一色になった）。
                    // 幅方向の端: 隣接ノーツの分離(§2.2)のため常に1.5pxを狙う。
                    // 厚み方向（上下の辺）: 画面上の厚みは奥行きで23倍変わる(§3.1)ので、半サイズに応じて
                    //   細らせ、1pxを切るあたりで0にする（遠方のノーツは輪郭なしの塗りだけになる）。
                    // 端形状(カプセル/尖り)に沿わせるため、輪郭は SDF の等値線で引き、太さだけを
                    // 「中央付近は厚み方向の値、左右端に近づくほど幅方向の値」へ補間する。
                    const float kOutlinePx = 1.5;
                    float wx = kOutlinePx;
                    float wy = min(kOutlinePx, max(0.0, bPx.y - 0.75) * 0.5);
                    float endZone = max(bPx.y * 1.5, 1.0);
                    float tEnd = saturate((abs(pPx.x) - (bPx.x - endZone)) / endZone);
                    float w = lerp(wy, wx, tEnd);
                    float outline = smoothstep(-w - 0.5, -w + 0.5, dist);

                    // gameplay-feel-r1.md §5.2: 薄い立体感（間近で見れば厚みが分かる程度）。
                    // 厚み方向に上（奥側, side=+1）を僅かに明るく・下を僅かに暗く、上端の内側に細いハイライト、
                    // 下端の内側に細い影。画面上の厚みが数px以下（遠方）では close→0 で自動的に消える。
                    float close = saturate((bPx.y - 3.0) / 4.0);
                    rgb *= 1.0 + 0.08 * IN.side * close;
                    float dTop = (0.5 - p.y) / duv.y;   // 上端からの距離(px)
                    float dBot = (0.5 + p.y) / duv.y;   // 下端からの距離(px)
                    float hl = (1.0 - smoothstep(w + 0.5, w + 2.0, dTop)) * step(w, dTop);
                    float sh = (1.0 - smoothstep(w + 0.5, w + 2.0, dBot)) * step(w, dBot);
                    rgb = lerp(rgb, float3(1, 1, 1), 0.35 * hl * close);
                    rgb *= 1.0 - 0.22 * sh * close;

                    rgb = lerp(rgb, kOutlineColor, outline);
                    a *= shapeAlpha; // 中継点マーカー(タグ3)の「食べる」は冒頭の eatMul で既に掛かっている(§5.4)
                }
                else if (IN.localUv.y > -0.5)
                {
                    // ---- Slide帯: 角丸なし。左右端の白い輪郭線＋帯中央の白線（note-visual-r1.md §5.2）。
                    // xのみ意味を持つ(0=左端/1=右端)。時間方向には分割の継ぎ目が出ないよう何もしない。
                    // 判定線での「食べる」は frag 冒頭の eatMul で fade/a に既に掛かっている。
                    float u = IN.localUv.x;
                    float duw = max(fwidth(u), 1e-5);
                    float edgeW = 1.2 * duw;
                    float edgeMask = saturate((1.0 - smoothstep(0.0, edgeW, u)) + (1.0 - smoothstep(0.0, edgeW, 1.0 - u)));
                    float centerW = 1.0 * duw;
                    float centerMask = 1.0 - smoothstep(0.0, centerW, abs(u - 0.5));
                    float lineMask = saturate(edgeMask + centerMask);
                    rgb = lerp(rgb, kOutlineColor, lineMask);

                    // gameplay-feel-r1.md §5.3: 地上帯は「暗めの半透明＋重なるほど明るく（上限4枚はステンシル）」。
                    // premultiplied 出力（Blend One OneMinusSrcAlpha）で「通常合成 α=_BandAlpha」と
                    // 「加算 _BandAdd×色」を1回で出す。1枚ごとの増分がほぼ一定になり段が読める。
                    // 頂点色の alpha（NoteColors.SlideGroundAlpha）はこのモードでは使わない。
                    if (_BandPremul > 0.5)
                    {
                        float na = fade * _BandAlpha;
                        return half4(rgb * fade * (_BandAlpha + _BandAdd), na);
                    }
                }
                // else: localUv.y<0（Riserの縁線・矢印等）→ SDF処理なし、頂点色そのまま。

                if (a <= 0.003) discard;
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
}
