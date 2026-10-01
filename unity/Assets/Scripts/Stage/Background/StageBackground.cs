using UnityEngine;

namespace Muses.Stage.Background
{
    /// <summary>
    /// ステージ背景の管理（memory/game/stage-bg-unity-port.md）。StageController が自分の GameObject に付けて、
    /// Rebuild のたび（cfg・画面比・テーマの変更時）に <see cref="Rebuild"/> を呼ぶ。
    /// 背景のパーツは子 "StageBackground" の下に作り、シーンには保存しない（DontSave）。
    /// </summary>
    [ExecuteAlways]
    public class StageBackground : MonoBehaviour
    {
        private BackgroundBuilder builder;
        private BackgroundParts parts;
        private Transform root;

        private static readonly int IdTime = Shader.PropertyToID("_MusesBgTime");
        private static readonly int IdAspect = Shader.PropertyToID("_MusesBgAspect");
        private static readonly int IdZone = Shader.PropertyToID("_MusesBgZone");
        private static readonly int IdFeather = Shader.PropertyToID("_MusesBgZoneFeather");
        private static readonly int IdCamPos = Shader.PropertyToID("_MusesBgCamPos");
        private static readonly int IdCamFwd = Shader.PropertyToID("_MusesBgCamFwd");
        private static readonly int IdCamRight = Shader.PropertyToID("_MusesBgCamRight");
        private static readonly int IdCamUp = Shader.PropertyToID("_MusesBgCamUp");
        private static readonly int IdTanHalfFov = Shader.PropertyToID("_MusesBgTanHalfFov");

        public static BackgroundBuilder CreateBuilder(BackgroundTheme theme)
        {
            switch (theme)
            {
                case BackgroundTheme.OrbitCity: return new Themes.OrbitCityBackground();
                case BackgroundTheme.Sanctuary: return new Themes.SanctuaryBackground();
                default: return null;
            }
        }

        /// <summary>
        /// 背景を作り直す。カメラの姿勢・画角を確定させた後に呼ぶこと。theme が None なら背景を消して null を返す。
        /// 戻り値のビルダーから ClearColor / Tint を読んでカメラとステージに反映するのは呼び出し側（StageController）。
        /// </summary>
        public BackgroundBuilder Rebuild(BackgroundTheme theme, StageConfig cfg, in Derived d, Camera cam)
        {
            Clear();
            builder = CreateBuilder(theme);
            if (builder == null) return null;

            EnsureRoot();
            // ドメインリロード（エディタ）で参照を失った前回のパーツが残っていれば消す
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
            }
            parts = new BackgroundParts(root);
            var ctx = new BackgroundContext { cfg = cfg, d = d, cam = cam, aspect = cam.aspect };
            ctx.zone = BackgroundContext.ComputeZone(ctx);
            SetGlobals(ctx);
            try
            {
                builder.Build(ctx, parts);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
            return builder;
        }

        private static void SetGlobals(BackgroundContext ctx)
        {
            var t = ctx.cam.transform;
            Shader.SetGlobalFloat(IdAspect, ctx.aspect);
            Shader.SetGlobalVector(IdZone, new Vector4(ctx.zone.u0, ctx.zone.v0, ctx.zone.u1, ctx.zone.v1));
            Shader.SetGlobalFloat(IdFeather, ctx.zone.feather);
            Shader.SetGlobalVector(IdCamPos, t.position);
            Shader.SetGlobalVector(IdCamFwd, t.forward);
            Shader.SetGlobalVector(IdCamRight, t.right);
            Shader.SetGlobalVector(IdCamUp, t.up);
            Shader.SetGlobalFloat(IdTanHalfFov, Mathf.Tan(ctx.cam.fieldOfView * Mathf.Deg2Rad * 0.5f));
        }

        private void Update()
        {
            if (builder == null) return;
            // 実時間（音楽・一時停止・timeScale に反応しない。ラボと同じ）
            float t = Time.realtimeSinceStartup;
            Shader.SetGlobalFloat(IdTime, t);
            builder.Tick(t);
        }

        private void EnsureRoot()
        {
            if (root != null) return;
            var existing = transform.Find("StageBackground");
            if (existing != null) root = existing;
            else
            {
                var go = new GameObject("StageBackground") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                root = go.transform;
            }
            // StageController の Transform がどこにあってもワールド原点基準で置く
            root.position = Vector3.zero;
            root.rotation = Quaternion.identity;
            root.localScale = Vector3.one;
        }

        private void Clear()
        {
            parts?.DestroyAll();
            parts = null;
            builder = null;
        }

        private void OnDestroy()
        {
            Clear();
            if (root != null)
            {
                if (Application.isPlaying) Destroy(root.gameObject); else DestroyImmediate(root.gameObject);
            }
        }
    }
}
