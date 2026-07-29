using UnityEngine;

namespace Muses.Stage
{
    /// <summary>
    /// StageDerive の移植確認用。空の GameObject にアタッチして Play すると
    /// Console に導出値を出力する（Web版と数値が一致するか目視確認するための一時スクリプト）。
    /// </summary>
    public class StageDeriveSmokeTest : MonoBehaviour
    {
        private void Start()
        {
            var cfg = StageConfig.Default();
            var d = StageDerive.Derive(cfg, 16f / 9f);

            Debug.Log($"theta={d.theta:F4} rad ({d.theta * 180f / Mathf.PI:F2} deg), " +
                      $"thetaRange=[{d.thetaMinDeg:F2}, {d.thetaMaxDeg:F2}] deg, clamped={d.thetaClamped}");
            Debug.Log($"zJudge={d.zJudge:F4}, skyHeight={d.skyHeight:F4}, zFar={d.zFar:F4}");
            Debug.Log($"groundWidth={d.groundWidth:F4}, skyWidth={d.skyWidth:F4}, " +
                      $"groundCellWidth={d.groundCellWidth:F4}, skyCellWidth={d.skyCellWidth:F4}");
            Debug.Log($"speed={d.speed:F4}, readAhead={d.readAhead:F4}, vHorizon={d.vHorizon:F4}");
        }
    }
}
