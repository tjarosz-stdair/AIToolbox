// Runs the real Ductwork screens in a real browser against the mock server and checks what a person would see. Prints one
// JSON line at the end: { ok, skipped, steps, failures }. Exit code 0 when everything held (or there is no browser here).

import { startServer } from "./mock-server.mjs";
import { findEdge, launchBrowser } from "./cdp.mjs";

const steps = [];
const failures = [];
const exe = findEdge();
if (!exe) {
  console.log(JSON.stringify({ ok: true, skipped: "No Edge or Chrome was found on this machine.", steps, failures }));
  process.exit(0);
}

const server = await startServer();
const base = `http://127.0.0.1:${server.port}`;
const browser = await launchBrowser(exe);
const s = browser.session;

const step = async (name, fn) => {
  try {
    await fn();
    const errors = s.takeErrors();
    if (errors.length) { failures.push(`${name}: the page reported errors: ${errors.join(" | ")}`); steps.push(`FAIL ${name}`); } else steps.push(`ok   ${name}`);
  } catch (e) {
    failures.push(`${name}: ${e.message}`);
    steps.push(`FAIL ${name}`);
    s.takeErrors();
  }
};

const text = () => s.eval("document.body.innerText");
// Screens fill in from the server after they appear, so the text is waited for rather than read once.
const expectText = async (...needles) => {
  const end = Date.now() + 10000;
  let t = "";
  for (;;) {
    t = await text();
    const missing = needles.filter((n) => !t.includes(n));
    if (!missing.length) return;
    if (Date.now() > end) throw new Error(`The page does not say “${missing[0]}”. It says:
${t.slice(0, 1500)}`);
    await new Promise((r) => setTimeout(r, 100));
  }
};
const state = async () => (await fetch(`${base}/__state`)).json();
const tab = async (id, ...needles) => {
  await s.eval(`location.hash = '#/${id}'`);
  await s.waitFor(`document.querySelector('.dw-tabpane[data-tab="${id}"]') && !document.querySelector('.dw-loading')`, 20000, `the ${id} tab to load`);
  await expectText(...needles);
};

try {
  await step("jobs list shows the job", async () => {
    await s.navigate(`${base}/Ductwork`);
    await s.waitFor("document.querySelector('.dw-jobrow')", 15000, "the job list");
    await expectText("Whiteland Apartments", "New job", "Settings");
  });

  await step("admin page shows bindings and the vision policy, with an ineligible model disabled", async () => {
    await s.navigate(`${base}/Ductwork/Admin`);
    await s.waitFor("document.body.innerText.includes('Hosted vision')", 15000, "the admin page");
    await expectText("Enabled kinds of work", "residential-label-counter", "Allow hosted vision as a fallback", "not eligible: The provider is not vision-enabled.");
    const disabled = await s.eval("[...document.querySelectorAll('input[type=checkbox]')].filter((c) => c.disabled).length");
    if (disabled !== 1) throw new Error(`Expected one disabled model checkbox, found ${disabled}.`);
  });

  await step("job page loads with its header and the lock bar", async () => {
    await s.navigate(`${base}/Ductwork/Jobs/1`);
    await s.waitFor("document.querySelector('.dw-tabs') && document.querySelector('.dw-tabpane')", 20000, "the workbench");
    await expectText("Whiteland Apartments", "Not editing", "Start editing", "Overview", "Drawings", "Review", "Count sheet");
  });

  await step("overview tab", async () => { await tab("overview", "Job details", "People on this job", "ed@example.com", "Progress", "Lifecycle", "Complete job", "Time on this job", "2.00 h"); });
  await step("drawings tab", async () => { await tab("drawings", "Drop PDF drawings here", "mech.pdf", "Sheets (2)", "no text"); });
  await step("setup tab", async () => { await tab("setup", "Building types and multipliers", "Type 4", "26 confirmed", "Type 5", "not set", "Views and scales"); });
  await step("detection tab", async () => { await tab("tasks", "Run detection", "detect-residential-labels", "classify-crops (hosted vision)", "Work on this job"); });
  await step("questions and rules tab", async () => { await tab("findings", "Questions (1 open)", "is the depth 7?", "4x12x4", "Rules", "Depth 7", "When type is Boot and size matches"); });
  await step("count sheet tab says why it is not final", async () => {
    await tab("count", "Not final yet", "Type 5", "have no confirmed multiplier", "78 EA", "TBD", "Unanswered questions", "Still to be determined");
  });
  await step("hosted vision tab", async () => { await tab("vision", "Hosted vision is available", "Approve sending pages", "M1.2", "no live text", "No approvals have been given"); });
  await step("audit tab verifies the log", async () => {
    await tab("audit", "Audit log", "Created the job.");
    await s.click(".dw-tabpane[data-tab=audit] .btn-outline-secondary");
    await s.waitFor("document.body.innerText.includes('The log is intact')", 8000, "the verification result");
  });

  await step("review tab draws the sheet and the items on it", async () => {
    await tab("review", "To review", "Verified", "Type 4");
    await s.waitFor("(() => { const c = document.querySelector('.dw-page'); return c && c.width > 100; })()", 25000, "pdf.js to draw the page");
    const inked = await s.eval(`(() => { const c = document.querySelector('.dw-page'); const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let ink = 0; for (let i = 0; i < d.length; i += 4) if (d[i] < 200 || d[i + 1] < 200) ink++; return ink; })()`);
    if (inked < 50) throw new Error(`The page canvas looks blank (${inked} dark pixels).`);
    const overlay = await s.eval(`(() => { const c = document.querySelector('.dw-overlay-canvas'); const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let n = 0; for (let i = 3; i < d.length; i += 4) if (d[i] > 0) n++; return n; })()`);
    if (overlay < 100) throw new Error(`The item boxes are not drawn over the sheet (${overlay} marked pixels).`);
  });

  await step("an item is found in the tree, selected, and verified from the keyboard (taking the lock on the way)", async () => {
    for (let i = 0; i < 4 && !(await s.eval("!!document.querySelector('[data-id=\"e1\"]')")); i++) await s.eval("(() => { const t = document.querySelector('.dw-twisty[aria-expanded=\"false\"]'); if (t) t.click(); })()");
    await s.waitFor("document.querySelector('[data-id=\"e1\"]')", 8000, "item e1 in the tree");
    await s.click("[data-id=\"e1\"]");
    await s.waitFor("document.querySelector('.dw-itempanel')?.innerText.includes('Boot 4x10x7')", 8000, "the item panel");
    await expectText("Verify (V)", "Reject (X)", "Correct…");
    await s.press("v");
    await s.waitFor("document.querySelector('.dw-lock-mine')", 8000, "the lock to be taken");
    const until = Date.now() + 8000;
    let e1;
    do { e1 = (await state()).entities.find((e) => e.publicId === "e1"); if (e1.reviewStatus === "Verified") break; await new Promise((r) => setTimeout(r, 100)); } while (Date.now() < until);
    if (e1.reviewStatus !== "Verified") throw new Error("Pressing V did not verify the item on the server.");
    await expectText("You are editing");
  });

  await step("clicking an item on the sheet selects it", async () => {
    // e2 is at x 180..210, y 120..140 on a 612x792 page. Ask the viewer where that is on screen.
    const spot = await s.eval(`(() => { const vp = document.querySelector('.dw-viewport').getBoundingClientRect(); const page = document.querySelector('.dw-page'); const t = page.style.transform.match(/translate\\(([-\\d.]+)px, ([-\\d.]+)px\\) scale\\(([\\d.]+)\\)/); const bmp = page.width / 612; const k = Number(t[3]) * bmp; return { x: vp.x + Number(t[1]) + 195 * k, y: vp.y + Number(t[2]) + 130 * k }; })()`);
    await s.clickAt(spot.x, spot.y);
    await s.waitFor("document.querySelector('.dw-itempanel')?.innerText.includes('4x12x7')", 8000, "the panel for the item clicked on the sheet");
  });

  await step("an item the detector missed can be added on the sheet", async () => {
    await s.click("button[title^='Click on the page to add']");
    await s.waitFor("document.querySelector('.dw-toolhint')", 4000, "the add tool");
    const vp = await s.eval("(() => { const r = document.querySelector('.dw-viewport').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 + 120 }; })()");
    await s.clickAt(vp.x, vp.y);
    await s.waitFor("document.querySelector('.dw-dialog')", 5000, "the add dialog");
    await s.eval("(() => { const i = document.querySelector('.dw-dialog input'); i.focus(); i.value = 'Damper'; i.dispatchEvent(new Event('input', { bubbles: true })); })()");
    await s.eval("[...document.querySelectorAll('.dw-dialog-foot button')].find((b) => b.textContent === 'Add').click()");
    const until = Date.now() + 8000;
    let added;
    do { added = (await state()).entities.find((e) => e.type === "Damper"); if (added) break; await new Promise((r) => setTimeout(r, 100)); } while (Date.now() < until);
    if (!added) throw new Error("The item was not sent to the server.");
    if (added.reviewStatus !== "Manual" || !added.viewId) throw new Error(`The added item has the wrong shape: ${JSON.stringify(added)}`);
    const sheet = (await state()).sheets.find((x) => x.sheetId === "s1");
    if (!sheet.views.length) throw new Error("A whole-page view was not created for the sheet.");
  });

  await step("a file is uploaded in pieces, each checked, and arrives whole", async () => {
    const result = await s.eval(`(async () => {
      const { Upload } = await import('/js/ductwork/uploader.js'); const { createApi } = await import('/js/ductwork/api.js');
      const api = createApi({ base: '/ductwork/api', antiforgery: () => 'test-token' }); api.setFencing(9);
      const bytes = new Uint8Array(10000).map((_, i) => i % 251);
      const stored = await new Upload({ api, jobId: 1, file: new File([bytes], 'big.pdf') }).run();
      return { id: stored.fileId, bytes: stored.bytes };
    })()`);
    if (result.bytes !== 10000) throw new Error(`The server stored ${result.bytes} bytes, not 10000.`);
    const u = (await state()).uploads.at(-1);
    if (u.received !== 10000) throw new Error("The upload did not arrive whole.");
  });

  const clickButton = (label, scope = "document") => s.eval(`(() => { const b = [...${scope}.querySelectorAll('button,a.btn')].find((x) => x.textContent.trim().startsWith(${JSON.stringify(label)}) && !x.disabled); if (!b) throw new Error('No enabled button starting with ${label}'); b.click(); })()`);
  const fill = (selector, value) => s.eval(`(() => { const i = document.querySelector(${JSON.stringify(selector)}); i.value = ${JSON.stringify(value)}; i.dispatchEvent(new Event('input', { bubbles: true })); i.dispatchEvent(new Event('change', { bubbles: true })); })()`);
  const until = async (fn, what, ms = 8000) => { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return; await new Promise((r) => setTimeout(r, 100)); } throw new Error(`Timed out waiting for ${what}`); };

  await step("admin lists workers, drains one, and shows the key once", async () => {
    await s.navigate(`${base}/Ductwork/Admin`);
    await expectText("Workers", "farm-1", "FARM-01", "Online", "1 running, up to 2");
    await clickButton("Drain");
    await s.waitFor("document.querySelector('.dw-dialog')", 5000, "the drain confirmation");
    await clickButton("Drain", "document.querySelector('.dw-dialog')");
    await expectText("Draining", "Resume");
    await clickButton("Create worker key");
    await s.waitFor("document.querySelector('.dw-dialog input')", 5000, "the key dialog");
    await fill(".dw-dialog input", "farm-2");
    await clickButton("Create", "document.querySelector('.dw-dialog')");
    await expectText("shown only once");
    const shown = await s.eval("document.querySelector('.dw-dialog textarea').value");
    if (!shown.includes("aitb_SMOKE") || !shown.includes("farm-2")) throw new Error(`The settings do not carry the key and name: ${shown}`);
    await clickButton("I have copied it", "document.querySelector('.dw-dialog')");
  });

  await step("admin page lists the Job Managers", async () => {
    await s.navigate(`${base}/Ductwork/Admin`);
    await expectText("Job Managers", "mgr@example.com", "does not let them see");
  });

  await step("archives page: archive and remove, wait for it, then restore", async () => {
    await s.navigate(`${base}/Ductwork/Archives`);
    await expectText("Archives", "Old Bid", "Closed", "Archive…");
    await clickButton("Archive…");
    await s.waitFor("document.querySelector('.dw-dialog')", 5000, "the archive dialog");
    await expectText("Keep a copy", "Archive and remove from the database");
    await s.eval("document.querySelector('.dw-dialog input[value=Purge]').click()");
    // The name must be typed before it will go ahead.
    await clickButton("Archive", "document.querySelector('.dw-dialog')");
    await expectText("Type the job’s name exactly");
    await fill(".dw-dialog input[type=text], .dw-dialog input:not([type])", "Old Bid");
    await clickButton("Archive", "document.querySelector('.dw-dialog')");
    await until(async () => (await state()).calls.some((c) => c === "POST /jobs/7/archive"), "the archive request");
    await expectText("Archiving and removing from the database");
    await expectText("Archived and removed from the database", "Restore");
    await clickButton("Restore");
    await s.waitFor("document.querySelector('.dw-dialog')", 5000, "the restore confirmation");
    await clickButton("Restore", "document.querySelector('.dw-dialog')");
    await expectText("Restoring");
    await expectText("Archived (copy kept, data still in the database)", "Archive…");
  });

  await step("detection tab warns when no worker is connected", async () => {
    await s.navigate(`${base}/Ductwork/Jobs/1`);
    await s.waitFor("document.querySelector('.dw-tabs') && document.querySelector('.dw-tabpane')", 20000, "the workbench");
    await tab("tasks", "No worker is connected", "taking no new work");
  });

  await step("a multiplier is confirmed from the setup tab", async () => {
    await tab("setup", "Type 5");
    await s.eval("(() => { const row = [...document.querySelectorAll('tr')].find((r) => r.innerText.includes('Type 5')); [...row.querySelectorAll('button')].find((b) => b.textContent === 'Set count').click(); })()");
    await s.waitFor("document.querySelector('.dw-dialog input[type=number]')", 5000, "the multiplier dialog");
    await fill(".dw-dialog input[type=number]", "12");
    await clickButton("Confirm", "document.querySelector('.dw-dialog')");
    await until(async () => (await state()).types[1].confirmed === true, "the multiplier to be confirmed on the server");
    if ((await state()).types[1].multiplier !== 12) throw new Error("The wrong multiplier was sent.");
  });

  await step("a question is answered by applying a rule after previewing it", async () => {
    await tab("findings", "Questions (1 open)");
    await clickButton("Apply a rule…");
    await s.waitFor("document.querySelector('.dw-dialog') && document.querySelector('.dw-dialog').innerText.includes('size 4x12 -> 4x12x7')", 8000, "the preview");
    await clickButton("Apply and answer", "document.querySelector('.dw-dialog')");
    await until(async () => (await state()).findings[0].state === "Resolved", "the question to be resolved on the server");
    await s.waitFor("document.body.innerText.includes('Questions (0 open)')", 8000, "the question to be closed");
  });

  await step("hosted vision is approved only after the notice is read, with a reason", async () => {
    await tab("vision", "Approve sending pages");
    await clickButton("Review what would be sent");
    await s.waitFor("document.querySelector('.dw-disclosure')", 8000, "the disclosure");
    await expectText("You are about to approve", "Drawing content leaves this server.", "vision-1", "Microsoft Foundry");
    await clickButton("Approve sending these pages");              // without ticking the box
    await s.waitFor("document.querySelector('.dw-toast-error')", 4000, "the refusal to approve without agreeing");
    await s.eval("(() => { const i = document.querySelector('.dw-disclosure input[type=text], .dw-disclosure input.form-control'); i.value = 'No live text on this sheet'; i.dispatchEvent(new Event('input', { bubbles: true })); document.querySelector('#dw-vision-agree').click(); })()");
    await clickButton("Approve sending these pages");
    await until(async () => (await state()).calls.some((c) => c.startsWith("POST /jobs/1/vision-approvals")), "the approval to be sent");
  });

  await step("a tag is added on the overview", async () => {
    await tab("overview", "Tags");
    await fill("input[aria-label='New tag']", "Priority");
    await s.eval("document.querySelector(\"input[aria-label='New tag']\").focus()");
    await s.press("Enter");
    await until(async () => (await state()).tags.includes("Priority"), "the tag to reach the server");
    await s.waitFor("document.body.innerText.includes('Priority')", 6000, "the tag");
  });

  await step("no request broke the API's rules (tab id, antiforgery token, fencing token on every edit)", async () => {
    const st = await state();
    if (st.violations.length) throw new Error(`The page made requests the real API would refuse:\n${st.violations.join("\n")}`);
  });
} finally {
  await browser.close();
  await server.close();
}

console.log(JSON.stringify({ ok: failures.length === 0, steps, failures }));
process.exit(failures.length ? 1 : 0);
