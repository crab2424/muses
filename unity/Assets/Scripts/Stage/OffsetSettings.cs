using UnityEngine;

namespace Muses.Stage
{
    /// <summary>
    /// judgeOffsetMs / visualOffsetMs の実機キャリブレーション値を PlayerPrefs へ永続化する。
    /// Web版の localStorage 保存（実装ロードマップ Phase 0 項目G）に相当。
    /// StageConfig の他のフィールドは開発中の調整値であり、Unity Editor上ではシーンへの
    /// シリアライズで既に永続化されているため対象外（キャリブレーション値だけがビルド後の
    /// プレイヤー本人による実機調整を想定した値）。
    /// </summary>
    public static class OffsetSettings
    {
        private const string JudgeKey = "muses.judgeOffsetMs";
        private const string VisualKey = "muses.visualOffsetMs";

        /// <summary>保存済みの値があれば cfg に上書きする（無ければ cfg の値のまま何もしない）</summary>
        public static void Load(StageConfig cfg)
        {
            if (PlayerPrefs.HasKey(JudgeKey)) cfg.judgeOffsetMs = PlayerPrefs.GetFloat(JudgeKey);
            if (PlayerPrefs.HasKey(VisualKey)) cfg.visualOffsetMs = PlayerPrefs.GetFloat(VisualKey);
        }

        public static void SetJudgeOffsetMs(StageConfig cfg, float ms)
        {
            cfg.judgeOffsetMs = ms;
            PlayerPrefs.SetFloat(JudgeKey, ms);
            PlayerPrefs.Save();
        }

        public static void SetVisualOffsetMs(StageConfig cfg, float ms)
        {
            cfg.visualOffsetMs = ms;
            PlayerPrefs.SetFloat(VisualKey, ms);
            PlayerPrefs.Save();
        }
    }
}
