import { LAYER_SKY, type Layer, type StageConfig } from './config';
import { arcAt, noteEnd, noteStart } from './chart';
import type { NoteField, NoteRuntime } from './notes';
import type { EnterEvent } from './input';
import type { InputManager } from './input';
import type { Overlay } from './overlay';

export interface Score {
  perfect: number;
  good: number;
  miss: number;
  combo: number;
  maxCombo: number;
  lastJudge: string;
  lastMs: number;
}

/**
 * 判定。※判定ウィンドウ・スコアリングは未設計項目のため暫定実装。
 * 検証したいのは主に「新規接触点の検出でヒットする」入力方式と、
 * アークの追従型判定（層間遷移中、ノーツが占めているセルが逐次アクティブになる）。
 */
export class Judge {
  score: Score = {
    perfect: 0,
    good: 0,
    miss: 0,
    combo: 0,
    maxCombo: 0,
    lastJudge: '',
    lastMs: 0,
  };

  constructor(
    private cfg: StageConfig,
    private nf: NoteField,
    private overlay: Overlay,
  ) {}

  setConfig(cfg: StageConfig): void {
    this.cfg = cfg;
  }

  reset(): void {
    this.score = {
      perfect: 0,
      good: 0,
      miss: 0,
      combo: 0,
      maxCombo: 0,
      lastJudge: '',
      lastMs: 0,
    };
  }

  /** 「入力範囲内に新規の接触点が検出された」= ヒット判定のトリガ */
  onEnter(e: EnterEvent, songTime: number): void {
    const win = this.cfg.windowGood / 1000;
    let best: NoteRuntime | null = null;
    let bestDt = Infinity;

    for (const rt of this.nf.runtimes) {
      if (rt.state !== 'pending') continue;
      const n = rt.note;
      if (n.kind === 'arc') continue; // アークは接触トリガではなく追従型
      const layer: Layer = n.layer;
      if (layer !== e.layer || n.cell !== e.cell) continue;
      const dt = n.time - songTime;
      if (Math.abs(dt) > win) continue;
      if (Math.abs(dt) < Math.abs(bestDt)) {
        best = rt;
        bestDt = dt;
      }
    }

    if (!best) return;
    const ms = -bestDt * 1000; // 正 = 早押し
    const kind = Math.abs(ms) <= this.cfg.windowPerfect ? 'perfect' : 'good';
    if (kind === 'perfect') this.score.perfect++;
    else this.score.good++;
    this.score.combo++;
    this.score.maxCombo = Math.max(this.score.maxCombo, this.score.combo);
    this.score.lastJudge = kind.toUpperCase();
    this.score.lastMs = ms;

    if (best.note.kind === 'hold') {
      best.state = 'active';
      best.lastHeld = songTime;
      this.nf.setNoteAlpha(best, 0.75);
    } else {
      best.state = 'hit';
      this.nf.setNoteAlpha(best, 0);
    }
    this.overlay.flashes.push({ layer: e.layer, cell: e.cell, born: songTime, kind });
  }

  /** 毎フレーム: 見逃し判定と、ホールド／アークの追従型判定 */
  update(songTime: number, input: InputManager): void {
    const win = this.cfg.windowGood / 1000;

    for (const rt of this.nf.runtimes) {
      const n = rt.note;
      const start = noteStart(n);
      const end = noteEnd(n);

      if (rt.state === 'pending') {
        if (n.kind === 'arc') {
          // アークは開始時刻に到達したら追従判定を開始する（接触が要らない）
          if (songTime >= start) {
            rt.state = 'active';
            rt.lastHeld = songTime;
          }
        } else if (songTime - start > win) {
          rt.state = 'missed';
          this.nf.setNoteAlpha(rt, 0.12);
          this.score.miss++;
          this.score.combo = 0;
          this.score.lastJudge = 'MISS';
          this.overlay.flashes.push({
            layer: n.layer,
            cell: n.cell,
            born: songTime,
            kind: 'miss',
          });
        }
        continue;
      }

      if (rt.state !== 'active') continue;

      // --- 追従型判定 ---
      let layer: Layer;
      let cell: number;
      if (n.kind === 'arc') {
        const p = arcAt(n, songTime);
        layer = p.layerF > 0.5 ? LAYER_SKY : 0;
        cell = Math.max(0, Math.min(this.cfg.cells - 1, Math.floor(p.cellF)));
      } else if (n.kind === 'hold') {
        layer = n.layer;
        cell = n.cell;
      } else {
        rt.state = 'hit';
        continue;
      }

      const held = input.isOccupied(layer, cell);
      if (held) rt.lastHeld = songTime;
      rt.judgeMs = held ? 0 : (songTime - rt.lastHeld) * 1000;

      // 保持が 0.2 秒以上外れたら失敗
      if (!held && songTime - rt.lastHeld > 0.2) {
        rt.state = 'missed';
        this.nf.setNoteAlpha(rt, 0.12);
        this.score.miss++;
        this.score.combo = 0;
        this.score.lastJudge = 'HOLD BREAK';
        continue;
      }

      this.nf.setNoteAlpha(rt, held ? 1 : 0.45);

      if (songTime > end) {
        // ロングノーツの終点は離す必要なし → 終端付近まで保持できていれば成立。
        // フレーム落ちで判定が丸ごと飛んだ場合に誤って成立しないよう、
        // 「最後に保持できていた時刻」が終端に十分近いことを条件にする。
        if (end - rt.lastHeld <= 0.2) {
          rt.state = 'hit';
          this.nf.setNoteAlpha(rt, 0);
          this.score.perfect++;
          this.score.combo++;
          this.score.maxCombo = Math.max(this.score.maxCombo, this.score.combo);
        } else {
          rt.state = 'missed';
          this.nf.setNoteAlpha(rt, 0.12);
          this.score.miss++;
          this.score.combo = 0;
          this.score.lastJudge = 'HOLD BREAK';
        }
      }
    }
  }
}
