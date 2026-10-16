// Chunked, resumable, hash-verified upload of one drawing file (plan section 6.8). The file is cut into the pieces the
// server asks for, each piece carries its own SHA-256 so a damaged one is caught at once, and the whole file's hash is
// sent when it is small enough to compute in the browser. The server holds how much it has, so a piece that fails is
// asked about and resumed, not started over. Nothing here touches the DOM.

const WHOLE_FILE_HASH_LIMIT = 128 * 1024 * 1024;

export async function sha256Hex(data) {
  const buffer = data instanceof ArrayBuffer ? data : await data.arrayBuffer();
  const digest = await (globalThis.crypto || window.crypto).subtle.digest("SHA-256", buffer);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export class UploadCancelled extends Error {
  constructor() { super("The upload was cancelled."); this.name = "UploadCancelled"; }
}

export class Upload {
  /**
   * @param api        the Ductwork API client
   * @param jobId      the job
   * @param file       a File (or anything with name, size, slice, arrayBuffer)
   * @param onProgress ({ sent, total, state }) => void
   * @param retryDelaysMs how long to wait before each retry of a piece
   */
  constructor({ api, jobId, file, onProgress = () => {}, retryDelaysMs = [500, 1500, 4000, 8000], hashFile = true }) {
    this.api = api;
    this.jobId = jobId;
    this.file = file;
    this.onProgress = onProgress;
    this.retryDelaysMs = retryDelaysMs;
    this.hashFile = hashFile;
    this.cancelled = false;
    this.uploadId = null;
    this.sent = 0;
  }

  get base() { return `/jobs/${this.jobId}/uploads`; }

  cancel() {
    this.cancelled = true;
    if (this.uploadId) this.api.del(`${this.base}/${this.uploadId}`).catch(() => {});
  }

  _report(state) { this.onProgress({ sent: this.sent, total: this.file.size, state }); }

  _check() { if (this.cancelled) throw new UploadCancelled(); }

  /** Sends the whole file and returns the stored file's record. */
  async run() {
    this._report("starting");
    let wholeHash = null;
    if (this.hashFile && this.file.size <= WHOLE_FILE_HASH_LIMIT) {
      this._report("checking");
      wholeHash = await sha256Hex(this.file);
    }

    this._check();
    const begun = await this.api.post(this.base, { fileName: this.file.name, totalBytes: this.file.size, sha256: wholeHash });
    this.uploadId = begun.uploadId;
    const chunk = begun.chunkBytes;
    this.sent = begun.receivedBytes || 0;

    while (this.sent < this.file.size) {
      this._check();
      this._report("sending");
      await this._sendPiece(chunk);
    }

    this._check();
    this._report("finishing");
    const file = await this.api.post(`${this.base}/${this.uploadId}/complete`);
    this.sent = this.file.size;
    this._report("done");
    return file;
  }

  async _sendPiece(chunkBytes) {
    const offset = this.sent;
    const end = Math.min(this.file.size, offset + chunkBytes);
    const blob = this.file.slice(offset, end);
    const hash = await sha256Hex(blob);

    for (let attempt = 0; ; attempt++) {
      this._check();
      try {
        const r = await this.api.request("PUT", `${this.base}/${this.uploadId}?offset=${offset}`, {
          rawBody: blob, headers: { "Content-Type": "application/octet-stream", "X-Chunk-Sha256": hash }, fencing: null
        });
        this.sent = r.receivedBytes;
        return;
      } catch (e) {
        if (e.status === 409 && e.code === "offset-mismatch") {
          // The server is somewhere else: carry on from where it says, which may be past this piece if a retry had landed.
          this.sent = e.body?.expectedOffset ?? offset;
          return;
        }

        const transient = e.code === "network" || e.status === 408 || e.status === 429 || e.status >= 500 || e.code === "checksum" || e.code === "busy";
        if (!transient || attempt >= this.retryDelaysMs.length) throw e;
        this._report("retrying");
        await sleep(this.retryDelaysMs[attempt]);
        // After a dropped connection the piece may or may not have landed. Ask, so the next try starts in the right place.
        try {
          const status = await this.api.get(`${this.base}/${this.uploadId}`);
          if (status.receivedBytes !== offset) { this.sent = status.receivedBytes; return; }
        } catch { /* try the piece again */ }
      }
    }
  }
}
