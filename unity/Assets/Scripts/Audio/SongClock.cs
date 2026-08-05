using System;
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
    ///
    /// フレーム補間について（ipad-build-issues-r1.md ②-B）: AudioSettings.dspTime は
    /// DSPバッファ単位でしか更新されない（実機で実測: 約23〜25Hz）。これをそのままノーツの
    /// 描画位置に使うと、描画は120Hzで回っていてもノーツは23〜25Hzでしか動かず「コマ落ち」して見える。
    /// SongTime は dspTime を「正解」として保持しつつ、dspTime が更新されないフレームは
    /// Time.unscaledDeltaTime で滑らかに前進させ、次にdspTimeが更新された時点でずれを補正する
    /// （音との同期精度は保ったまま、見た目だけを滑らかにする）。
    /// </summary>
    public class SongClock
    {
        private double t0;
        private double nextBeat;
        /// <summary>Pause() 時点の SongTime を保持する（Running=false の間、SongTime はここで凍結する）</summary>
        private double pausedAt;
        public bool Running { get; private set; }

        private readonly AudioSource source;
        private readonly AudioClip accentClip;
        private readonly AudioClip normalClip;

        // ---- フレーム補間用の状態 ----
        private double lastObservedDsp = -1;
        private double smoothed;
        /// <summary>この秒数を超えてdspTime基準の値とズレたら、補間せず即座にスナップする
        /// （Seek直後や大きなハング直後の想定）。</summary>
        private const double SnapThreshold = 0.05;
        /// <summary>毎フレーム、ズレのこの割合だけ縮める（音ズレを一気にではなく滑らかに吸収する）。</summary>
        private const double DriftCorrectionRate = 0.10;

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
            pausedAt = 0;
            lastObservedDsp = t0;
            smoothed = 0;
            Running = true;
        }

        /// <summary>
        /// implementation-roadmap.md 項目F。曲時刻を止める（開発中の判定確認・エディタでの一時停止用）。
        /// SongTime は Pause() 時点の値のまま凍結される。
        /// </summary>
        public void Pause()
        {
            if (!Running) return;
            pausedAt = AudioSettings.dspTime - t0;
            Running = false;
        }

        /// <summary>Pause() で止めた地点から再開する。</summary>
        public void Resume()
        {
            if (Running) return;
            t0 = AudioSettings.dspTime - pausedAt;
            lastObservedDsp = AudioSettings.dspTime;
            smoothed = pausedAt;
            Running = true;
        }

        /// <summary>
        /// implementation-roadmap.md 項目D。任意時刻へジャンプする（実行中・一時停止中どちらでも呼べる）。
        /// メトロノームの次拍もこの時刻基準で組み直す。
        /// </summary>
        public void Seek(double songTime)
        {
            songTime = Math.Max(0, songTime);
            if (Running)
            {
                t0 = AudioSettings.dspTime - songTime;
                lastObservedDsp = AudioSettings.dspTime;
            }
            else
            {
                pausedAt = songTime;
            }
            smoothed = songTime; // Seek直後は補間せず即スナップ
            nextBeat = songTime;
        }

        /// <summary>
        /// 毎フレーム呼ぶ想定（GameController.Update()の先頭）。smoothed は毎フレーム必ず
        /// deltaTime分前進させ（＝見た目のなめらかさを常に保証）、dspTimeが更新された
        /// フレームだけ追加でオーディオクロック基準の値へ少しだけ寄せる（実測23〜25Hzで発生）。
        /// 呼ばなくても SongTime 自体は動くが、その場合は従来どおり dspTime の階段状になる。
        ///
        /// v1実装（クランプで追い越しを止める方式）は、DSP更新間隔(実測約40〜43ms)と
        /// クランプの許容幅(50ms)の差がわずか7〜10msしかなく、DSPコールバックの
        /// タイミングジッタでこの薄い余白をすぐ使い切って**smoothedが数フレーム凍結する**
        /// 周期的なスタッタリングを引き起こしていた（実機で確認、120fps基準で3〜4フレーム分、
        /// 秒間数回）。v2はクランプ自体を廃止し、「毎フレーム必ず前進・ズレは足し算で
        /// 補正するだけ」にすることで、smoothedが止まる経路を無くした。
        /// </summary>
        public void Advance(float unscaledDeltaTime)
        {
            if (!Running) return;
            smoothed += unscaledDeltaTime; // 毎フレーム必ず前進させる（止まる経路を作らない）

            double dsp = AudioSettings.dspTime;
            if (dsp != lastObservedDsp)
            {
                lastObservedDsp = dsp;
                double authoritative = dsp - t0;
                double drift = authoritative - smoothed;
                // 大きくズレていれば全部、小さければ一部だけ足す（引く）。どちらも加算なので
                // smoothed自体は単調増加のまま（クランプのように値を据え置く経路が無い）。
                smoothed += Math.Abs(drift) > SnapThreshold ? drift : drift * DriftCorrectionRate;
            }
        }

        public float SongTime => Running ? (float)smoothed : (float)pausedAt;

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
