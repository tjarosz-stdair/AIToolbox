// The single-editor lock from this tab's point of view (plan section 9). One tab at a time can edit a job; the others
// can look. This holds the state, keeps the lock alive with a heartbeat, and tells the screen when something changes:
// it was taken, it was lost, or someone is asking for it. It never decides what an edit may do; the server does.

export class LockManager {
  /**
   * @param api   the Ductwork API client (get/post/del/setFencing)
   * @param jobId the job
   * @param opts  { heartbeatMs, onChange(state), setTimer/clearTimer for tests }
   */
  constructor(api, jobId, opts = {}) {
    this.api = api;
    this.jobId = jobId;
    this.heartbeatMs = opts.heartbeatMs ?? 15000;
    this.onChange = opts.onChange || (() => {});
    this._setInterval = opts.setInterval || ((fn, ms) => setInterval(fn, ms));
    this._clearInterval = opts.clearInterval || ((t) => clearInterval(t));
    this.state = { youHoldIt: false, holder: null, token: null, pendingRequest: null, lost: false, message: "" };
    this._timer = null;
    this._busy = false;
  }

  get path() { return `/jobs/${this.jobId}/lock`; }
  get holding() { return this.state.youHoldIt; }

  _set(patch) {
    this.state = { ...this.state, ...patch };
    this.api.setFencing(this.state.youHoldIt ? this.state.token : null);
    this.onChange(this.state);
  }

  /** Looks without touching. */
  async refresh() {
    const s = await this.api.get(this.path);
    this._set({ youHoldIt: !!s.youHoldIt, holder: s.holder, token: s.fencingToken ?? null, pendingRequest: s.pendingRequest });
    return this.state;
  }

  /** Takes the lock if it is free (or expired). Returns true if this tab now holds it. */
  async acquire() {
    const r = await this.api.post(this.path);
    if (r.outcome === "HeldByOther") {
      this._set({ youHoldIt: false, holder: r.holder, token: null, lost: false, message: `${r.holder?.name || r.holder?.email || "Someone"} is editing this job.` });
      return false;
    }

    this._set({ youHoldIt: true, holder: r.holder, token: r.fencingToken, lost: false, message: "" });
    this._start();
    return true;
  }

  async release() {
    this._stop();
    if (this.state.youHoldIt) {
      try { await this.api.del(this.path); } catch { /* it will expire */ }
    }
    this._set({ youHoldIt: false, token: null, holder: null, pendingRequest: null });
  }

  /** Best-effort release as the page closes. */
  releaseOnUnload() {
    if (!this.state.youHoldIt) return;
    try { this.api.request("DELETE", this.path, { keepalive: true }); } catch { /* it will expire */ }
  }

  async requestTakeover() {
    const r = await this.api.post(`${this.path}/takeover`);
    if (r.granted) {
      this._set({ youHoldIt: true, token: r.fencingToken, holder: r.holder, lost: false, message: "" });
      this._start();
    } else {
      this._set({ message: r.message || "Your request was sent. The current editor can hand over or the lock passes to you if they do not answer.", pendingRequest: r.request || null });
    }
    return r;
  }

  async cancelTakeover(id) {
    await this.api.del(`${this.path}/takeover/${id}`);
    this._set({ pendingRequest: null, message: "" });
  }

  /** The current editor answers a request: hand over now, or keep editing. */
  async respond(requestId, handOver) {
    const r = await this.api.post(`${this.path}/takeover/${requestId}/respond`, { handOver });
    if (r.handedOver) {
      this._stop();
      this._set({ youHoldIt: false, token: null, pendingRequest: null, message: "You handed over editing." });
    } else {
      this._set({ pendingRequest: null });
    }
    return r;
  }

  /** An owner or administrator takes the lock whoever holds it. */
  async force() {
    const r = await this.api.post(`${this.path}/force`);
    if (r.fencingToken) {
      this._set({ youHoldIt: true, token: r.fencingToken, lost: false, message: "" });
      this._start();
    } else {
      await this.refresh();
    }
    return r;
  }

  _start() {
    this._stop();
    this._timer = this._setInterval(() => this.beat(), this.heartbeatMs);
  }

  _stop() {
    if (this._timer) this._clearInterval(this._timer);
    this._timer = null;
  }

  /** One heartbeat. Exposed so tests and "I am still here" interactions can call it directly. */
  async beat() {
    if (this._busy || !this.state.youHoldIt) return;
    this._busy = true;
    try {
      const r = await this.api.post(`${this.path}/heartbeat`);
      if (!r.stillHolder) {
        this._stop();
        this._set({ youHoldIt: false, token: null, lost: true, message: r.message || "You no longer hold the editing lock.", pendingRequest: null });
      } else if ((r.pendingRequest?.id ?? null) !== (this.state.pendingRequest?.id ?? null)) {
        this._set({ pendingRequest: r.pendingRequest });
      }
    } catch (e) {
      // A missed beat is not fatal; the next one may get through. A refusal that says the lock is gone is.
      if (e?.isLockLost) {
        this._stop();
        this._set({ youHoldIt: false, token: null, lost: true, message: e.message });
      }
    } finally {
      this._busy = false;
    }
  }

  /** Called when any edit comes back "lock-lost": the server has already moved on. */
  markLost(message) {
    this._stop();
    this._set({ youHoldIt: false, token: null, lost: true, message: message || "You no longer hold the editing lock." });
  }

  dispose() { this._stop(); }
}
