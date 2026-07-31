using UnityEngine;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-spec.md §5.1。エディタのプレビュー再生用の時計。ゲーム本体の <see cref="Muses.Audio.SongClock"/>
    /// はdspTime基準の固定レートを前提にしているため流用せず、再生速度0.25x〜2.0xの変更に対応する
    /// 専用の時計を新設した。
    ///
    /// 音源(AudioClip)がある場合は <see cref="AudioSource.time"/> を songTime の正としてそのまま使う
    /// （AudioSource.pitch を変えると Unity が自動的にクリップ内の再生位置をその倍率で進めてくれるため、
    /// 独自クロックとの間でドリフトが起きない）。音源が無い場合（現状プロジェクトに音声アセットが
    /// 1つも無い、[[muses-unity-port-progress]]参照）は AudioSettings.dspTime 基準の無音クロックに
    /// フォールバックし、レート変更時はアンカーを組み直す（SongClock.Seek と同じ考え方）。
    /// </summary>
    public class PreviewClock
    {
        private readonly AudioSource source;
        private double pausedAt;
        private double silentT0;
        public bool Running { get; private set; }
        public float Rate { get; private set; } = 1f;

        public PreviewClock(AudioSource source)
        {
            this.source = source;
        }

        private bool HasClip => source != null && source.clip != null;

        public float SongTime
        {
            get
            {
                if (HasClip) return Running ? source.time : (float)pausedAt;
                return Running ? (float)((AudioSettings.dspTime - silentT0) * Rate) : (float)pausedAt;
            }
        }

        public void Play()
        {
            if (Running) return;
            if (HasClip)
            {
                source.time = Mathf.Clamp((float)pausedAt, 0f, Mathf.Max(0f, source.clip.length - 0.001f));
                source.pitch = Rate;
                source.Play();
            }
            else
            {
                silentT0 = AudioSettings.dspTime - pausedAt / Mathf.Max(0.0001f, Rate);
            }
            Running = true;
        }

        public void Pause()
        {
            if (!Running) return;
            if (HasClip)
            {
                pausedAt = source.time;
                source.Pause();
            }
            else
            {
                pausedAt = (AudioSettings.dspTime - silentT0) * Rate;
            }
            Running = false;
        }

        public void TogglePlay()
        {
            if (Running) Pause(); else Play();
        }

        /// <summary>任意位置へシークする。再生中・停止中どちらでも呼べる。</summary>
        public void Seek(float songTime)
        {
            songTime = Mathf.Max(0f, songTime);
            if (HasClip)
            {
                float clamped = Mathf.Clamp(songTime, 0f, Mathf.Max(0f, source.clip.length - 0.001f));
                source.time = clamped;
                pausedAt = clamped;
            }
            else
            {
                pausedAt = songTime;
                if (Running) silentT0 = AudioSettings.dspTime - songTime / Mathf.Max(0.0001f, Rate);
            }
        }

        /// <summary>0.25x〜2.0xの再生速度。ピッチは保持しない(そのまま変わる、editor-spec.md §5.1で許容済み)。</summary>
        public void SetRate(float rate)
        {
            rate = Mathf.Clamp(rate, 0.25f, 2f);
            if (HasClip)
            {
                Rate = rate;
                source.pitch = rate; // AudioSource.time はpitch変更後も自動的にこの倍率で進む
            }
            else
            {
                float cur = SongTime;
                Rate = rate;
                if (Running) silentT0 = AudioSettings.dspTime - cur / Mathf.Max(0.0001f, Rate);
                else pausedAt = cur;
            }
        }

        public void Stop()
        {
            Running = false;
            pausedAt = 0;
            if (HasClip) source.Stop();
        }
    }
}
