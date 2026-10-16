// A very small Chrome DevTools Protocol client, enough to drive a headless Edge: open a page, run script in it, wait for
// things to appear, press keys and click, and collect every error the page reports. It uses only what Node ships with.

import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export function findEdge() {
  const candidates = [process.env.EDGE_PATH, "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "/usr/bin/microsoft-edge", "/usr/bin/google-chrome", "/usr/bin/chromium"].filter(Boolean);
  return candidates.find((c) => fs.existsSync(c)) || null;
}

export async function launchBrowser(exe) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "dw-smoke-"));
  const child = spawn(exe, ["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--window-size=1500,950", `--user-data-dir=${profile}`,
    "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"] });
  const wsUrl = await new Promise((resolve, reject) => {
    let text = "";
    const timer = setTimeout(() => reject(new Error("The browser did not start in time.\n" + text)), 30000);
    child.stderr.on("data", (d) => {
      text += d;
      const m = text.match(/DevTools listening on (ws:\/\/[^\s]+)/);
      if (m) { clearTimeout(timer); resolve(m[1]); }
    });
    child.on("exit", (code) => reject(new Error(`The browser exited with code ${code}.\n${text}`)));
  });

  const port = new URL(wsUrl).port;
  const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = list.find((t) => t.type === "page");
  const session = new Session(page.webSocketDebuggerUrl);
  await session.open();
  return {
    session,
    async close() {
      try { session.close(); } catch { /* already closed */ }
      child.kill();
      await new Promise((r) => setTimeout(r, 300));
      try { fs.rmSync(profile, { recursive: true, force: true }); } catch { /* the browser may still hold a file */ }
    }
  };
}

export class Session {
  constructor(url) {
    this.url = url;
    this.id = 0;
    this.pending = new Map();
    this.errors = [];
    this.logs = [];
  }

  open() {
    return new Promise((resolve, reject) => {
      this.ws = new WebSocket(this.url);
      this.ws.onopen = async () => {
        for (const domain of ["Page", "Runtime", "Log", "Network"]) await this.send(`${domain}.enable`);
        resolve();
      };
      this.ws.onerror = (e) => reject(new Error("Could not connect to the browser: " + (e.message || e)));
      this.ws.onmessage = (ev) => this._message(JSON.parse(ev.data));
    });
  }

  close() { this.ws?.close(); }

  _message(msg) {
    if (msg.id && this.pending.has(msg.id)) {
      const { resolve, reject } = this.pending.get(msg.id);
      this.pending.delete(msg.id);
      msg.error ? reject(new Error(`${msg.error.message}`)) : resolve(msg.result);
      return;
    }

    switch (msg.method) {
      case "Runtime.exceptionThrown": {
        const d = msg.params.exceptionDetails;
        this.errors.push(`exception: ${d.exception?.description || d.text} at ${d.url || ""}:${d.lineNumber}`);
        break;
      }
      case "Runtime.consoleAPICalled":
        if (msg.params.type === "error") this.errors.push(`console.error: ${msg.params.args.map((a) => a.value ?? a.description ?? "").join(" ")}`);
        else this.logs.push(`${msg.params.type}: ${msg.params.args.map((a) => a.value ?? a.description ?? "").join(" ")}`);
        break;
      case "Log.entryAdded":
        if (msg.params.entry.level === "error") this.errors.push(`log.error: ${msg.params.entry.text} ${msg.params.entry.url || ""}`);
        break;
      default:
    }
  }

  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  async navigate(url) {
    await this.send("Page.navigate", { url });
    await this.waitFor("document.readyState === 'complete'", 20000);
  }

  /** Evaluates an expression in the page and returns its value. */
  async eval(expression, { awaitPromise = true } = {}) {
    const r = await this.send("Runtime.evaluate", { expression, awaitPromise, returnByValue: true });
    if (r.exceptionDetails) throw new Error(`In the page: ${r.exceptionDetails.exception?.description || r.exceptionDetails.text}`);
    return r.result.value;
  }

  async waitFor(expression, timeoutMs = 15000, what = expression) {
    const until = Date.now() + timeoutMs;
    let last;
    while (Date.now() < until) {
      try { last = await this.eval(`(() => { try { return !!(${expression}); } catch (e) { return false; } })()`); } catch (e) { last = false; }
      if (last) return true;
      await new Promise((r) => setTimeout(r, 100));
    }

    throw new Error(`Timed out waiting for: ${what}`);
  }

  /** Clicks the centre of the first element matching a selector, with real mouse events. */
  async click(selector, { offsetX, offsetY } = {}) {
    const box = await this.eval(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) return null; e.scrollIntoView({ block: 'center' }); const r = e.getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; })()`);
    if (!box) throw new Error(`Nothing matches ${selector} to click.`);
    return this.clickAt(box.x + (offsetX ?? box.w / 2), box.y + (offsetY ?? box.h / 2));
  }

  async clickAt(x, y) {
    await this.send("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
    await this.send("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", clickCount: 1 });
    await this.send("Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "left", clickCount: 1 });
  }

  async press(key) {
    const code = key.length === 1 ? `Key${key.toUpperCase()}` : key;
    await this.send("Input.dispatchKeyEvent", { type: "keyDown", key, code, text: key.length === 1 ? key : undefined });
    await this.send("Input.dispatchKeyEvent", { type: "keyUp", key, code });
  }

  async type(text) {
    for (const ch of text) { await this.send("Input.dispatchKeyEvent", { type: "keyDown", key: ch, text: ch }); await this.send("Input.dispatchKeyEvent", { type: "keyUp", key: ch }); }
  }

  takeErrors() {
    const e = this.errors.splice(0);
    return e;
  }
}
