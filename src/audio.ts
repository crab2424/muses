/**
 * 曲時刻の供給源。AudioContext.currentTime を基準にする
 * （requestAnimationFrame の時刻を基準にすると音とズレるため）。
 *
 * outputLatency / baseLatency も表示する。iPad Safari の Web Audio 遅延は
 * プラットフォーム選定上の実測リスクとして挙がっていた項目。
 */
export class Clock {
  ctx: AudioContext | null = null;
  private t0 = 0;
  private nextBeat = 0;
  running = false;

  async start(bpm: number): Promise<void> {
    if (!this.ctx) {
      this.ctx = new (window.AudioContext ||
        (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext)({
        latencyHint: 'interactive',
      });
    }
    await this.ctx.resume();
    this.t0 = this.ctx.currentTime;
    this.nextBeat = 0;
    this.running = true;
    void bpm;
  }

  /** 曲時刻（秒） */
  get songTime(): number {
    if (!this.ctx || !this.running) return 0;
    return this.ctx.currentTime - this.t0;
  }

  get outputLatencyMs(): number {
    const c = this.ctx as (AudioContext & { outputLatency?: number }) | null;
    if (!c) return 0;
    return ((c.outputLatency ?? c.baseLatency ?? 0) as number) * 1000;
  }

  /** メトロノーム。0.3 秒先まで先読みスケジュールする */
  tickMetronome(bpm: number, enabled: boolean): void {
    if (!this.ctx || !this.running) return;
    const b = 60 / bpm;
    const ahead = this.songTime + 0.3;
    while (this.nextBeat < ahead) {
      if (enabled && this.nextBeat >= 0) {
        this.click(this.t0 + this.nextBeat, this.nextBeat % (b * 4) < b * 0.5);
      }
      this.nextBeat += b;
    }
  }

  private click(when: number, accent: boolean): void {
    const ctx = this.ctx!;
    const osc = ctx.createOscillator();
    const g = ctx.createGain();
    osc.frequency.value = accent ? 1600 : 1000;
    g.gain.setValueAtTime(0.0001, when);
    g.gain.exponentialRampToValueAtTime(accent ? 0.25 : 0.12, when + 0.002);
    g.gain.exponentialRampToValueAtTime(0.0001, when + 0.05);
    osc.connect(g).connect(ctx.destination);
    osc.start(when);
    osc.stop(when + 0.06);
  }
}
