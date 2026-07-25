import type { Layer, StageConfig } from './config';
import { LAYER_GROUND, LAYER_SKY } from './config';

export interface Contact {
  id: number;
  u: number;
  v: number;
  layer: Layer;
  cell: number;
  /** 押下開始時刻 (performance.now 基準の秒) */
  since: number;
}

export interface EnterEvent {
  layer: Layer;
  cell: number;
  /** 新規接触 (pointerdown) か、移動によるセル更新か */
  fresh: boolean;
  /** イベント発生時刻（秒、audio clock と同じ基準に変換済み） */
  at: number;
}

/**
 * 入力モデル（設計メモ「入力モデル」節そのまま）:
 *
 *   layer = (v > y_split) ? 1 : 0
 *   cell  = clamp( floor( (u + U) * cells / (2U) ), 0, cells-1 )
 *
 * - 層の境界は1本のみ。不感帯は置かない（層間遷移する追従型判定を切らないため）。
 * - ただし押下中のポインタにはヒステリシスを入れる。
 * - セル境界は垂直（消失点へ収束させない）。収束させると cell がタッチの y にも依存し、
 *   指の縦移動でホールドが落ちる。
 */
export class InputManager {
  contacts = new Map<number, Contact>();
  /** 現在占有されているセル。キーは layer * cells + cell */
  occupied = new Set<number>();
  onEnter: ((e: EnterEvent) => void) | null = null;
  /** 直近の「新規接触」表示用（オーバーレイのフラッシュ） */
  ripples: Array<{ layer: Layer; cell: number; born: number }> = [];

  private cfg: StageConfig;
  private el: HTMLElement;
  private nowSec: () => number;

  constructor(el: HTMLElement, cfg: StageConfig, nowSec: () => number) {
    this.el = el;
    this.cfg = cfg;
    this.nowSec = nowSec;

    el.addEventListener('pointerdown', this.onDown, { passive: false });
    el.addEventListener('pointermove', this.onMove, { passive: false });
    el.addEventListener('pointerup', this.onUp, { passive: false });
    el.addEventListener('pointercancel', this.onUp, { passive: false });
    el.addEventListener('pointerleave', this.onUp, { passive: false });
    el.addEventListener('contextmenu', (e) => e.preventDefault());
  }

  setConfig(cfg: StageConfig): void {
    this.cfg = cfg;
  }

  key(layer: Layer, cell: number): number {
    return layer * this.cfg.cells + cell;
  }

  private ndc(e: PointerEvent): { u: number; v: number } {
    const r = this.el.getBoundingClientRect();
    return {
      u: ((e.clientX - r.left) / r.width) * 2 - 1,
      v: 1 - ((e.clientY - r.top) / r.height) * 2,
    };
  }

  private cellOf(u: number): number {
    const U = this.cfg.U;
    const n = this.cfg.cells;
    return Math.max(0, Math.min(n - 1, Math.floor(((u + U) * n) / (2 * U))));
  }

  private layerOf(v: number, prev: Layer | null): Layer {
    const s = this.cfg.vSplit;
    if (prev === null) return v > s ? LAYER_SKY : LAYER_GROUND;
    // 押下中はヒステリシス。境界を h だけ越えないと層は切り替わらない
    const h = this.cfg.splitHysteresis;
    if (prev === LAYER_SKY) return v > s - h ? LAYER_SKY : LAYER_GROUND;
    return v > s + h ? LAYER_SKY : LAYER_GROUND;
  }

  private onDown = (e: PointerEvent) => {
    e.preventDefault();
    const { u, v } = this.ndc(e);
    const layer = this.layerOf(v, null);
    const cell = this.cellOf(u);
    const at = this.nowSec();
    this.contacts.set(e.pointerId, { id: e.pointerId, u, v, layer, cell, since: at });
    this.occupied.add(this.key(layer, cell));
    this.emit(layer, cell, true, at);
    (this.el as HTMLElement).setPointerCapture?.(e.pointerId);
  };

  private onMove = (e: PointerEvent) => {
    const c = this.contacts.get(e.pointerId);
    if (!c) return;
    e.preventDefault();
    const { u, v } = this.ndc(e);
    const layer = this.layerOf(v, c.layer);
    const cell = this.cellOf(u);
    c.u = u;
    c.v = v;
    if (layer !== c.layer || cell !== c.cell) {
      this.releaseCell(c.layer, c.cell, e.pointerId);
      c.layer = layer;
      c.cell = cell;
      this.occupied.add(this.key(layer, cell));
      // 「入力の更新」= 新規の接触点の検出。タップ判定はこれでも成立する
      this.emit(layer, cell, false, this.nowSec());
    }
  };

  private onUp = (e: PointerEvent) => {
    const c = this.contacts.get(e.pointerId);
    if (!c) return;
    this.contacts.delete(e.pointerId);
    this.releaseCell(c.layer, c.cell, e.pointerId);
  };

  private releaseCell(layer: Layer, cell: number, exceptId: number): void {
    for (const [id, o] of this.contacts) {
      if (id !== exceptId && o.layer === layer && o.cell === cell) return; // 他の指が占有中
    }
    this.occupied.delete(this.key(layer, cell));
  }

  private emit(layer: Layer, cell: number, fresh: boolean, at: number): void {
    this.ripples.push({ layer, cell, born: at });
    this.onEnter?.({ layer, cell, fresh, at });
  }

  isOccupied(layer: Layer, cell: number): boolean {
    return this.occupied.has(this.key(layer, cell));
  }
}
