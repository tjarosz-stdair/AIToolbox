// Reading a drawing file in the browser with pdf.js: how many pages, how big each is on screen, and whether it has live text.
// The same library draws the sheet in the viewer, so what the server records about a page is what the viewer shows.

let loading = null;

/** Loads pdf.js once, from the copy served with the site. */
export function loadPdfjs(boot) {
  if (!loading) {
    loading = import(/* @vite-ignore */ boot.pdfjs).then((lib) => {
      lib.GlobalWorkerOptions.workerSrc = boot.pdfjsWorker;
      return lib;
    });
  }

  return loading;
}

export async function openPdf(boot, source) {
  const lib = await loadPdfjs(boot);
  return lib.getDocument({ ...source, withCredentials: true, isEvalSupported: false }).promise;
}

/**
 * What the server should record about each page. The size is the displayed page (after the page's own rotation) in points,
 * because that is the space every box and polygon is stored in. Pages with no text at all are drawn as outlines and have to be
 * read by other means, so the server is told.
 */
export async function readPages(doc, { onProgress } = {}) {
  const pages = [];
  for (let n = 1; n <= doc.numPages; n++) {
    const page = await doc.getPage(n);
    const view = page.getViewport({ scale: 1 });
    const text = await page.getTextContent();
    pages.push({
      pageNo: n,
      widthPts: round(view.width),
      heightPts: round(view.height),
      rotation: normalizeRotation(page.rotate),
      hasTextLayer: text.items.some((i) => (i.str || "").trim().length > 0),
      sheetNumber: guessSheetNumber(text.items, view),
      title: null
    });
    page.cleanup();
    onProgress?.(n, doc.numPages);
  }

  return pages;
}

const round = (n) => Math.round(n * 100) / 100;

export function normalizeRotation(r) {
  const n = ((Math.round((r || 0) / 90) * 90) % 360 + 360) % 360;
  return [0, 90, 180, 270].includes(n) ? n : 0;
}

const SHEET_NUMBER = /^[A-Z]{1,3}[-\s]?\d{1,3}(\.\d{1,3})?[A-Z]?$/;

/**
 * A guess at the sheet number from the text on the page: the text in the title block, which sits in the lower right of nearly
 * every drawing, that looks like M1.2 or M-101. It is only a starting point for the person to correct.
 * @param items pdf.js text items ({ str, transform:[a,b,c,d,e,f] })
 * @param view  the page viewport at scale 1 (width, height, and convertToViewportPoint)
 */
export function guessSheetNumber(items, view) {
  const candidates = [];
  for (const item of items) {
    const text = (item.str || "").trim();
    if (!SHEET_NUMBER.test(text)) continue;
    const [x, y] = view.convertToViewportPoint ? view.convertToViewportPoint(item.transform[4], item.transform[5]) : [item.transform[4], view.height - item.transform[5]];
    const right = x / view.width;
    const low = y / view.height;
    if (right < 0.6 || low < 0.6) continue;                       // not in the title block corner
    const size = Math.hypot(item.transform[0], item.transform[1]);
    candidates.push({ text, size, score: size + right + low });
  }

  candidates.sort((a, b) => b.size - a.size || b.score - a.score);
  return candidates[0]?.text ?? null;
}
