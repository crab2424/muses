using Muses.Stage.Background;
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
        /// <summary>stage-bg-unity-port.md。None = 従来の単色（cfg.bgColor）。ゲームでは GameController が設定から入れる</summary>
        [SerializeField] private BackgroundTheme backgroundTheme = BackgroundTheme.None;
        private StageBackground background;

        private Derived derived;
        private float lastAspect = -1f;
        private bool dirty = true;

        public Derived Derived => derived;
        public StageConfig Config => cfg;

        /// <summary>ステージ背景。変えると次の EnsureBuilt で作り直す（譜面に依存しないので即時反映）</summary>
        public BackgroundTheme BackgroundTheme
        {
            get => backgroundTheme;
            set { if (backgroundTheme != value) { backgroundTheme = value; dirty = true; } }
        }

        /// <summary>Rebuild のたびに増える。cfg/Derived を読んで描く側（StageOverlay 等）が
        /// 「描き直しが必要か」を安く判定するためのもの（perf-r1.md §5）。</summary>
        public int Version { get; private set; }

        private void Reset()
        {
            cam = Camera.main;
            view = GetComponent<StageView>();
        }

        /// <summary>
        /// editor-spec.md §2.2。3Dプレビュー用のオフスクリーン rig をコードから組み立てるための
        /// プログラム的な配線口（Inspector 経由のシーン配線を前提にしない）。
        /// </summary>
        public void Configure(Camera camera, StageView stageView, StageConfig config)
        {
            cam = camera;
            view = stageView;
            cfg = config;
            dirty = true;
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

            // 背景（カメラ姿勢を決めた後に作る。暗部ゾーンは最遠端の投影から求めるため）
            if (backgroundTheme != BackgroundTheme.None || background != null)
            {
                if (background == null) background = GetComponent<StageBackground>();
                if (background == null) background = gameObject.AddComponent<StageBackground>();
                var b = background.Rebuild(backgroundTheme, cfg, derived, cam);
                if (b != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = b.ClearColor;
                    if (b.Tint.HasValue) view.ApplyTint(b.Tint.Value);
                }
            }
            Version++;
        }
    }
}
