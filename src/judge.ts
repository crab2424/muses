import { LAYER_GROUND, LAYER_SKY, type Layer, type StageConfig } from './config';
import { arcAt, noteEnd, noteStart } from './chart';
import type { NoteField, NoteRuntime } from './notes';
import type { EnterEvent, InputManager } from './input';
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
 *
 * 走査コスト: 譜面は開始時刻順にソートされているので、毎フレーム全ノーツを見ずに
 * 「まだ解決していない最初のノーツ」から「開始時刻が現在時刻を超えるノーツ」までの
 * 数個〜数十個だけを見る。譜面の長さに依らず O(同時進行中のノーツ数)。
 */
export class Judge {
  score: Score = blankScore();
  /** これより前の runtimes はすべて hit / missed で確定済み */
  private cursor = 0;

  constructor(
    private cfg: StageConfig,
    private nf: NoteField,
    private overlay: Overlay,
  ) {}

  setConfig(cfg: StageConfig): void {
    this.cfg = cfg;
  }

  reset(): void {
    this.score = blankScore();
    this.cursor = 0;
  }

  /** ノーツが占有するセル範囲 [lo, hi]（両端含む） */
  private cellRange(rt: NoteRuntime, songTime: number): { layer: Layer; lo: number; hi: number } {
    const n = rt.note;
    const max = this.cfg.cells - 1;
    if (n.kind === 'arc') {
      const p = arcAt(n, songTime);
      const lo = Math.floor(p.cellF - n.width / 2);
      const hi = Math.ceil(p.cellF + n.width / 2) - 1;
      return {
        layer: p.layerF > 0.5 ? LAYER_SKY : LAYER_GROUND,
        lo: Math.max(0, Math.min(max, lo)),
        hi: Math.max(0, Math.min(max, Math.max(lo, hi))),
      };
    }
    return {
      layer: n.layer,
      lo: Math.max(0, Math.min(max, n.cell)),
      hi: Math.max(0, Math.min(max, n.cell + n.width - 1)),
    };
  }

  private anyOccupied(input: InputManager, layer: Layer, lo: number, hi: number): boolean {
    for (let k = lo; k <= hi; k++) if (input.isOccupied(layer, k)) return true;
    return false;
  }

  /** 「入力範囲内に新規の接触点が検出された」= ヒット判定のトリガ */
  onEnter(e: EnterEvent, songTime: number): void {
    const win = this.cfg.windowGood / 1000;
    const rts = this.nf.runtimes;
    let best: NoteRuntime | null = null;
    let bestDt = Infinity;

    for (let i = this.cursor; i < rts.length; i++) {
      const rt = rts[i];
      const n = rt.note;
      if (noteStart(n) > songTime + win) break; // これ以降は窓の外
      if (rt.state !== 'pending' || n.kind === 'arc') continue; // アークは接触トリガではなく追従型
      if (n.layer !== e.layer) continue;
      if (e.cell < n.cell || e.cell >= n.cell + n.width) continue;
      const dt = n.time - songTime;
      if (Math.abs(dt) > win) continue;
      if (Math.abs(dt) < Math.abs(bestDt)) {
        best = rt;
        bestDt = dt;
      }
    }

    if (!best) return;
    const n = best.note;
    if (n.kind === 'arc') return; // 型の絞り込み用（上で除外済み）

    const ms = -bestDt * 1000; // 正 = 早押し
    const kind = Math.abs(ms) <= this.cfg.windowPerfect ? 'perfect' : 'good';
    if (kind === 'perfect') this.score.perfect++;
    else this.score.good++;
    this.score.combo++;
    this.score.maxCombo = Math.max(this.score.maxCombo, this.score.combo);
    this.score.lastJudge = kind.toUpperCase();
    this.score.lastMs = ms;

    if (n.kind === 'hold') {
      best.state = 'active';
      best.lastHeld = songTime;
      this.nf.setNoteAlpha(best, 0.75);
    } else {
      best.state = 'hit';
      this.nf.setNoteAlpha(best, 0);
    }
    this.overlay.flashes.push({
      layer: n.layer,
      cell: n.cell,
      width: n.width,
      born: songTime,
      kind,
    });
  }

  /** 毎フレーム: 見逃し判定と、ホールド／アークの追従型判定 */
  update(songTime: number, input: InputManager): void {
    const win = this.cfg.windowGood / 1000;
    const rts = this.nf.runtimes;

    // 確定済みのノーツを飛ばす
    while (
      this.cursor < rts.length &&
      (rts[this.cursor].state === 'hit' || rts[this.cursor].state === 'missed')
    ) {
      this.cursor++;
    }

    for (let i = this.cursor; i < rts.length; i++) {
      const rt = rts[i];
      const n = rt.note;
      const start = noteStart(n);
      if (start > songTime) break; // 以降はまだ開始していない（開始時刻順にソート済み）
      const end = noteEnd(n);

      if (rt.state === 'pending') {
        if (n.kind === 'arc') {
          // アークは開始時刻に到達したら追従判定を開始する（接触が要らない）
          rt.state = 'active';
          rt.lastHeld = songTime;
        } else if (songTime - start > win) {
          rt.state = 'missed';
          this.nf.setNoteAlpha(rt, 0.12);
          this.score.miss++;
          this.score.combo = 0;
          this.score.lastJudge = 'MISS';
          this.overlay.flashes.push({
            layer: n.layer,
            cell: n.cell,
            width: n.width,
            born: songTime,
            kind: 'miss',
          });
        }
        continue;
      }

      if (rt.state !== 'active') continue;

      // --- 追従型判定: ノーツが現在占めているセルがアクティブになる ---
      const { layer, lo, hi } = this.cellRange(rt, songTime);
      const held = this.anyOccupied(input, layer, lo, hi);
      if (held) rt.lastHeld = songTime;

      // 保持が 0.2 秒以上外れたら失敗
      if (!held && songTime - rt.lastHeld > 0.2) {
        this.breakNote(rt);
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
          this.breakNote(rt);
        }
      }
    }
  }

  private breakNote(rt: NoteRuntime): void {
    rt.state = 'missed';
    this.nf.setNoteAlpha(rt, 0.12);
    this.score.miss++;
    this.score.combo = 0;
    this.score.lastJudge = 'HOLD BREAK';
  }
}

function blankScore(): Score {
  return { perfect: 0, good: 0, miss: 0, combo: 0, maxCombo: 0, lastJudge: '', lastMs: 0 };
}
