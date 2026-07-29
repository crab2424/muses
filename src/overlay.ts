import { COLORS, LAYER_GROUND, LAYER_SKY, type Layer, type StageConfig } from './config';
import type { Derived } from './derive';
import type { InputManager } from './input';

export interface HitFlash {
  layer: Layer;
  cell: number;
  width: number;
  born: number;
  kind: 'perfect' | 'good' | 'miss';
}

/**
 * 判定帯のスクリーン空間オーバーレイ。
 *
 * 設計メモの帰結: 判定帯はワールド空間のポリゴンではなく **スクリーン空間のオーバーレイ**
 * として描画しなければならない（両層の判定帯を同じ奥行き範囲に揃えることは原理的に不可能で、
 * 帯の厚みは人間工学パラメータであり、タイミングとは無関係だから）。
 */
export class Overlay {
  flashes: HitFlash[] = [];
  private ctx: CanvasRenderingContext2D;
  private w = 0;
  private h = 0;

  constructor(private canvas: HTMLCanvasElement) {
    this.ctx = canvas.getContext('2d')!;
  }

  resize(w: number, h: number, dpr: number): void {
    this.w = w;
    this.h = h;
    this.canvas.width = Math.round(w * dpr);
    this.canvas.height = Math.round(h * dpr);
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  private px(u: number): number {
    return ((u + 1) / 2) * this.w;
  }
  private py(v: number): number {
    return ((1 - v) / 2) * this.h;
  }

  draw(cfg: StageConfig, d: Derived, input: InputManager, now: number): void {
    const c = this.ctx;
    c.clearRect(0, 0, this.w, this.h);

    // 地平線と最遠端は θ・farFrac からの導出値なので Derived から取る。
    // 地平線は θ が大きいと画面外 (v > 1) に出るので、そのときは描かない。
    if (cfg.showHorizon && d.vHorizon <= 1) {
      c.strokeStyle = 'rgba(120,150,220,0.35)';
      c.lineWidth = 1;
      c.setLineDash([6, 6]);
      this.hline(d.vHorizon);
      c.setLineDash([]);
      this.label('horizon', this.px(cfg.U) - 60, this.py(d.vHorizon) - 4, 'rgba(140,170,230,0.6)');
    }

    // 空中レイヤー
    this.band(
      cfg,
      input,
      now,
      LAYER_SKY,
      cfg.vSkyTop,
      cfg.vSkyBot,
      cfg.vSkyJudge,
      COLORS.skyCss,
      '255,62,165',
    );
    // 地上レイヤー
    this.band(
      cfg,
      input,
      now,
      LAYER_GROUND,
      cfg.vGroundTop,
      cfg.vGroundBot,
      cfg.vGroundJudge,
      COLORS.groundCss,
      '139,92,246',
    );

    if (cfg.showSplitLine) {
      c.strokeStyle = 'rgba(220,220,255,0.30)';
      c.lineWidth = 1;
      c.setLineDash([3, 7]);
      this.hline(cfg.vSplit);
      c.setLineDash([]);
      this.label('y_split', 0, this.py(cfg.vSplit) - 4, 'rgba(200,205,235,0.55)');
    }

    if (cfg.showTouchDebug) this.touches(input);

    // 判定フラッシュ
    this.flashes = this.flashes.filter((f) => now - f.born >= 0 && now - f.born < 0.45);
    for (const f of this.flashes) {
      const k = Math.min(1, Math.max(0, (now - f.born) / 0.45));
      const vJ = f.layer === LAYER_SKY ? cfg.vSkyJudge : cfg.vGroundJudge;
      const x0 = this.px(this.cellU(cfg, f.cell));
      const x1 = this.px(this.cellU(cfg, f.cell + f.width));
      const y = this.py(vJ);
      const col =
        f.kind === 'perfect' ? '255,255,255' : f.kind === 'good' ? '120,220,255' : '255,80,80';
      const r = 6 + 26 * k;
      c.fillStyle = `rgba(${col},${(1 - k) * 0.55})`;
      c.fillRect(x0 + 2, y - r / 2, x1 - x0 - 4, r);
    }
  }

  private cellU(cfg: StageConfig, cellIdx: number): number {
    return -cfg.U + (2 * cfg.U * cellIdx) / cfg.cells;
  }

  private band(
    cfg: StageConfig,
    input: InputManager,
    now: number,
    layer: Layer,
    vTop: number,
    vBot: number,
    vJudge: number,
    css: string,
    rgb: string,
  ): void {
    const c = this.ctx;
    const yT = this.py(vTop);
    const yB = this.py(vBot);
    const yJ = this.py(vJudge);
    const xL = this.px(-cfg.U);
    const xR = this.px(cfg.U);

    if (cfg.showBand) {
      // 帯の背景
      const g = c.createLinearGradient(0, yT, 0, yB);
      const stop = Math.min(1, Math.max(0, (yJ - yT) / (yB - yT || 1)));
      g.addColorStop(0, `rgba(${rgb},0.02)`);
      g.addColorStop(stop, `rgba(${rgb},0.13)`);
      g.addColorStop(1, `rgba(${rgb},0.02)`);
      c.fillStyle = g;
      c.fillRect(xL, yT, xR - xL, yB - yT);
    }

    // アクティブセルのハイライト（帯を消していても押した位置は出す）
    for (let k = 0; k < cfg.cells; k++) {
      if (!input.isOccupied(layer, k)) continue;
      const a = this.px(this.cellU(cfg, k));
      const b = this.px(this.cellU(cfg, k + 1));
      c.fillStyle = `rgba(${rgb},0.30)`;
      c.fillRect(a, yT, b - a, yB - yT);
    }

    if (cfg.showBand) {
      // セル境界（垂直。消失点へは収束させない）
      c.strokeStyle = `rgba(${rgb},0.38)`;
      c.lineWidth = 1;
      c.beginPath();
      for (let k = 0; k <= cfg.cells; k++) {
        const x = this.px(this.cellU(cfg, k));
        c.moveTo(x, yT);
        c.lineTo(x, yB);
      }
      c.stroke();

      // 帯の上下端
      c.strokeStyle = `rgba(${rgb},0.5)`;
      c.beginPath();
      c.moveTo(xL, yT);
      c.lineTo(xR, yT);
      c.moveTo(xL, yB);
      c.lineTo(xR, yB);
      c.stroke();
    }

    if (cfg.showJudgeLine) {
      c.strokeStyle = css;
      c.lineWidth = 2.5;
      c.shadowColor = css;
      c.shadowBlur = 12;
      c.beginPath();
      c.moveTo(xL, yJ);
      c.lineTo(xR, yJ);
      c.stroke();
      c.shadowBlur = 0;
    }

    // セル番号（画面外にはみ出さないようクランプする）
    if (cfg.showCellIndex) {
      c.fillStyle = `rgba(${rgb},0.75)`;
      c.font = '10px ui-monospace, Menlo, monospace';
      c.textAlign = 'center';
      const y = Math.min(this.h - 3, Math.max(10, yB - 4));
      for (let k = 0; k < cfg.cells; k++) {
        const x = (this.px(this.cellU(cfg, k)) + this.px(this.cellU(cfg, k + 1))) / 2;
        c.fillText(String(k), x, y);
      }
      c.textAlign = 'left';
    }

    // 新規接触のリップル
    for (const r of input.ripples) {
      if (r.layer !== layer) continue;
      const k = (now - r.born) / 0.3;
      if (k < 0 || k > 1) continue;
      const x0 = this.px(this.cellU(cfg, r.cell));
      const x1 = this.px(this.cellU(cfg, r.cell + 1));
      c.strokeStyle = `rgba(255,255,255,${(1 - k) * 0.8})`;
      c.lineWidth = 2;
      const inset = k * 6;
      c.strokeRect(x0 + inset, yT + inset, x1 - x0 - inset * 2, yB - yT - inset * 2);
    }
    input.ripples = input.ripples.filter((r) => now - r.born >= 0 && now - r.born < 0.3);
  }

  private touches(input: InputManager): void {
    const c = this.ctx;
    for (const t of input.contacts.values()) {
      const x = this.px(t.u);
      const y = this.py(t.v);
      c.strokeStyle = t.layer === LAYER_SKY ? COLORS.skyCss : COLORS.groundCss;
      c.lineWidth = 2;
      c.beginPath();
      c.arc(x, y, 26, 0, Math.PI * 2);
      c.stroke();
      c.fillStyle = 'rgba(255,255,255,0.8)';
      c.font = '10px ui-monospace, Menlo, monospace';
      c.fillText(`L${t.layer} C${t.cell}`, x + 30, y + 4);
    }
  }

  private hline(v: number): void {
    const y = this.py(v);
    this.ctx.beginPath();
    this.ctx.moveTo(0, y);
    this.ctx.lineTo(this.w, y);
    this.ctx.stroke();
  }

  private label(text: string, x: number, y: number, color: string): void {
    this.ctx.fillStyle = color;
    this.ctx.font = '10px ui-monospace, Menlo, monospace';
    this.ctx.fillText(text, x + 4, Math.max(10, y));
  }
}
