# Publishing AIToolbox to chromeextensions.stdair.com

## Extension identity

- **Extension ID: `lmhmnihpflkpmaojclcnjodbggfndoka`**
- Signing key: `aitoolbox-extension.pem` (RSA 2048, generated once via `openssl genrsa`), kept in this folder, **gitignored — never commit it**. Deliberately kept here rather than inside `AIToolbox.ChromeExtension/` — that folder is what Chrome's "Load unpacked" loads, and Chrome flags any `.pem` it finds anywhere in that tree as a bundled private key.

Chrome derives the extension's ID from this key's public half. **The key must never be regenerated.** Doing so would silently mint a *different* extension ID and break auto-update for every copy already installed corporate-wide. Back this file up somewhere durable outside of this one machine (wherever this org keeps other long-lived credential material) — if it's ever lost, every installed copy of AIToolbox has to be manually reinstalled under a new ID.

## How a publish works

1. `Pack-Extension.ps1` (in this folder):
   - Reads the current version from `manifest.json` (does **not** bump it — bump `manifest.json`/`package.json` together yourself first, same as always).
   - Runs `npm ci && npm run build`.
   - Stages a copy of everything "Load unpacked" uses (minus dev-only files) and packs it into a `.crx` via an installed Chrome/Edge's `--pack-extension` CLI, signed with `aitoolbox-extension.pem`.
   - Regenerates `aitoolbox_update_manifest.xml` from scratch with the new version — this file is exclusively AIToolbox's own; it never touches the Dayforce site's existing `update_manifest.xml`.
   - Writes both to `output/` (in this same folder).
2. The `AIToolbox.ChromeExtension.Publish` project (added to the solution) runs this script automatically as part of `dotnet publish` / Visual Studio's Publish, and pushes only those 2 files to `chromeextensions.stdair.com` via the `ChromeExtensionsSite` Web Deploy profile.

## The one rule that keeps the Dayforce extension safe

The Web Deploy profile must **never** have "Remove additional files at destination" enabled. Left at its default (off), a publish can only add/update `aitoolbox.crx` and `aitoolbox_update_manifest.xml` — it can never delete the site's existing `update_manifest.xml`, `web.config`, `redirect-dayforcetimeclock.html`, or `dayforcehcm.com_timeclockautologin.crx`, even by mistake.

## Group Policy

`ExtensionInstallForcelist`/`ExtensionSettings` are per-extension-ID entries, not a single global manifest reference. Add AIToolbox as its own entry alongside the existing Dayforce one:

```
lmhmnihpflkpmaojclcnjodbggfndoka;https://chromeextensions.stdair.com/aitoolbox_update_manifest.xml
```

The Dayforce entry (`fjfejncbjafkpjfoilecjglncngdligf;https://chromeextensions.stdair.com/update_manifest.xml`) stays exactly as it is.
