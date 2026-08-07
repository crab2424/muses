using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Muses.Stage;

namespace Muses.Notes
{
    /// <summary>
    /// ノーツの Mesh/Material 管理。頂点生成は <see cref="NoteGeometry"/> が担当する。
    /// 移植元: web-prototype/src/notes.ts の NoteField（THREE依存部分を除く）。
    /// </summary>
    [ExecuteAlways]
    public class NoteView : MonoBehaviour
    {
        [SerializeField] private Shader noteShader;
        [SerializeField] private Shader beatLineShader;

        /// <summary>editor-spec.md §2.2。3Dプレビューのオフスクリーン rig をコードから組み立てる際に、
        /// Inspector 配線なしでシェーダを渡すための口。</summary>
        public void ConfigureShaders(Shader note, Shader beatLine)
        {
            noteShader = note;
            beatLineShader = beatLine;
        }

        [Header("タップ/ホールド始点の奥行き厚み（詳細は NotePlacement.hlsl のコメント）")]
        [Tooltip("ワールド固定の厚み。judge線の奥行きに対する割合（半幅）。遠近感を出す本来の厚み")]
        // editor-ui-rework-r13.md §7.1: SampleScene.unity上のInspector調整値(0.06)と乖離していた
        // （プレビューはこのC#既定値をそのまま使うコード組み立てのrigのため、シーンの調整が
        // 反映されなかった）。§7.9: 実機と見比べたユーザーがfrac=0.06 / minFrac=0.01で確定。
        [SerializeField] private float thicknessFrac = 0.06f;
        [Tooltip("画面上の最小厚みを保つための下限。現在の奥行きに対する割合（半幅）。遠方の点滅防止にのみ効く")]
        [SerializeField] private float thicknessMinFrac = 0.01f;

        [Tooltip("note-visual-r1.md §3: 空中ノーツの画面上の厚みを地上と揃える係数。既定1.96は" +
            "奥行き再マップ後の空中/地上の画面厚み比(0.509)の逆数（理論上、地上と完全一致する上限）。" +
            "layerFで連続的に補間するため、層を跨ぐSlideでも自然に効く。")]
        [SerializeField] private float skyThicknessMul = 1.96f;

        public float SkyThicknessMul
        {
            get => skyThicknessMul;
            set { skyThicknessMul = value; ApplyThicknessUniforms(); }
        }

        /// <summary>editor-ui-rework-r13.md §7.1追補: C#既定値をシーンの調整値へ合わせても
        /// プレビューで見た目の改善が確認できなかったため、実機で値を振って原因切り分けできるよう
        /// 実行時に調整可能にする（プレビュー設定タブのスライダーから使う）。</summary>
        public float ThicknessFrac
        {
            get => thicknessFrac;
            set { thicknessFrac = value; ApplyThicknessUniforms(); }
        }
        public float ThicknessMinFrac
        {
            get => thicknessMinFrac;
            set { thicknessMinFrac = value; ApplyThicknessUniforms(); }
        }

        private void ApplyThicknessUniforms()
        {
            if (notesMaterial != null)
            {
                notesMaterial.SetFloat("_ThicknessFrac", thicknessFrac);
                notesMaterial.SetFloat("_ThicknessMinFrac", thicknessMinFrac);
                notesMaterial.SetFloat("_SkyThicknessMul", skyThicknessMul);
            }
            if (beatMaterial != null)
            {
                beatMaterial.SetFloat("_ThicknessFrac", thicknessFrac);
                beatMaterial.SetFloat("_ThicknessMinFrac", thicknessMinFrac);
                beatMaterial.SetFloat("_SkyThicknessMul", skyThicknessMul);
            }
        }

        // StageView が GroundPlane/GroundLines/SkyPlane/SkyLines に 3000〜3003 を使う。
        // Notes側にキューを明示しないとデフォルト(3000)でGroundPlaneと同キューになり、
        // 距離ソート次第でほぼ不透明なGroundPlaneが加算合成のノーツを覆い隠してしまう
        // （groundFillAlpha=1 のときに顕著）。必ずステージより後ろに描画されるよう固定する。
        private const int NotesRenderQueue = 3010;
        private const int BeatLinesRenderQueue = 3011;

        /// <summary>note-spec.md §5.5。シェーダの _GroupX[] と同じ長さで固定（グループ数に理論上限はないが、
        /// GPUへ渡す配列は実装上の上限を設ける。Note.shader の MUSES_MAX_SCROLL_GROUPS と一致させること）。</summary>
        private const int MaxScrollGroups = 16;

        public List<NoteRuntime> Runtimes { get; private set; } = new();

        private Dictionary<int, Chart.ScrollTimeline> scrollTimelines = new();
        private readonly float[] groupXBuffer = new float[MaxScrollGroups];
        private float baseSpeed;

        private GameObject notesGo;
        private MeshFilter notesFilter;
        private MeshRenderer notesRenderer;
        private Mesh notesMesh;
        private Material notesMaterial;
        private Vector2[] notesUv0;

        private GameObject beatGo;
        private MeshFilter beatFilter;
        private MeshRenderer beatRenderer;
        private Mesh beatMesh;
        private Material beatMaterial;

        public void Build(StageConfig cfg, in Derived d, List<Chart.Note> notes,
            Dictionary<int, Chart.ScrollTimeline> scrollTimelines = null, List<float> barTimes = null)
        {
            if (noteShader == null || beatLineShader == null)
            {
                Debug.LogWarning("NoteView: シェーダが未設定です。", this);
                return;
            }

            this.scrollTimelines = scrollTimelines ?? new Dictionary<int, Chart.ScrollTimeline>();
            baseSpeed = d.speed;

            var data = NoteGeometry.Build(cfg, d, notes, this.scrollTimelines, barTimes);
            Runtimes = data.runtimes;

            EnsureNotesObject();
            notesMesh.Clear();
            // 既定の16bitインデックス（上限65535頂点）では長時間譜面で頂点数が溢れ、
            // インデックスが 65536 で巻き戻って「あるノーツの最終頂点＋次のノーツの先頭2頂点」を
            // 結ぶ三角形が大量に生まれる（65536 mod 3 = 1 なので巻き戻り後は3頂点境界からずれる）。
            // 600秒・BPM150のデモ譜面で既に約8万頂点あるため必須。
            notesMesh.indexFormat = IndexFormat.UInt32;
            notesMesh.SetVertices(data.positions);
            notesMesh.SetColors(data.colors);
            notesUv0 = Pack(data.state, data.near);
            notesMesh.SetUVs(0, notesUv0);
            notesMesh.SetUVs(1, Pack(data.layerF, data.side));
            notesMesh.SetUVs(2, Pack(data.group, null));
            // note-visual-r1.md §3-3/§8-3: SDF描画(角丸+輪郭線)用のローカルUV。
            notesMesh.SetUVs(3, data.localUv);
            var tris = new int[data.positions.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = i;
            notesMesh.SetTriangles(tris, 0);
            notesMesh.RecalculateBounds();
            notesRenderer.enabled = data.positions.Length > 0;

            EnsureBeatObject();
            beatMesh.Clear();
            beatMesh.indexFormat = IndexFormat.UInt32; // 現状は数百頂点だが、長尺譜面での溢れを防ぐため合わせておく
            beatMesh.SetVertices(data.beatPositions);
            beatMesh.SetUVs(0, Pack(null, data.beatNear));
            beatMesh.SetUVs(1, Pack(data.beatLayerF, null));
            var beatIdx = new int[data.beatPositions.Length];
            for (int i = 0; i < beatIdx.Length; i++) beatIdx[i] = i;
            beatMesh.SetIndices(beatIdx, MeshTopology.Lines, 0);
            beatMesh.RecalculateBounds();
            beatRenderer.enabled = data.beatPositions.Length > 0;

            ApplyStaticUniforms(cfg, d);
        }

        /// <summary>
        /// note-spec.md §5.5。毎フレーム呼ぶ。グループごとの表示位置 X(songTime) を求めて
        /// シェーダの _GroupX[] に渡し、実効速度 baseSpeed×hiSpeed を _Speed に渡す。
        /// scrollEvents を持たないグループは Chart.ScrollTimeline.Identity（X(t)=t）を使うため、
        /// ソフラン未使用の譜面では従来（_SongTime を直接引く）と同じ結果になる。
        /// </summary>
        public void UpdateScroll(float songTime, float hiSpeed)
        {
            for (int g = 0; g < MaxScrollGroups; g++)
            {
                var tl = scrollTimelines.TryGetValue(g, out var found) ? found : Chart.ScrollTimeline.Identity;
                groupXBuffer[g] = tl.XAt(songTime);
            }

            float speed = baseSpeed * hiSpeed;
            if (notesMaterial != null)
            {
                notesMaterial.SetFloatArray("_GroupX", groupXBuffer);
                notesMaterial.SetFloat("_Speed", speed);
            }
            if (beatMaterial != null)
            {
                beatMaterial.SetFloatArray("_GroupX", groupXBuffer);
                beatMaterial.SetFloat("_Speed", speed);
            }
        }

        private bool alphaDirty;

        /// <summary>ノーツの表示状態を更新（0で非表示）。値が変わらないときは何もしない。
        /// editor-ui-rework-r13.md §7.3: 呼び出しのたびにメッシュ全体(数万頂点)を再アップロード
        /// していたのが再生中・シーク時のフレームレート悪化の主因だった。ここでは配列を書き換える
        /// だけにして実転送はFlushAlphaへ切り出す（1フレームに最大1回にまとめるため）。</summary>
        public void SetNoteAlpha(NoteRuntime rt, float alpha)
        {
            if (notesUv0 == null || rt.alpha == alpha) return;
            rt.alpha = alpha;
            for (int i = rt.vStart; i < rt.vStart + rt.vCount; i++) notesUv0[i].x = alpha;
            alphaDirty = true;
        }

        /// <summary>SetNoteAlphaで書き換えた分をまとめてGPUへ転送する。judge.Update / judge.Seek /
        /// noteView.Build を呼んだ直後に1回だけ呼ぶ規則（r13 §7.3）。呼ばなければ描画は更新されない。</summary>
        public void FlushAlpha()
        {
            if (!alphaDirty || notesUv0 == null) return;
            notesMesh.SetUVs(0, notesUv0);
            alphaDirty = false;
        }

        private void ApplyStaticUniforms(StageConfig cfg, in Derived d)
        {
            // ローカル関数は in パラメータを直接キャプチャできない (CS1628) ため値コピーを介す
            Derived dCopy = d;
            float laneConverge = Mathf.Clamp01(cfg.laneConverge);

            void Apply(Material m)
            {
                m.SetFloat("_ZJudge", dCopy.zJudge);
                // _Speed は note-spec.md §5.5 により hiSpeed 込みで毎フレーム変わるため UpdateScroll() 側で設定する
                m.SetFloat("_Far", dCopy.zFar);
                m.SetFloat("_HardFar", cfg.hardFarEdge ? 1f : 0f);
                m.SetFloat("_YCam", cfg.yCam);
                m.SetFloat("_SkyHeight", dCopy.skyHeight);
                m.SetFloat("_SinTheta", dCopy.sinTheta);
                m.SetFloat("_CosTheta", dCopy.cosTheta);
                m.SetFloat("_LaneK", dCopy.laneK);
                m.SetFloat("_LaneConverge", laneConverge);
                m.SetFloat("_ZcFarGround", dCopy.zcFarGround);
                // 2026-08-06: 層別の奥行き再マップ（NotePlacement.hlslのPlaceNote参照）用。
                // 判定線・最遠端のv値はシェーダ側で_ZJudge/_Farから直接求めるので渡さない
                // （そのほうがpgが構成上厳密に[0,1]に収まり、zFarのクランプ時にもズレない）。
                m.SetFloat("_TanHalfPhi", dCopy.tanHalfPhi);
                m.SetFloat("_ThicknessFrac", thicknessFrac);
                m.SetFloat("_ThicknessMinFrac", thicknessMinFrac);
                m.SetFloat("_SkyThicknessMul", skyThicknessMul);
            }

            Apply(notesMaterial);
            Apply(beatMaterial);
        }

        private static Vector2[] Pack(float[] a, float[] b)
        {
            int n = a?.Length ?? b?.Length ?? 0;
            var outArr = new Vector2[n];
            for (int i = 0; i < n; i++) outArr[i] = new Vector2(a != null ? a[i] : 0f, b != null ? b[i] : 0f);
            return outArr;
        }

        private void EnsureNotesObject()
        {
            if (notesGo != null) return;
            var existing = transform.Find("Notes");
            notesGo = existing != null ? existing.gameObject : new GameObject("Notes");
            if (existing == null) notesGo.transform.SetParent(transform, false);
            notesGo.hideFlags = HideFlags.DontSave;
            notesFilter = notesGo.GetComponent<MeshFilter>();
            if (notesFilter == null) notesFilter = notesGo.AddComponent<MeshFilter>();
            notesRenderer = notesGo.GetComponent<MeshRenderer>();
            if (notesRenderer == null) notesRenderer = notesGo.AddComponent<MeshRenderer>();
            notesRenderer.shadowCastingMode = ShadowCastingMode.Off;
            notesRenderer.receiveShadows = false;
            notesMesh = notesFilter.sharedMesh != null ? notesFilter.sharedMesh : new Mesh { name = "Notes" };
            notesMesh.MarkDynamic();
            notesFilter.sharedMesh = notesMesh;
            bool needsMat = notesRenderer.sharedMaterial == null || notesRenderer.sharedMaterial.shader != noteShader;
            notesMaterial = needsMat ? new Material(noteShader) { name = "Notes" } : notesRenderer.sharedMaterial;
            notesMaterial.renderQueue = NotesRenderQueue;
            notesRenderer.sharedMaterial = notesMaterial;
        }

        private void EnsureBeatObject()
        {
            if (beatGo != null) return;
            var existing = transform.Find("BeatLines");
            beatGo = existing != null ? existing.gameObject : new GameObject("BeatLines");
            if (existing == null) beatGo.transform.SetParent(transform, false);
            beatGo.hideFlags = HideFlags.DontSave;
            beatFilter = beatGo.GetComponent<MeshFilter>();
            if (beatFilter == null) beatFilter = beatGo.AddComponent<MeshFilter>();
            beatRenderer = beatGo.GetComponent<MeshRenderer>();
            if (beatRenderer == null) beatRenderer = beatGo.AddComponent<MeshRenderer>();
            beatRenderer.shadowCastingMode = ShadowCastingMode.Off;
            beatRenderer.receiveShadows = false;
            beatMesh = beatFilter.sharedMesh != null ? beatFilter.sharedMesh : new Mesh { name = "BeatLines" };
            beatMesh.MarkDynamic();
            beatFilter.sharedMesh = beatMesh;
            bool needsMat = beatRenderer.sharedMaterial == null || beatRenderer.sharedMaterial.shader != beatLineShader;
            beatMaterial = needsMat ? new Material(beatLineShader) { name = "BeatLines" } : beatRenderer.sharedMaterial;
            beatMaterial.renderQueue = BeatLinesRenderQueue;
            beatRenderer.sharedMaterial = beatMaterial;
        }

        private void OnDestroy()
        {
            DestroyObj(notesGo);
            DestroyObj(notesMesh);
            DestroyObj(notesMaterial);
            DestroyObj(beatGo);
            DestroyObj(beatMesh);
            DestroyObj(beatMaterial);
        }

        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
