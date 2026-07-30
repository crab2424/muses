using Muses.Stage;

namespace Muses.Gameplay
{
    /// <summary>移植元: web-prototype/src/judge.ts の Score</summary>
    public class Score
    {
        public int perfect;
        public int good;
        public int miss;
        public int combo;
        public int maxCombo;
        public string lastJudge = "";
        public float lastMs;
    }

    public enum JudgeKind
    {
        Perfect,
        Good,
        Miss,
    }

    /// <summary>移植元: web-prototype/src/overlay.ts の HitFlash</summary>
    public struct HitFlash
    {
        public Layer layer;
        public int cell;
        public float width;
        public float born;
        public JudgeKind kind;
    }
}
