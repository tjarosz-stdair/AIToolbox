// How the screens group, filter and place the items a detector found. Pure functions over the JSON the API returns.

export const REVIEW = { Proposed: "Proposed", Verified: "Verified", Rejected: "Rejected", Manual: "Manual" };

/** A short key for the colour and wording of an item's review state. */
export function statusKey(entity) {
  switch (entity.reviewStatus) {
    case "Verified": return "verified";
    case "Rejected": return "rejected";
    case "Manual": return "manual";
    default: return "proposed";
  }
}

function norm(text) {
  return String(text ?? "").trim().toLowerCase().replace(/\s+/g, "");
}

/** Whether a detector's free-text sheet reference ("M1.2") names this sheet. */
export function refMatchesSheet(ref, sheet) {
  if (!ref) return false;
  const r = norm(ref);
  return r === norm(sheet.sheetNumber) || r === `p${sheet.pageNo}` || r === norm(sheet.title);
}

/**
 * The items shown on a sheet: those a person or the server has placed on one of its views, and those whose reference
 * names the sheet. An item placed on a different sheet is never shown here, whatever its reference says.
 */
export function entitiesForSheet(entities, sheet) {
  return entities.filter((e) => (e.sheetId ? e.sheetId === sheet.sheetId : refMatchesSheet(e.sheetViewRef, sheet)));
}

/** The sheet an item belongs on, or null. */
export function sheetOf(entity, sheets) {
  if (entity.sheetId) return sheets.find((s) => s.sheetId === entity.sheetId) || null;
  return sheets.find((s) => refMatchesSheet(entity.sheetViewRef, s)) || null;
}

/**
 * @param filter { statuses:Set<string>, dispositions:Set<string>, minConfidence, maxConfidence, text, buildingTypeId, showRejected }
 * Empty sets mean "any".
 */
export function filterEntities(entities, filter = {}) {
  const text = norm(filter.text);
  return entities.filter((e) => {
    if (filter.statuses?.size && !filter.statuses.has(e.reviewStatus)) return false;
    if (!filter.showRejected && e.reviewStatus === "Rejected" && !filter.statuses?.has("Rejected")) return false;
    if (filter.dispositions?.size && !filter.dispositions.has(e.disposition)) return false;
    if (filter.minConfidence != null && e.confidence < filter.minConfidence) return false;
    if (filter.maxConfidence != null && e.confidence > filter.maxConfidence) return false;
    if (filter.buildingTypeId !== undefined && filter.buildingTypeId !== "" && String(e.buildingTypeId ?? "") !== String(filter.buildingTypeId)) return false;
    if (text) {
      const hay = norm([e.type, e.size, e.tag, e.category, e.manufacturer, e.model, e.customerPartCode, e.evidence].filter(Boolean).join(" "));
      if (!hay.includes(text)) return false;
    }

    return true;
  });
}

function naturalCompare(a, b) {
  return String(a ?? "").localeCompare(String(b ?? ""), undefined, { numeric: true, sensitivity: "base" });
}

export function itemLabel(e) {
  return [e.type, e.size, e.tag ? `(${e.tag})` : null].filter(Boolean).join(" ");
}

/**
 * Building type, then kind of item, then the items. Each node carries its count and how many are still proposals, so
 * the tree can show where the work is.
 */
export function buildTree(entities, buildingTypes) {
  const names = new Map(buildingTypes.map((b) => [b.buildingTypeId, b.name]));
  const byType = new Map();
  for (const e of entities) {
    const key = e.buildingTypeId ?? "none";
    if (!byType.has(key)) byType.set(key, { key: `bt:${key}`, id: e.buildingTypeId ?? null, label: e.buildingTypeId ? names.get(e.buildingTypeId) || `Type ${e.buildingTypeId}` : "Not assigned to a building type", groups: new Map(), items: [] });
    const node = byType.get(key);
    node.items.push(e);
    const gkey = [e.type, e.size ?? "", e.tag ?? ""].join("|");
    if (!node.groups.has(gkey)) node.groups.set(gkey, { key: `g:${key}:${gkey}`, label: itemLabel(e), type: e.type, size: e.size, items: [] });
    node.groups.get(gkey).items.push(e);
  }

  const summarize = (items) => ({
    count: items.length,
    quantity: items.reduce((n, e) => n + Number(e.quantity || 0), 0),
    proposed: items.filter((e) => e.reviewStatus === "Proposed").length
  });

  return [...byType.values()]
    .map((t) => ({
      key: t.key, id: t.id, label: t.label, ...summarize(t.items),
      groups: [...t.groups.values()]
        .sort((a, b) => naturalCompare(a.type, b.type) || naturalCompare(a.size, b.size) || naturalCompare(a.label, b.label))
        .map((g) => ({ ...g, ...summarize(g.items), items: [...g.items].sort((a, b) => a.confidence - b.confidence || a.entityId - b.entityId) }))
    }))
    .sort((a, b) => (a.id === null) - (b.id === null) || naturalCompare(a.label, b.label));
}

export function counts(entities) {
  const c = { total: entities.length, Proposed: 0, Verified: 0, Rejected: 0, Manual: 0, tbd: 0 };
  for (const e of entities) {
    c[e.reviewStatus] = (c[e.reviewStatus] || 0) + 1;
    if (e.disposition === "TBD") c.tbd++;
  }

  return c;
}

/** The next or previous item in a list, wrapping round. Returns the first when nothing is selected. */
export function stepItem(list, currentId, direction) {
  if (!list.length) return null;
  const i = list.findIndex((e) => e.publicId === currentId);
  if (i < 0) return list[direction > 0 ? 0 : list.length - 1];
  return list[(i + direction + list.length) % list.length];
}

/** What a person is asked to look at first: proposals, lowest confidence first. */
export function reviewQueue(entities) {
  return entities.filter((e) => e.reviewStatus === "Proposed").sort((a, b) => a.confidence - b.confidence || a.entityId - b.entityId);
}

/** Confidence at or above a threshold, among proposals: the items a person may choose to verify in bulk. */
export function bulkVerifiable(entities, threshold) {
  return entities.filter((e) => e.reviewStatus === "Proposed" && e.confidence >= threshold && e.disposition !== "TBD");
}

/** Replaces an item in a list by public id, returning a new list. */
export function replaceEntity(list, updated) {
  return list.map((e) => (e.publicId === updated.publicId ? updated : e));
}

/** The quantity rule a person must be told: nothing is final while proposals, open findings or unconfirmed multipliers remain. */
export function countSheetBlockers(sheet) {
  const out = [];
  if (sheet.unconfirmedBuildingTypes?.length) out.push(`${sheet.unconfirmedBuildingTypes.length} building type(s) have no confirmed multiplier: ${sheet.unconfirmedBuildingTypes.join(", ")}.`);
  if (sheet.openItems?.length) out.push(`${sheet.openItems.length} item(s) are still to be determined.`);
  if (sheet.openFindings?.length) out.push(`${sheet.openFindings.length} question(s) are unanswered.`);
  const proposed = (sheet.totals || []).reduce((n, t) => n + (t.proposedCount || 0), 0);
  if (proposed) out.push(`${proposed} counted item(s) have not been verified.`);
  return out;
}
