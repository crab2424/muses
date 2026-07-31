using UnityEngine;
using UnityEngine.Rendering;

namespace Muses.Stage
{
    /// <summary>
    /// ステージ4パーツ（地上/空中 × 面/線）の Mesh・Material・GameObject を管理する。
    /// 頂点生成は行わない（<see cref="StageGeometry"/> が担当）。Rebuild は cfg 変更のたびに呼ぶ想定で、
    /// Mesh・GameObject は使い回す（毎回 new すると GC を踏む）。
    /// [ExecuteAlways]: Play せずに Scene ビューでステージを確認できるようにするため。
    /// </summary>
    [ExecuteAlways]
    public class StageView : MonoBehaviour
    {
        [SerializeField] private Shader stageShader;

        /// <summary>editor-spec.md §2.2。3Dプレビューのオフスクリーン rig をコードから組み立てる際に、
        /// Inspector 配線なしでシェーダを渡すための口。</summary>
        public void ConfigureShader(Shader shader) => stageShader = shader;

        private const int RenderQueueBase = 3000;

        private LayerView groundPlane;
        private LayerView groundLines;
        private LayerView skyPlane;
        private LayerView skyLines;

        private class LayerView
        {
            public GameObject go;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public Mesh mesh;
            public Material material;
        }

        public void Rebuild(StageConfig cfg, in Derived d)
        {
            if (stageShader == null)
            {
                Debug.LogWarning("StageView: Stage Shader が未設定です。", this);
                return;
            }

            EnsureLayer(ref groundPlane, "GroundPlane", RenderQueueBase + 0);
            EnsureLayer(ref groundLines, "GroundLines", RenderQueueBase + 2);
            EnsureLayer(ref skyPlane, "SkyPlane", RenderQueueBase + 1);
            EnsureLayer(ref skyLines, "SkyLines", RenderQueueBase + 3);

            if (!cfg.showLaneFloor)
            {
                SetActive(groundPlane, false);
                SetActive(groundLines, false);
                SetActive(skyPlane, false);
                SetActive(skyLines, false);
                return;
            }

            ApplyLayer(groundPlane, groundLines, cfg, d, Layer.Ground);
            ApplyLayer(skyPlane, skyLines, cfg, d, Layer.Sky);
        }

        private void ApplyLayer(LayerView planeView, LayerView linesView, StageConfig cfg, in Derived d, Layer layer)
        {
            bool ok = StageGeometry.Build(cfg, d, layer, out var geo);
            if (!ok)
            {
                SetActive(planeView, false);
                SetActive(linesView, false);
                return;
            }

            if (geo.hasPlane)
            {
                SetActive(planeView, true);
                var mesh = planeView.mesh;
                mesh.Clear();
                mesh.SetVertices(geo.planeVertices);
                mesh.SetTriangles(geo.planeTriangles, 0);
                mesh.RecalculateBounds();
                ApplyMaterial(planeView.material, geo.planeColor, geo.planeAlpha, geo.near, geo.far, geo.hardFarEdge);
            }
            else
            {
                SetActive(planeView, false);
            }

            if (geo.hasLines)
            {
                SetActive(linesView, true);
                var mesh = linesView.mesh;
                mesh.Clear();
                mesh.SetVertices(geo.lineVertices);
                mesh.SetIndices(geo.lineIndices, MeshTopology.Lines, 0);
                mesh.RecalculateBounds();
                ApplyMaterial(linesView.material, geo.lineColor, geo.lineAlpha, geo.near, geo.far, geo.hardFarEdge);
            }
            else
            {
                SetActive(linesView, false);
            }
        }

        private static void ApplyMaterial(Material mat, Color color, float alpha, float near, float far, bool hardFar)
        {
            mat.SetColor("_Color", color);
            mat.SetFloat("_Alpha", alpha);
            mat.SetFloat("_Near", near);
            mat.SetFloat("_Far", far);
            mat.SetFloat("_HardFar", hardFar ? 1f : 0f);
        }

        /// <summary>
        /// 同名の子 GameObject が既にあれば再利用する。ExecuteAlways のスクリプトは
        /// エディタのドメインリロードで非シリアライズフィールドの参照を失うため、
        /// 参照だけで判断すると再生成のたびに重複した子が増えてしまう。
        /// </summary>
        private void EnsureLayer(ref LayerView lv, string name, int renderQueue)
        {
            if (lv != null && lv.go != null) return;

            var existing = transform.Find(name);
            lv = new LayerView();
            if (existing != null)
            {
                lv.go = existing.gameObject;
            }
            else
            {
                lv.go = new GameObject(name);
                lv.go.transform.SetParent(transform, false);
            }
            lv.go.hideFlags = HideFlags.DontSave;

            lv.filter = lv.go.GetComponent<MeshFilter>();
            if (lv.filter == null) lv.filter = lv.go.AddComponent<MeshFilter>();

            lv.renderer = lv.go.GetComponent<MeshRenderer>();
            if (lv.renderer == null) lv.renderer = lv.go.AddComponent<MeshRenderer>();
            lv.renderer.shadowCastingMode = ShadowCastingMode.Off;
            lv.renderer.receiveShadows = false;

            lv.mesh = lv.filter.sharedMesh != null ? lv.filter.sharedMesh : new Mesh { name = name };
            lv.mesh.MarkDynamic();
            lv.filter.sharedMesh = lv.mesh;

            bool needsMaterial = lv.renderer.sharedMaterial == null || lv.renderer.sharedMaterial.shader != stageShader;
            lv.material = needsMaterial ? new Material(stageShader) { name = name } : lv.renderer.sharedMaterial;
            lv.material.renderQueue = renderQueue;
            lv.renderer.sharedMaterial = lv.material;
        }

        private static void SetActive(LayerView lv, bool active)
        {
            if (lv?.go != null && lv.go.activeSelf != active) lv.go.SetActive(active);
        }

        private void OnDestroy()
        {
            DestroyLayer(groundPlane);
            DestroyLayer(groundLines);
            DestroyLayer(skyPlane);
            DestroyLayer(skyLines);
        }

        private static void DestroyLayer(LayerView lv)
        {
            if (lv == null) return;
            if (Application.isPlaying)
            {
                if (lv.go != null) Destroy(lv.go);
                if (lv.mesh != null) Destroy(lv.mesh);
                if (lv.material != null) Destroy(lv.material);
            }
            else
            {
                if (lv.go != null) DestroyImmediate(lv.go);
                if (lv.mesh != null) DestroyImmediate(lv.mesh);
                if (lv.material != null) DestroyImmediate(lv.material);
            }
        }
    }
}
