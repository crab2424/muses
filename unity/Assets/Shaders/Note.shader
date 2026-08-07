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
                float4 color : COLOR;
                float2 uv0 : TEXCOORD0; // x = aState, y = aNear
                float2 uv1 : TEXCOORD1; // x = aLayerF, y = aSide
                float2 uv2 : TEXCOORD2; // x = aScrollGroup（note-spec.md §5.5、_GroupX[] のインデックス）
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
                return OUT;
            }

            // note-visual-r1.md §2.2/§4/§9-6: 角丸矩形の符号付き距離関数（uvのローカル空間）。
            // p は中心を原点とした座標、b は半サイズ、r は角丸半径。標準的な定式化。
            float RoundedBoxSDF(float2 p, float2 b, float r)
            {
                float2 q = abs(p) - b + r;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (IN.state <= 0.001) discard;
                if (IN.depth > _Far) discard; // 最遠端で切る（両層共通）
                float aFar = _HardFar > 0.5 ? 1.0 : 1.0 - smoothstep(_Far * 0.7, _Far, IN.depth);
                // 手前端でフェードアウト。範囲を狭くして面の終端の先へノーツがはみ出さないようにする
                float aNear = smoothstep(IN.near * 0.90, IN.near, IN.depth);
                // riser-r2.md §3.1: 頂点色のalphaも乗せる。既存ノーツは全てalpha=1なので回帰なし。
                float a = IN.state * aFar * aNear * IN.color.a;
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
                    // ---- タップ形状（Tap/ExTap/Flick/Slideマーカー）: 角丸 + スクリーン空間一定幅の
                    // 白い輪郭線（note-visual-r1.md §2.2）。fwidth(dist)は「distがピクセルあたり
                    // どれだけ変化するか」＝透視変形込みのスクリーン空間換算係数になるため、
                    // これをアンチエイリアス幅・輪郭幅の単位として使うと自動的に画面上で一定幅になる。
                    // 実機フィードバック(2026-08-07): dist(スカラー)にfwidthを掛けると、
                    // ノーツが横に広く縦に薄い(このステージの遠近圧縮の帰結)場合、薄い方の
                    // 軸のスケールがaa全体を支配し、輪郭が塗りをほぼ飲み込んで彩度の高い色でも
                    // 白っぽく見えていた。座標を軸ごとにpx単位へ変換してから距離を測ることで
                    // 異方性に対応する（変換後は「1=1px」なのでaa/outlineWは定数でよい）。
                    // 厚み方向の座標は side から導く（QuadThinの4頂点で -1/+1 なので
                    // side*0.5+0.5 は旧 localUv.y={0,0,1,1} と完全に同値）。
                    float2 uv = float2(IN.localUv.x, IN.side * 0.5 + 0.5);
                    float2 p = uv - 0.5;
                    float2 duv = float2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
                    float2 pPx = p / duv;
                    float2 bPx = float2(0.5, 0.5) / duv;      // 画面上の半サイズ(px) x=幅方向 / y=厚み方向
                    float rPx = min(0.16 / max(duv.x, duv.y), min(bPx.x, bPx.y) * 0.9); // SDFの定義上 r<=min(b) が要る
                    float dist = RoundedBoxSDF(pPx, bPx, rPx);
                    float shapeAlpha = 1.0 - smoothstep(0.0, 1.0, dist);
                    if (shapeAlpha <= 0.003) discard;

                    // 輪郭線の太さは軸ごとに決める（2026-08-07: 両軸1.5px固定だと遠方で白一色になった）。
                    // 幅(x)方向: 「隣接ノーツの分離」という本来の役割(§2.2)があり、ノーツは遠方でも
                    //   幅方向には十分なpx数を持つので、常に1.5pxを狙う。
                    // 厚み(y)方向: 画面上の厚みは奥行きで23倍変わる(§3.1: 判定線で約14px、最遠部は
                    //   1px未満)。固定幅だと遠方で輪郭が塗りを完全に食い潰すため、半サイズに応じて
                    //   細らせ、1pxを切るあたりで0にする（＝遠方のノーツは輪郭なしの塗りだけになる）。
                    const float kOutlinePx = 1.5;
                    float2 w = min(kOutlinePx, max(0.0, bPx - 0.75) * 0.5);
                    float2 dEdge = (0.5 - abs(p)) / duv; // 各軸で端からの距離(px)
                    float2 outAxis = 1.0 - smoothstep(w - 0.5, w + 0.5, dEdge);
                    rgb = lerp(rgb, kOutlineColor, max(outAxis.x, outAxis.y));
                    a *= shapeAlpha;
                }
                else if (IN.localUv.y > -0.5)
                {
                    // ---- Slide帯: 角丸なし。左右端の白い輪郭線＋帯中央の白線（note-visual-r1.md §5.2）。
                    // xのみ意味を持つ(0=左端/1=右端)。時間方向には分割の継ぎ目が出ないよう何もしない。
                    float u = IN.localUv.x;
                    float duw = max(fwidth(u), 1e-5);
                    float edgeW = 1.2 * duw;
                    float edgeMask = saturate((1.0 - smoothstep(0.0, edgeW, u)) + (1.0 - smoothstep(0.0, edgeW, 1.0 - u)));
                    float centerW = 1.0 * duw;
                    float centerMask = 1.0 - smoothstep(0.0, centerW, abs(u - 0.5));
                    float lineMask = saturate(edgeMask + centerMask);
                    rgb = lerp(rgb, kOutlineColor, lineMask);
                }
                // else: localUv.y<0（Riserの縁線・矢印等）→ SDF処理なし、頂点色そのまま。

                if (a <= 0.003) discard;
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
}
