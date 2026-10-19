// Pure geometry for the sheet viewer. Everything the server stores about a sheet is in PDF points on the displayed page
// (origin top-left, y down, after the page's own rotation), which is exactly the space pdf.js shows at scale 1, so a box
// on screen is its points times the zoom plus the pan. Nothing here touches the DOM.

/** The zoom that fits a page inside a viewport with a margin. */
export function fitScale(pageW, pageH, viewW, viewH, margin = 16) {
  if (!(pageW > 0 && pageH > 0 && viewW > 0 && viewH > 0)) return 1;
  return Math.max(0.01, Math.min((viewW - 2 * margin) / pageW, (viewH - 2 * margin) / pageH));
}

/** A view of a page: how far it is zoomed and where its top-left corner sits in the viewport. */
export function fitView(pageW, pageH, viewW, viewH, margin = 16) {
  const scale = fitScale(pageW, pageH, viewW, viewH, margin);
  return { scale, x: (viewW - pageW * scale) / 2, y: (viewH - pageH * scale) / 2 };
}

export const MIN_SCALE = 0.02;
export const MAX_SCALE = 16;

/** Zooms by a factor keeping the point under the cursor where it is. */
export function zoomAt(view, factor, px, py) {
  const scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, view.scale * factor));
  const k = scale / view.scale;
  return { scale, x: px - (px - view.x) * k, y: py - (py - view.y) * k };
}

export function pan(view, dx, dy) {
  return { ...view, x: view.x + dx, y: view.y + dy };
}

export function toScreen(view, x, y) {
  return [view.x + x * view.scale, view.y + y * view.scale];
}

export function toPage(view, sx, sy) {
  return [(sx - view.x) / view.scale, (sy - view.y) / view.scale];
}

export function boxToScreen(view, box) {
  const [x0, y0] = toScreen(view, box.x0, box.y0);
  const [x1, y1] = toScreen(view, box.x1, box.y1);
  return { x: Math.min(x0, x1), y: Math.min(y0, y1), w: Math.abs(x1 - x0), h: Math.abs(y1 - y0) };
}

export function area(box) {
  return Math.max(0, box.x1 - box.x0) * Math.max(0, box.y1 - box.y0);
}

/**
 * The item under a screen point. Among boxes that contain the point the smallest wins, so a small label inside a
 * large region is reachable. A box too small to hit on screen counts as `tolerance` pixels wide.
 * @param items [{ id, box }]
 */
export function hitTest(view, items, sx, sy, tolerance = 6) {
  let best = null;
  let bestArea = Infinity;
  for (const item of items) {
    if (!item.box) continue;
    const r = boxToScreen(view, item.box);
    const pad = Math.max(0, (tolerance * 2 - Math.min(r.w, r.h)) / 2);
    if (sx >= r.x - pad && sx <= r.x + r.w + pad && sy >= r.y - pad && sy <= r.y + r.h + pad) {
      const a = Math.max(r.w, tolerance * 2) * Math.max(r.h, tolerance * 2);
      if (a < bestArea) { best = item; bestArea = a; }
    }
  }
  return best;
}

/** The items whose box touches the viewport, so a sheet with thousands of items draws only what can be seen. */
export function visibleItems(view, items, viewW, viewH) {
  const out = [];
  for (const item of items) {
    if (!item.box) continue;
    const r = boxToScreen(view, item.box);
    if (r.x + r.w >= 0 && r.y + r.h >= 0 && r.x <= viewW && r.y <= viewH) out.push(item);
  }
  return out;
}

/** The view that centres a box and zooms to show it with room around it, never closer than `maxScale`. */
export function focusBox(view, box, viewW, viewH, { maxScale = 4, padding = 8 } = {}) {
  const w = Math.max(1, box.x1 - box.x0);
  const h = Math.max(1, box.y1 - box.y0);
  const scale = Math.min(maxScale, Math.max(view.scale, Math.min(viewW / (w * padding), viewH / (h * padding))));
  const cx = (box.x0 + box.x1) / 2;
  const cy = (box.y0 + box.y1) / 2;
  return { scale, x: viewW / 2 - cx * scale, y: viewH / 2 - cy * scale };
}

export function distance(a, b) {
  return Math.hypot(b[0] - a[0], b[1] - a[1]);
}

/**
 * The scale that makes two clicked points a known real length apart. A scale is the real length per drawn length, so
 * 1/4 inch = 1 foot is 48. Points are 1/72 inch, so the drawn inches are the distance over 72.
 */
export function ratioFromCalibration(pointA, pointB, knownLengthFeet) {
  const d = distance(pointA, pointB);
  if (!(d > 0) || !(knownLengthFeet > 0)) return null;
  return (knownLengthFeet * 12) / (d / 72);
}

/** The real length in feet that a drawn distance in points stands for at a scale. */
export function feetFor(distancePts, ratio) {
  return (distancePts / 72) * ratio / 12;
}

/** Common architectural and engineering scales: the label, and the ratio the API stores. */
export const STANDARD_SCALES = [
  ["1/8\" = 1'-0\"", 96], ["3/16\" = 1'-0\"", 64], ["1/4\" = 1'-0\"", 48], ["3/8\" = 1'-0\"", 32], ["1/2\" = 1'-0\"", 24],
  ["3/4\" = 1'-0\"", 16], ["1\" = 1'-0\"", 12], ["1 1/2\" = 1'-0\"", 8], ["3\" = 1'-0\"", 4], ["1\" = 10'", 120], ["1\" = 20'", 240]
];

export function scaleLabel(ratio) {
  if (ratio == null) return "not set";
  const known = STANDARD_SCALES.find(([, r]) => Math.abs(r - ratio) < 0.005);
  return known ? known[0] : `1 : ${Number(ratio.toFixed(3))}`;
}

/** How a view is named wherever it is listed or outlined: its title and its scale, or just the title when no scale is set. */
export function viewLabel(view) {
  return view.scaleRatio == null ? view.title : `${view.title}: ${scaleLabel(view.scaleRatio)}`;
}

export function rectPolygon(a, b) {
  const x0 = Math.min(a[0], b[0]);
  const y0 = Math.min(a[1], b[1]);
  const x1 = Math.max(a[0], b[0]);
  const y1 = Math.max(a[1], b[1]);
  return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]];
}

export function clampPoint(p, pageW, pageH) {
  return [Math.min(pageW, Math.max(0, p[0])), Math.min(pageH, Math.max(0, p[1]))];
}

/** A square box of `size` points around a page point, kept inside the page. For an item placed by hand. */
export function boxAround(p, size, pageW, pageH) {
  const half = size / 2;
  const [x, y] = clampPoint(p, pageW, pageH);
  return { x0: Math.max(0, x - half), y0: Math.max(0, y - half), x1: Math.min(pageW, x + half), y1: Math.min(pageH, y + half) };
}
