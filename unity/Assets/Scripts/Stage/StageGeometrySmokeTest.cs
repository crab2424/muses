using System.Text;
using UnityEngine;

namespace Muses.Stage
{
    /// <summary>
    /// StageGeometry の移植確認用。空の GameObject にアタッチして Play すると
    /// 頂点座標を Console に出力する。Web版の参照値（`node memory/verify/verify_stage_geometry.mjs` の出力、
    /// z の符号のみ反転して比較）と突き合わせるための一時スクリプト。
    /// </summary>
    public class StageGeometrySmokeTest : MonoBehaviour
    {
        private void Start()
        {
            var cfg = StageConfig.Default();
            var d = StageDerive.Derive(cfg, 16f / 9f);

            Debug.Log($"zJudge={d.zJudge:F4} zFar={d.zFar:F4} skyHeight={d.skyHeight:F4}");

            LogLayer("ground", cfg, d, Layer.Ground);
            LogLayer("sky", cfg, d, Layer.Sky);
        }

        private static void LogLayer(string name, StageConfig cfg, in Derived d, Layer layer)
        {
            bool ok = StageGeometry.Build(cfg, d, layer, out var geo);
            if (!ok)
            {
                Debug.Log($"-- {name} -- visible=false");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"-- {name} -- near/far = {geo.near:F4} {geo.far:F4}");

            if (geo.hasPlane)
            {
                sb.Append("plane = ");
                foreach (var v in geo.planeVertices) sb.Append(Fmt(v)).Append(' ');
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("plane = null");
            }

            int lineCount = geo.hasLines ? geo.lineVertices.Length / 2 : 0;
            sb.AppendLine($"lineCount = {lineCount}");
            if (geo.hasLines)
            {
                sb.AppendLine($"first line = {Fmt(geo.lineVertices[0])} {Fmt(geo.lineVertices[1])}");
                int n = geo.lineVertices.Length;
                sb.AppendLine($"last line = {Fmt(geo.lineVertices[n - 2])} {Fmt(geo.lineVertices[n - 1])}");
            }

            Debug.Log(sb.ToString());
        }

        private static string Fmt(Vector3 v) => $"[{v.x:F4}, {v.y:F4}, {v.z:F4}]";
    }
}
