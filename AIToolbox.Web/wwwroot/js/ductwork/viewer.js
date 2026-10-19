// The sheet viewer: a PDF page drawn with pdf.js, the items found on it drawn over it, and pan, zoom, selection and a few
// drawing tools (place an item, measure a known length, mark a region). It knows nothing about jobs or the API: the screen
// that owns it feeds it items and listens for what the person does.

import { h } from "./util.js";
import * as geo from "./geometry.js";

const COLORS = { proposed: "#d97706", verified: "#15803d", rejected: "#9ca3af", manual: "#2563eb" };
const MAX_BITMAP = 8192;                    // pixels on a side, so a huge sheet at high zoom cannot exhaust memory

export class SheetViewer {
  /**
   * @param host the element to fill
   * @param opts { statusOf(item) -> "proposed"|..., onSelect(item|null), onTool(event), onHover(item|null) }
   */
  constructor(host, opts = {}) {
    this.host = host;
    this.opts = opts;
    this.page = null;                       // pdf.js page
    this.pageW = 0;
    this.pageH = 0;
    this.view = { scale: 1, x: 0, y: 0 };
    this.items = [];                        // [{ id, box, status, dashed, label }]
    this.regions = [];                      // [{ kind, polygon }]
    this.views = [];                        // [{ label, polygon }] the views on the sheet that have an area of their own
    this.showViews = true;
    this.selectedId = null;
    this.tool = "select";                   // select | add | calibrate | region
    this.draft = null;                      // what the current tool has so far: points, or a rubber band
    this._renderTask = null;
    this._renderTimer = null;
    this._bitmapScale = 0;
    this._disposed = false;

    this.pageCanvas = h("canvas", { class: "dw-page" });
    this.overlay = h("canvas", { class: "dw-overlay-canvas" });
    this.tip = h("div", { class: "dw-viewer-tip", hidden: true });
    this.empty = h("div", { class: "dw-viewer-empty" }, "Choose a sheet to look at.");
    this.viewport = h("div", { class: "dw-viewport", tabindex: 0, "aria-label": "Sheet viewer" }, this.pageCanvas, this.overlay, this.tip, this.empty);
    host.append(this.viewport);

    this._bind();
    this._resizeObserver = new ResizeObserver(() => this._resized());
    this._resizeObserver.observe(this.viewport);
  }

  dispose() {
    this._disposed = true;
    this._resizeObserver.disconnect();
    this._renderTask?.cancel();
    clearTimeout(this._renderTimer);
    this.page?.cleanup();
  }

  get width() { return this.viewport.clientWidth; }
  get height() { return this.viewport.clientHeight; }

  // ------------------------------------------------------------------ the page

  /** Shows a pdf.js page. The sheet's own size is trusted over the page's so boxes line up with what the server stored. */
  async showPage(page, { keepView = false } = {}) {
    this._renderTask?.cancel();
    this.page = page;
    const vp = page.getViewport({ scale: 1 });
    this.pageW = vp.width;
    this.pageH = vp.height;
    this.empty.hidden = true;
    if (!keepView || !this._hadView) { this.fit(); this._hadFitted = this.width > 0; }
    this._hadView = true;
    this._bitmapScale = 0;
    this._schedulePageRender(0);
    this.draw();
  }

  clearPage() {
    this._renderTask?.cancel();
    this.page = null;
    this.empty.hidden = false;
    this.pageCanvas.width = 0;
    this.draw();
  }

  setItems(items) { this.items = items; this.draw(); }
  setRegions(regions) { this.regions = regions; this.draw(); }

  /** The views found on or set up for the sheet. Only a view with its own area is drawn; one that is the whole page needs no outline. */
  setViews(views, show = this.showViews) { this.views = views.filter((v) => Array.isArray(v.polygon) && v.polygon.length >= 3); this.showViews = show; this.draw(); }

  /** Brings a view's area to fill the viewer. */
  focusPolygon(polygon) {
    const xs = polygon.map((p) => p[0]);
    const ys = polygon.map((p) => p[1]);
    this.view = geo.focusBox(this.view, [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)], this.width, this.height);
    this._viewChanged();
  }

  select(id, { focus = false } = {}) {
    this.selectedId = id;
    if (focus && id) {
      const item = this.items.find((i) => i.id === id);
      if (item?.box) this.view = geo.focusBox(this.view, item.box, this.width, this.height);
      this._viewChanged();
    } else {
      this.draw();
    }
  }

  fit() {
    this.view = geo.fitView(this.pageW, this.pageH, this.width, this.height);
    this._viewChanged();
  }

  zoomBy(factor) {
    this.view = geo.zoomAt(this.view, factor, this.width / 2, this.height / 2);
    this._viewChanged();
  }

  setTool(tool) {
    this.tool = tool;
    this.draft = null;
    this.viewport.classList.toggle("dw-tool-active", tool !== "select");
    this.draw();
  }

  // ------------------------------------------------------------------ drawing

  _viewChanged() {
    this._applyBitmapTransform();
    this.draw();
    this._schedulePageRender(180);
  }

  _applyBitmapTransform() {
    if (!this._bitmapScale) return;
    const k = this.view.scale / this._bitmapScale;
    this.pageCanvas.style.transform = `translate(${this.view.x}px, ${this.view.y}px) scale(${k})`;
  }

  _schedulePageRender(delay) {
    clearTimeout(this._renderTimer);
    this._renderTimer = setTimeout(() => this._renderPage(), delay);
  }

  async _renderPage() {
    if (!this.page || this._disposed) return;
    const dpr = window.devicePixelRatio || 1;
    // Draw the bitmap at the zoom the person is looking at, but no larger than is safe.
    let scale = this.view.scale * dpr;
    scale = Math.min(scale, MAX_BITMAP / Math.max(this.pageW, this.pageH));
    if (Math.abs(scale - this._bitmapScale) / Math.max(scale, 0.001) < 0.02 && this._bitmapScale) return;

    const viewport = this.page.getViewport({ scale });
    const canvas = document.createElement("canvas");
    canvas.width = Math.ceil(viewport.width);
    canvas.height = Math.ceil(viewport.height);
    this._renderTask?.cancel();
    const task = this.page.render({ canvasContext: canvas.getContext("2d"), viewport });
    this._renderTask = task;
    try {
      await task.promise;
    } catch (e) {
      if (e?.name === "RenderingCancelledException") return;
      throw e;
    }

    if (this._disposed) return;
    // Swap the finished bitmap in at once, so the page never goes blank while it re-renders.
    this.pageCanvas.width = canvas.width;
    this.pageCanvas.height = canvas.height;
    this.pageCanvas.getContext("2d").drawImage(canvas, 0, 0);
    this._bitmapScale = scale;
    this.pageCanvas.style.transformOrigin = "0 0";
    this._applyBitmapTransform();
  }

  _resized() {
    const dpr = window.devicePixelRatio || 1;
    this.overlay.width = Math.max(1, Math.round(this.width * dpr));
    this.overlay.height = Math.max(1, Math.round(this.height * dpr));
    this.overlay.style.width = `${this.width}px`;
    this.overlay.style.height = `${this.height}px`;
    if (this.page && !this._hadFitted && this.width > 0) { this._hadFitted = true; this.fit(); } else this.draw();
  }

  draw() {
    const ctx = this.overlay.getContext("2d");
    const dpr = window.devicePixelRatio || 1;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, this.width, this.height);
    if (!this.page) return;

    // Views and regions first, so items sit on top of them.
    if (this.showViews) for (const view of this.views) this._drawView(ctx, view);
    for (const region of this.regions) this._drawRegion(ctx, region);

    const visible = geo.visibleItems(this.view, this.items, this.width, this.height);
    // The selected item is drawn last so it is never hidden under its neighbours.
    for (const item of visible) if (item.id !== this.selectedId) this._drawItem(ctx, item, false);
    const selected = visible.find((i) => i.id === this.selectedId);
    if (selected) this._drawItem(ctx, selected, true);

    this._drawDraft(ctx);
  }

  _drawItem(ctx, item, selected) {
    const r = geo.boxToScreen(this.view, item.box);
    const color = COLORS[item.status] || COLORS.proposed;
    const w = Math.max(r.w, 5);
    const hh = Math.max(r.h, 5);
    const x = r.x - (w - r.w) / 2;
    const y = r.y - (hh - r.h) / 2;
    ctx.save();
    ctx.lineWidth = selected ? 3 : 1.5;
    ctx.strokeStyle = color;
    if (item.dashed || item.status === "rejected") ctx.setLineDash([5, 3]);
    ctx.fillStyle = selected ? hexAlpha(color, 0.25) : hexAlpha(color, item.status === "rejected" ? 0.04 : 0.1);
    ctx.fillRect(x, y, w, hh);
    ctx.strokeRect(x, y, w, hh);
    if (selected) {
      ctx.setLineDash([]);
      ctx.strokeStyle = "#ffffff";
      ctx.lineWidth = 1;
      ctx.strokeRect(x - 2, y - 2, w + 4, hh + 4);
    }

    // A label once there is room for it.
    if (item.label && (selected || this.view.scale > 0.6) && w > 20) {
      ctx.setLineDash([]);
      ctx.font = "11px system-ui, sans-serif";
      ctx.fillStyle = color;
      ctx.fillText(item.label, x, Math.max(10, y - 3));
    }

    ctx.restore();
  }

  _drawView(ctx, view) {
    const pts = view.polygon.map((p) => geo.toScreen(this.view, p[0], p[1]));
    ctx.save();
    ctx.beginPath();
    pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    ctx.strokeStyle = "#2563eb";
    ctx.setLineDash([14, 6]);
    ctx.lineWidth = 2;
    ctx.stroke();
    // A label at the top-left corner, kept on screen, so the person can tell which view is which and what scale it is.
    const left = Math.min(...pts.map((p) => p[0]));
    const top = Math.min(...pts.map((p) => p[1]));
    ctx.setLineDash([]);
    ctx.font = "12px system-ui, sans-serif";
    const text = view.label;
    const w = ctx.measureText(text).width + 10;
    const x = Math.max(2, left);
    const y = Math.max(2, top);
    ctx.fillStyle = "rgba(37,99,235,0.9)";
    ctx.fillRect(x, y, w, 18);
    ctx.fillStyle = "#ffffff";
    ctx.fillText(text, x + 5, y + 13);
    ctx.restore();
  }

  _drawRegion(ctx, region) {
    const pts = region.polygon.map((p) => geo.toScreen(this.view, p[0], p[1]));
    ctx.save();
    ctx.beginPath();
    pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    const exclude = region.kind === "Exclude";
    ctx.fillStyle = exclude ? "rgba(220,38,38,0.12)" : "rgba(22,163,74,0.08)";
    ctx.strokeStyle = exclude ? "#dc2626" : "#16a34a";
    ctx.setLineDash([8, 4]);
    ctx.lineWidth = 1.5;
    ctx.fill();
    ctx.stroke();
    ctx.restore();
  }

  _drawDraft(ctx) {
    const d = this.draft;
    if (!d) return;
    ctx.save();
    ctx.strokeStyle = "#7c3aed";
    ctx.fillStyle = "rgba(124,58,237,0.12)";
    ctx.lineWidth = 2;
    if (d.kind === "line" && d.points.length) {
      const pts = d.points.concat(d.cursor ? [d.cursor] : []).map((p) => geo.toScreen(this.view, p[0], p[1]));
      ctx.beginPath();
      pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
      ctx.stroke();
      for (const [x, y] of pts) { ctx.beginPath(); ctx.arc(x, y, 4, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); }
    } else if (d.kind === "rect" && d.start && d.cursor) {
      const poly = geo.rectPolygon(d.start, d.cursor).map((p) => geo.toScreen(this.view, p[0], p[1]));
      ctx.beginPath();
      poly.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
    }

    ctx.restore();
  }

  // ------------------------------------------------------------------ input

  _bind() {
    const vp = this.viewport;
    let drag = null;

    vp.addEventListener("wheel", (e) => {
      if (!this.page) return;
      e.preventDefault();
      const r = vp.getBoundingClientRect();
      this.view = geo.zoomAt(this.view, Math.exp(-e.deltaY * 0.0015), e.clientX - r.left, e.clientY - r.top);
      this._viewChanged();
    }, { passive: false });

    vp.addEventListener("pointerdown", (e) => {
      if (!this.page || e.button !== 0) return;
      vp.setPointerCapture(e.pointerId);
      const r = vp.getBoundingClientRect();
      drag = { sx: e.clientX, sy: e.clientY, lx: e.clientX, ly: e.clientY, moved: false, rect: r };
      if (this.tool === "region") this.draft = { kind: "rect", start: this._page(e, r), cursor: null };
    });

    vp.addEventListener("pointermove", (e) => {
      if (!this.page) return;
      const r = vp.getBoundingClientRect();
      if (drag) {
        if (Math.hypot(e.clientX - drag.sx, e.clientY - drag.sy) > 4) drag.moved = true;
        if (this.tool === "region" && this.draft?.kind === "rect") {
          this.draft.cursor = this._page(e, r);
          this.draw();
        } else if (drag.moved) {
          this.view = geo.pan(this.view, e.clientX - drag.lx, e.clientY - drag.ly);
          this._viewChanged();
        }
        drag.lx = e.clientX;
        drag.ly = e.clientY;
      } else {
        if (this.tool === "calibrate" && this.draft?.points?.length === 1) { this.draft.cursor = this._page(e, r); this.draw(); }
        this._hover(e, r);
      }
    });

    vp.addEventListener("pointerup", (e) => {
      if (!this.page || !drag) return;
      const r = vp.getBoundingClientRect();
      const wasClick = !drag.moved;
      const finished = drag;
      drag = null;
      if (this.tool === "region" && this.draft?.kind === "rect") {
        const start = this.draft.start;
        const end = this._page(e, r);
        this.draft = null;
        this.draw();
        if (!wasClick && Math.abs(end[0] - start[0]) > 4 && Math.abs(end[1] - start[1]) > 4) this.opts.onTool?.({ tool: "region", polygon: geo.rectPolygon(start, end) });
        return;
      }

      if (!wasClick) return;
      const point = this._page(e, r);
      if (this.tool === "add") { this.opts.onTool?.({ tool: "add", point }); return; }
      if (this.tool === "calibrate") {
        const points = (this.draft?.points || []).concat([point]);
        if (points.length >= 2) { this.draft = null; this.draw(); this.opts.onTool?.({ tool: "calibrate", points: points.slice(0, 2) }); } else { this.draft = { kind: "line", points, cursor: null }; this.draw(); }
        return;
      }

      const hit = geo.hitTest(this.view, this.items, e.clientX - r.left, e.clientY - r.top);
      this.opts.onSelect?.(hit ? hit.id : null, { additive: e.shiftKey || e.ctrlKey || e.metaKey });
      void finished;
    });

    vp.addEventListener("pointerleave", () => { this.tip.hidden = true; });
    vp.addEventListener("dblclick", (e) => {
      if (!this.page || this.tool !== "select") return;
      const r = vp.getBoundingClientRect();
      this.view = geo.zoomAt(this.view, 2, e.clientX - r.left, e.clientY - r.top);
      this._viewChanged();
    });
  }

  _page(e, rect) {
    return geo.clampPoint(geo.toPage(this.view, e.clientX - rect.left, e.clientY - rect.top), this.pageW, this.pageH);
  }

  _hover(e, rect) {
    if (this.tool !== "select") { this.tip.hidden = true; return; }
    const sx = e.clientX - rect.left;
    const sy = e.clientY - rect.top;
    const hit = geo.hitTest(this.view, this.items, sx, sy);
    this.viewport.style.cursor = hit ? "pointer" : "";
    if (hit?.tip) {
      this.tip.textContent = hit.tip;
      this.tip.style.left = `${Math.min(sx + 14, this.width - 180)}px`;
      this.tip.style.top = `${sy + 14}px`;
      this.tip.hidden = false;
    } else {
      this.tip.hidden = true;
    }

    this.opts.onHover?.(hit || null);
  }
}

function hexAlpha(hex, alpha) {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}
