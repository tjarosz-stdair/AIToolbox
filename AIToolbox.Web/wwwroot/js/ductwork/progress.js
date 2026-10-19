// Live progress: the server pushes a small event when a task or archive operation on a job someone has open changes, so the page can show it
// at once. An event says only that something changed and gives a progress bar's numbers; a page that wants more re-reads through the normal API.
//
// The connection is a convenience, never a requirement. While it is down (or if it never comes up: a blocked script, an old browser, a proxy that
// does not pass web sockets) `connected` is false and pages go back to asking on a timer, exactly as they did before this existed.

const RETRY_MS = 30000;
const RECONNECT_DELAYS = [0, 2000, 5000, 10000, 30000];

let scriptLoading = null;

/** Loads the vendored SignalR client once. It is a classic script that defines `signalR`. */
export function loadSignalR(boot, doc = document) {
  if (globalThis.signalR) return Promise.resolve(globalThis.signalR);
  scriptLoading ??= new Promise((resolve, reject) => {
    const s = doc.createElement("script");
    s.src = boot.signalr;
    s.onload = () => (globalThis.signalR ? resolve(globalThis.signalR) : reject(new Error("The live progress library did not load.")));
    s.onerror = () => { scriptLoading = null; reject(new Error("The live progress library could not be loaded.")); };
    doc.head.append(s);
  });
  return scriptLoading;
}

function defaultConnection(boot, lib) {
  return new lib.HubConnectionBuilder().withUrl(boot.progressHub).withAutomaticReconnect(RECONNECT_DELAYS).build();
}

export class ProgressClient {
  /**
   * @param boot what the server put in the page (`progressHub`, `signalr`)
   * @param deps replaceable for tests: `createConnection(boot)` returns an object shaped like a SignalR HubConnection, `setTimeout`/`clearTimeout`
   */
  constructor(boot, deps = {}) {
    this.boot = boot;
    this._create = deps.createConnection || (async () => defaultConnection(boot, await loadSignalR(boot)));
    this._setTimeout = deps.setTimeout || ((fn, ms) => setTimeout(fn, ms));
    this._clearTimeout = deps.clearTimeout || ((t) => clearTimeout(t));
    this.connected = false;
    this._handlers = new Map();   // jobId -> Set of handlers
    this._stateListeners = new Set();
    this._conn = null;
    this._starting = null;
    this._retry = null;
  }

  /**
   * Calls `handler(events, { resync })` whenever the job's tasks or archive operations change. `resync` is true after the connection came back, when
   * changes may have been missed and the page should re-read everything. Returns a function that stops watching.
   */
  watch(jobId, handler) {
    if (!this.boot.progressHub || !this.boot.signalr) return () => {};
    if (!this._handlers.has(jobId)) this._handlers.set(jobId, new Set());
    this._handlers.get(jobId).add(handler);
    this._start().then(() => this._invokeWatch(jobId)).catch(() => {});
    return () => this._unwatch(jobId, handler);
  }

  /** Calls `fn(connected)` when the connection goes up or down. */
  onState(fn) {
    this._stateListeners.add(fn);
    return () => this._stateListeners.delete(fn);
  }

  async stop() {
    this._clearTimeout(this._retry);
    this._retry = null;
    this._handlers.clear();
    const conn = this._conn;
    this._conn = null;
    this._starting = null;
    this._setConnected(false);
    if (conn) await conn.stop().catch(() => {});
  }

  _unwatch(jobId, handler) {
    const set = this._handlers.get(jobId);
    if (!set) return;
    set.delete(handler);
    if (set.size > 0) return;
    this._handlers.delete(jobId);
    if (this.connected) this._conn?.invoke("Unwatch", jobId).catch(() => {});
  }

  _setConnected(value) {
    if (this.connected === value) return;
    this.connected = value;
    for (const fn of [...this._stateListeners]) { try { fn(value); } catch (e) { console.error(e); } }
  }

  _start() {
    if (this._starting) return this._starting;
    this._starting = (async () => {
      try {
        const conn = await this._create(this.boot);
        conn.on("progress", (jobId, events) => this._dispatch(jobId, events, false));
        conn.onreconnecting(() => this._setConnected(false));
        conn.onreconnected(async () => {
          await this._watchAll();
          this._setConnected(true);
          for (const jobId of this._handlers.keys()) this._dispatch(jobId, [], true);
        });
        // Automatic reconnection gives up after a while; keep trying slowly, because the page may be left open for days.
        conn.onclose(() => { this._setConnected(false); this._scheduleRetry(); });
        await conn.start();
        this._conn = conn;
        this._setConnected(true);
      } catch (e) {
        this._starting = null;
        this._setConnected(false);
        this._scheduleRetry();
        throw e;
      }
    })();
    return this._starting;
  }

  _scheduleRetry() {
    if (this._retry || this._handlers.size === 0) return;
    this._retry = this._setTimeout(async () => {
      this._retry = null;
      this._conn = null;
      this._starting = null;
      try {
        await this._start();
        await this._watchAll();
        for (const jobId of this._handlers.keys()) this._dispatch(jobId, [], true);
      } catch { /* _start has already scheduled the next try */ }
    }, RETRY_MS);
  }

  async _invokeWatch(jobId) {
    if (!this._conn || !this._handlers.has(jobId)) return;
    try { await this._conn.invoke("Watch", jobId); } catch { /* not allowed or not found: the page's own reads will say so */ }
  }

  _watchAll() {
    return Promise.all([...this._handlers.keys()].map((id) => this._invokeWatch(id)));
  }

  _dispatch(jobId, events, resync) {
    for (const handler of [...(this._handlers.get(jobId) || [])]) {
      try { handler(events || [], { resync }); } catch (e) { console.error(e); }
    }
  }
}

/** Calls `fn` once after `ms` of quiet, however many times it is asked. Events for one task arrive in bursts; one repaint covers them. */
export function settle(fn, ms, deps = {}) {
  const set = deps.setTimeout || ((f, t) => setTimeout(f, t));
  const clear = deps.clearTimeout || ((t) => clearTimeout(t));
  let timer = null;
  const run = () => { clear(timer); timer = set(() => { timer = null; fn(); }, ms); };
  run.cancel = () => { clear(timer); timer = null; };
  return run;
}
