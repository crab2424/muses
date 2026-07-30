using UnityEngine;

namespace Muses.Audio
{
    /// <summary>
    /// 曲時刻の供給源。移植元: web-prototype/src/audio.ts の Clock。
    /// AudioContext.currentTime の代わりに AudioSettings.dspTime を基準にする
    /// （Time.time は描画フレーム基準でずれるため、音との同期には dspTime を使う）。
    ///
    /// メトロノームは簡略化: Web版の先読みスケジューリング（AudioParamへのRampで極めて正確）ではなく、
    /// 毎フレームのポーリングで拍を跨いだ瞬間に単発再生する。クリック音自体は外部アセット無しで、
    /// 短い減衰サイン波を実行時に合成する（Web版の OscillatorNode + GainNode ランプを模したもの）。
    /// </summary>
    public class SongClock
    {
        private double t0;
        private double nextBeat;
        public bool Running { get; private set; }

        private readonly AudioSource source;
        private readonly AudioClip accentClip;
        private readonly AudioClip normalClip;

        public SongClock(AudioSource source)
        {
            this.source = source;
            accentClip = BuildClickClip(1600f);
            normalClip = BuildClickClip(1000f);
        }

        public void Start()
        {
            t0 = AudioSettings.dspTime;
            nextBeat = 0;
            Running = true;
        }

        public float SongTime => Running ? (float)(AudioSettings.dspTime - t0) : 0f;

        public void TickMetronome(float bpm, bool enabled)
        {
            if (!Running || source == null) return;
            float b = 60f / bpm;
            float ahead = SongTime + 0.05f;
            while (nextBeat < ahead)
            {
                if (enabled && nextBeat >= 0)
                {
                    bool accent = nextBeat % (b * 4f) < b * 0.5f;
                    source.PlayOneShot(accent ? accentClip : normalClip, accent ? 0.5f : 0.25f);
                }
                nextBeat += b;
            }
        }

        private static AudioClip BuildClickClip(float freq)
        {
            const int sampleRate = 44100;
            int length = (int)(sampleRate * 0.06f);
            var clip = AudioClip.Create($"Click{freq:F0}", length, 1, sampleRate, false);
            var data = new float[length];
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)sampleRate;
                float env = Mathf.Exp(-t * 60f); // 約50msで減衰
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.5f;
            }
            clip.SetData(data, 0);
            return clip;
        }
    }
}
