using UnityEngine;
using Muses.Gameplay;

namespace Muses.Audio
{
    /// <summary>
    /// song-play-flow-r1.md §7。判定成立(MISS以外)のたび鳴らすヒットSE。
    /// エディタ側(PreviewSystem)のような先読みスケジュール(PlayScheduled)は使わない
    /// ――プレイヤーが実際に叩いた瞬間の音なので、時刻は既知ではなく「今」が正しい。
    /// SE素材(Assets/Audio/SE/)はまだ無いため、SongClock.BuildClickClip と同じ手法
    /// （減衰サイン波の実行時合成）で最小限の音を用意する。素材を用意したくなったら
    /// AudioClip をコンストラクタで差し替えられるようにしてある。
    /// </summary>
    public class HitSePlayer
    {
        private readonly AudioSource source;
        private readonly AudioClip clip;

        public HitSePlayer(AudioSource source, AudioClip clipOverride = null)
        {
            this.source = source;
            clip = clipOverride != null ? clipOverride : BuildClip();
        }

        public void OnJudged(JudgeKind kind)
        {
            if (source == null || clip == null) return;
            source.PlayOneShot(clip, 0.6f);
        }

        private static AudioClip BuildClip()
        {
            const int sampleRate = 44100;
            int length = (int)(sampleRate * 0.05f);
            var clip = AudioClip.Create("HitSe", length, 1, sampleRate, false);
            var data = new float[length];
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)sampleRate;
                float env = Mathf.Exp(-t * 80f); // 約35msで減衰
                data[i] = Mathf.Sin(2f * Mathf.PI * 2000f * t) * env * 0.6f;
            }
            clip.SetData(data, 0);
            return clip;
        }
    }
}
