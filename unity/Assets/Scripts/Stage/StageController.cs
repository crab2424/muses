using UnityEngine;

namespace Muses.Stage
{
    /// <summary>
    /// main.ts の rebuild() 相当。cfg + カメラの aspect から Derived を導出し、
    /// カメラへ反映したうえで StageView にジオメトリの再構築を指示する。
    /// [ExecuteAlways]: Inspector 上の cfg 編集を Play せずに確認できるようにするため。
    /// </summary>
    [ExecuteAlways]
    public class StageController : MonoBehaviour
    {
        [SerializeField] private Camera cam;
        [SerializeField] private StageView view;
        [SerializeField] private StageConfig cfg = StageConfig.Default();

        private Derived derived;
        private float lastAspect = -1f;
        private bool dirty = true;

        public Derived Derived => derived;
        public StageConfig Config => cfg;

        private void Reset()
        {
            cam = Camera.main;
            view = GetComponent<StageView>();
        }

        private void OnValidate()
        {
            dirty = true;
        }

        private void Awake()
        {
            // GameController 等の他コンポーネントの Start() から Derived を読まれる可能性があるため、
            // 最初の Update() を待たずここで一度確定させておく（さもないと all-zero の Derived が
            // 読まれ、0除算由来の NaN がノーツ頂点に混入する）
            EnsureBuilt();
        }

        private void Update()
        {
            EnsureBuilt();
        }

        /// <summary>dirty またはアスペクト比変化があれば再導出する。何度呼んでも安全</summary>
        public void EnsureBuilt()
        {
            if (cam == null || view == null) return;

            float aspect = cam.aspect;
            if (dirty || !Mathf.Approximately(aspect, lastAspect))
            {
                Rebuild(aspect);
                dirty = false;
                lastAspect = aspect;
            }
        }

        private void Rebuild(float aspect)
        {
            derived = StageDerive.Derive(cfg, aspect);

            cam.fieldOfView = cfg.phiDeg;
            cam.nearClipPlane = Mathf.Max(0.01f, derived.zJudge * 0.01f);
            cam.farClipPlane = derived.drawFar * 1.5f;
            cam.transform.position = new Vector3(0f, cfg.yCam, 0f);
            cam.transform.localEulerAngles = new Vector3(derived.theta * Mathf.Rad2Deg, 0f, 0f);

            if (ColorUtility.TryParseHtmlString(cfg.bgColor, out var bg))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = bg;
            }

            view.Rebuild(cfg, derived);
        }
    }
}
