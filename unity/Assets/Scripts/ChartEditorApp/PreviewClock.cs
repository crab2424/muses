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
    ///
    /// editor-ui-rework-r4.md §12: 内部状態(pausedAt/source.time/dspアンカー)は常に「音源上の
    /// 再生位置(audioTime)」を表す。外部に公開する <see cref="SongTime"/>（＝譜面tick0を0とする
    /// 譜面時間）とは <c>audioTime = songTime + Offset</c> の関係で変換する。呼び出し側
    /// (PreviewSystem/ChartEditorApp)はすべて譜面時間でやり取りしているため、この層だけで
    /// オフセットを吸収すれば呼び出し側の変更は不要になる。
    ///
    /// editor-ui-rework-r8.md §1: <c>audioTime</c> は <see cref="AudioSource.time"/> では
    /// 表現できない範囲（負＝前奏区間の途中、clip.length以上＝末尾区間）も取りうる。
    /// この範囲では dspTime 基準の仮想クロック（<see cref="anchorDsp"/>/<see cref="anchorAudio"/>）を
    /// 真の値として使い、音源が実際に鳴っている範囲（0 ≤ audioTime &lt; clip.length）だけ
    /// <see cref="AudioSource.time"/> を真の値として使う。
    /// </summary>
    public class PreviewClock
    {
        private readonly AudioSource source;
        private double pausedAt;
        public bool Running { get; private set; }
        public float Rate { get; private set; } = 1f;

        /// <summary>editor-ui-rework-r7.md §2.4b。AudioSource.Play()は「次のミックスブロック境界」
        /// まで待つため、48kHz/バッファ256サンプルでも最大5.3msの開始遅延ゆらぎが乗る。
        /// PlayScheduledでdspTime基準のサンプル精度の開始時刻を指定し、これを消す。</summary>
        private const double ScheduleLeadSec = 0.05;

        /// <summary>音源先頭 → 譜面tick0のズレ(秒)。SongMeta.offsetSecをそのまま渡す想定。
        /// Offset&gt;0 なら譜面tick0は音源のOffset秒地点（＝音源の先頭に前奏がある場合の値）。</summary>
        public float Offset { get; set; }

        /// <summary>r8 §1: Running中、「dspTime=anchorDspのときaudioTime=anchorAudioだった」という
        /// アンカー対。前奏区間・末尾区間の仮想クロック(<see cref="VirtualAudioTime"/>)の基準になる。</summary>
        private double anchorDsp;
        private double anchorAudio;

        public PreviewClock(AudioSource source)
        {
            this.source = source;
        }

        private bool HasClip => source != null && source.clip != null;

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>アンカーから現在のdspTimeまでの経過時間をRate倍して外挿した音源上の位置。
        /// 音源が実際に鳴っていない区間（前奏の途中・末尾より後）ではこれが真の値になる。</summary>
        private double VirtualAudioTime => anchorAudio + (AudioSettings.dspTime - anchorDsp) * Rate;

        /// <summary>音源上の現在位置(秒)。負（前奏区間の途中）・clip.length以上（末尾区間）もありうる。</summary>
        private double AudioTimeD
        {
            get
            {
                if (!Running) return pausedAt;
                if (!HasClip) return VirtualAudioTime;
                double clipLen = source.clip.length;
                double virt = VirtualAudioTime;
                // 音源が実際に再生できる範囲の外では、AudioSource.time は意味を持たない
                // （負にはならず0にクランプされる／末尾を過ぎると停止して0に戻る等）ため仮想クロックを使う。
                if (virt < 0.0 || virt >= clipLen) return virt;
                return source.time;
            }
        }

        private float AudioTime => (float)AudioTimeD;

        public float SongTime => AudioTime - Offset;

        public void Play()
        {
            if (Running) return;
            double a0 = pausedAt;
            if (HasClip)
            {
                double clipLen = source.clip.length;
                anchorDsp = AudioSettings.dspTime + ScheduleLeadSec;
                anchorAudio = a0;
                if (a0 < clipLen)
                {
                    // r8 §1.3: a0が負(前奏区間の途中)でも、音源自体はaudioTime=0地点から鳴らし、
                    // 開始をその分だけ未来のdspTimeへ予約するだけで正確に前奏を表現できる。
                    double startAudio = Clamp(a0, 0.0, Mathf.Max(0f, (float)clipLen - 0.001f));
                    source.time = (float)startAudio;
                    source.pitch = Rate;
                    double preRoll = a0 < 0.0 ? -a0 : 0.0;
                    double startDsp = anchorDsp + preRoll / Mathf.Max(0.0001f, Rate);
                    source.PlayScheduled(startDsp);
                }
                // a0 >= clipLen（末尾区間）は鳴らす音が無い。時刻はanchorから仮想クロックのみで進む。
            }
            else
            {
                anchorDsp = AudioSettings.dspTime;
                anchorAudio = a0;
            }
            Running = true;
        }

        public void Pause()
        {
            if (!Running) return;
            pausedAt = AudioTimeD;
            // 予約済みでまだ鳴り始めていない場合を含め、次のPlay()で必ず予約し直すのでStop()で統一する
            // （AudioSource.Pause()は「予約中でまだ再生開始していない」状態の挙動が不定なため避ける）。
            if (HasClip) source.Stop();
            Running = false;
        }

        public void TogglePlay()
        {
            if (Running) Pause(); else Play();
        }

        /// <summary>譜面時間(songTime)でシークする。内部ではOffsetを足した音源上の位置(audioTime)へ
        /// 変換する。r8 §1: audioTimeが負（負のOffsetで譜面tick0が音源より前にある場合）もそのまま
        /// 保持し、0にクランプしない（以前はここでクランプしており、譜面の先頭区間に到達できない
        /// 不具合の原因になっていた）。</summary>
        public void Seek(float songTime)
        {
            songTime = Mathf.Max(0f, songTime);
            double audioTime = songTime + Offset;
            if (Running)
            {
                anchorAudio = audioTime;
                anchorDsp = AudioSettings.dspTime;
                if (HasClip)
                {
                    double clipLen = source.clip.length;
                    source.Stop();
                    if (audioTime < clipLen)
                    {
                        double startAudio = Clamp(audioTime, 0.0, Mathf.Max(0f, (float)clipLen - 0.001f));
                        source.time = (float)startAudio;
                        source.pitch = Rate;
                        double preRoll = audioTime < 0.0 ? -audioTime : 0.0;
                        double startDsp = anchorDsp + preRoll / Mathf.Max(0.0001f, Rate);
                        source.PlayScheduled(startDsp);
                    }
                    // else 末尾区間: 鳴らす音が無いので停止したまま仮想クロックだけ進む
                }
            }
            else
            {
                pausedAt = audioTime;
                if (HasClip)
                {
                    // 停止中のAudioSource.timeは機能的には使われない(AudioTimeDは!Running時pausedAtを
                    // 直接返す)が、範囲内なら見た目上も合わせておく(ベストエフォート)。
                    double clipLen = source.clip.length;
                    double clamped = Clamp(audioTime, 0.0, Mathf.Max(0f, (float)clipLen - 0.001f));
                    source.time = (float)clamped;
                }
            }
        }

        /// <summary>0.25x〜2.0xの再生速度。ピッチは保持しない(そのまま変わる、editor-spec.md §5.1で許容済み)。
        /// r8 §1: 前奏区間・末尾区間で速度を変えた場合はアンカーを組み直し、前奏区間なら開始予約もやり直す。</summary>
        public void SetRate(float rateIn)
        {
            float newRate = Mathf.Clamp(rateIn, 0.25f, 2f);
            if (Mathf.Approximately(Rate, newRate)) return;
            double cur = AudioTimeD;
            Rate = newRate;
            if (!Running)
            {
                pausedAt = cur;
                return;
            }
            anchorAudio = cur;
            anchorDsp = AudioSettings.dspTime;
            if (!HasClip) return;

            double clipLen = source.clip.length;
            source.pitch = newRate;
            if (cur < 0.0)
            {
                // 前奏区間の途中でレートが変わった: 残り前奏の長さが変わるので予約を組み直す。
                source.Stop();
                source.time = 0f;
                double startDsp = anchorDsp + (-cur) / Mathf.Max(0.0001f, newRate);
                source.PlayScheduled(startDsp);
            }
            else if (cur >= clipLen)
            {
                // 末尾区間: 鳴らす音は無い。念のため停止状態を保証する。
                source.Stop();
            }
            // 0 <= cur < clipLen: 再生中のAudioSourceのpitchを変えるだけで正しく追従する。
        }

        public void Stop()
        {
            Running = false;
            pausedAt = 0;
            if (HasClip) source.Stop();
        }
    }
}
