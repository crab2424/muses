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
    ///
    /// 楽曲再生について（song-play-flow-r1.md §4.2）: AudioSource.time は読まない
    /// （PreviewClockが正として使っているが、あちらもdspTimeと同じDSPバッファ単位でしか
    /// 更新されずiPadでは同じ問題が起きる）。dspTime を唯一の正としたまま、音源は
    /// PlayScheduled で dspTime 基準の時刻へ「貼り付ける」だけにする。これにより
    /// 「音が実際に鳴り始める瞬間」と「songTimeがその値になる瞬間」が dspTime 上で
    /// 厳密に一致し、上記のフレーム補間はそのまま生きる。
    /// </summary>
    public class SongClock
    {
        private double t0;
        private double nextBeat;
        /// <summary>Pause() 時点の SongTime を保持する（Running=false の間、SongTime はここで凍結する）</summary>
        private double pausedAt;
        public bool Running { get; private set; }

        private readonly AudioSource metronomeSource;
        private readonly AudioClip accentClip;
        private readonly AudioClip normalClip;

        // ---- 楽曲再生 ----
        private AudioSource musicSource;
        private AudioClip musicClip;
        /// <summary>音源先頭 → 譜面tick0のズレ(秒)。SongMeta.offsetSecをそのまま渡す想定。</summary>
        public float Offset { get; private set; }

        /// <summary>AudioSource.Play()は「次のミックスブロック境界」まで待つため、
        /// 48kHz/バッファ256サンプルでも最大5.3msの開始遅延ゆらぎが乗る。PlayScheduledで
        /// dspTime基準のサンプル精度の開始時刻を指定し、これを消す（PreviewClockと同値）。
        /// Seek中の再スケジュールにはこのリードは使わない（PreviewClock.Seekと同じ判断）。</summary>
        private const double ScheduleLeadSec = 0.05;

        // ---- フレーム補間用の状態 ----
        private double lastObservedDsp = -1;
        private double smoothed;
        /// <summary>この秒数を超えてdspTime基準の値とズレたら、補間せず即座にスナップする
        /// （Seek直後や大きなハング直後の想定）。</summary>
        private const double SnapThreshold = 0.05;
        /// <summary>毎フレーム、ズレのこの割合だけ縮める（音ズレを一気にではなく滑らかに吸収する）。</summary>
        private const double DriftCorrectionRate = 0.10;

        public SongClock(AudioSource metronomeSource)
        {
            this.metronomeSource = metronomeSource;
            accentClip = BuildClickClip(1600f);
            normalClip = BuildClickClip(1000f);
        }

        /// <summary>song-play-flow-r1.md §3.2/§4.2。曲の読み込み完了時に呼ぶ。
        /// clip が null なら「音源無しの無音クロック」に戻る（従来どおりの挙動）。</summary>
        public void SetMusic(AudioSource source, AudioClip clip, float offsetSec)
        {
            musicSource = source;
            musicClip = clip;
            Offset = offsetSec;
        }

        public void Start()
        {
            nextBeat = 0;
            pausedAt = 0;
            Running = true;

            double startDsp = AudioSettings.dspTime + ScheduleLeadSec;
            t0 = startDsp;
            lastObservedDsp = AudioSettings.dspTime;
            // Start()呼び出し直後は dspTime < startDsp（リード分だけ未来）なので、
            // 真の値(=dspTime-t0)はわずかに負になる。0へスナップせず真の値を直接採用することで、
            // Advance()の補正を待たずに最初から正確にする。
            smoothed = lastObservedDsp - t0;

            ScheduleMusicAt(0.0, startDsp);
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
            // 予約済みでまだ鳴り始めていない場合を含め、次のResume/Seekで必ず予約し直すので
            // Stop()で統一する（AudioSource.Pause()は「予約中でまだ再生開始していない」状態の
            // 挙動が不定なため避ける、PreviewClockと同じ判断）。
            musicSource?.Stop();
        }

        /// <summary>Pause() で止めた地点から再開する。</summary>
        public void Resume()
        {
            if (Running) return;
            Running = true;

            double startDsp = AudioSettings.dspTime + ScheduleLeadSec;
            t0 = startDsp - pausedAt;
            lastObservedDsp = AudioSettings.dspTime;
            smoothed = lastObservedDsp - t0;

            ScheduleMusicAt(pausedAt, startDsp);
        }

        /// <summary>完全に停止する（曲を終える・タイトルへ戻る等）。Start()で再度頭から始められる。</summary>
        public void Stop()
        {
            Running = false;
            pausedAt = 0;
            musicSource?.Stop();
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
                // Start/Resumeと違いここではリードを使わない（PreviewClock.Seekと同じ判断:
                // 既に鳴っている状態からの差し替えなので、境界待ちのゆらぎより即時性を優先する）。
                double dspAnchor = AudioSettings.dspTime;
                t0 = dspAnchor - songTime;
                lastObservedDsp = dspAnchor;
                ScheduleMusicAt(songTime, dspAnchor);
            }
            else
            {
                pausedAt = songTime;
            }
            smoothed = songTime; // Seek直後は補間せず即スナップ
            nextBeat = songTime;
        }

        /// <summary>指定した songTime に音源上の対応位置が鳴るよう、dspAnchor 時刻へ予約する。
        /// audioTime(=songTime+Offset) が負（前奏区間の途中）なら、音源自体はaudioTime=0地点から
        /// 鳴らし開始をその分だけ未来のdspTimeへ予約するだけで正確に表現できる
        /// （PreviewClock r8 §1.3と同じ考え方）。clipの範囲外（末尾より後）は鳴らす音が無い。</summary>
        private void ScheduleMusicAt(double songTime, double dspAnchor)
        {
            if (musicSource == null || musicClip == null) return;
            musicSource.Stop();

            double audioTime = songTime + Offset;
            double clipLen = musicClip.length;
            if (audioTime >= clipLen) return; // 末尾区間: 鳴らす音が無い

            double startAudio = Math.Max(0.0, Math.Min(audioTime, clipLen - 0.001));
            musicSource.time = (float)startAudio;
            double preRoll = audioTime < 0.0 ? -audioTime : 0.0;
            musicSource.PlayScheduled(dspAnchor + preRoll);
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
            if (!Running || metronomeSource == null) return;
            float b = 60f / bpm;
            float ahead = SongTime + 0.05f;
            while (nextBeat < ahead)
            {
                if (enabled && nextBeat >= 0)
                {
                    bool accent = nextBeat % (b * 4f) < b * 0.5f;
                    metronomeSource.PlayOneShot(accent ? accentClip : normalClip, accent ? 0.5f : 0.25f);
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
