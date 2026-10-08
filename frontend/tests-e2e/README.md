# Browser QA runners

The standard Playwright Test suite runs with `pnpm run test:e2e:web` after loading `scripts/dev-env.ps1`. That environment disables Playwright 1.52's experimental TypeScript ESM loader, which hangs during discovery on Node 24 in this Windows environment. The additional manual scripts use the installed Playwright API directly. The frontend shell mock is labeled in-page and cannot connect to qBittorrent. The WebView runner checks for the isolated local shell and `Not connected` status before interacting.

Tools used: Node 24.19.0, pnpm 11.19.0, Vite 6.3.5, Playwright 1.52.0, Edge 154.0.4258.53.

## Production shell matrix

From `frontend/`, build the assets in the project cache:

```powershell
pnpm run build
```

Start the built preview from `frontend/` in one terminal:

```powershell
node ./node_modules/vite/bin/vite.js preview --outDir ../.cache/frontend --host 127.0.0.1 --port 4173 --strictPort
```

From the repository root in another terminal, run:

```powershell
node frontend/tests-e2e/manual-shell-qa.mjs
```

It checks six navigation sections across RU/EN and dark/light at 1200×840 and 960×680. It saves 24 screenshots to `.cache/e2e/screenshots/` and reports current semantic-token contrast plus keyboard focus and OS theme behavior.

## Component gallery

Start Vite development mode from `frontend/`:

```powershell
node ./node_modules/vite/bin/vite.js --host 127.0.0.1 --port 4174 --strictPort
```

Run from the repository root:

```powershell
node frontend/tests-e2e/manual-gallery-qa.mjs
```

This checks RU/EN, light/dark, input errors, tab keyboard behavior, native dialog focus/Escape, and forced-colors styles. It writes four screenshots to `.cache/e2e/gallery/` and a token contrast report to `.cache/e2e/gallery-qa.log`.

## Native WebView2 shell

Verify the loopback port is unused before launching the staging candidate from the repository root:

```powershell
if (Get-NetTCPConnection -LocalPort 53047 -State Listen -ErrorAction SilentlyContinue) { throw 'Port is occupied' }
$exe = (Resolve-Path '.cache/staging/qFrey-Tuner.exe').Path
$webviewProcess = Start-Process -FilePath $exe -ArgumentList '--webview-test-port=53047' -WorkingDirectory (Get-Location).Path -WindowStyle Hidden -PassThru
```

Run from the repository root:

```powershell
node frontend/tests-e2e/manual-webview-qa.mjs 53047
```

The host flag is test-only, binds WebView debugging to loopback, and creates a unique `.cache/tests/webview/<id>` profile. The runner connects only to that port, requires `https://qfrey.local/index.html` and `Not connected`, then checks the same six-by-two-by-two matrix. It closes the WebView with `window.close()` after capture; the current source wires `WindowCloseRequested` to the WPF window close handler. Confirm graceful exit of the exact `$webviewProcess` process:

```powershell
$webviewProcess.Refresh()
if (-not $webviewProcess.HasExited) { $closed = $webviewProcess.CloseMainWindow(); if ($closed) { $webviewProcess.WaitForExit(10000) } }
$webviewProcess.Refresh()
"PID=$($webviewProcess.Id) exited=$($webviewProcess.HasExited)"
```

Never force-kill the host. If the port closes but the owned process remains, record its PID and stop before replacing the candidate executable.

## Evidence

Machine-readable logs and screenshots are kept in `.cache/e2e/`. The Playwright Test CLI diagnosis is in `manual-checks.md`; native WebView screenshots are under `.cache/e2e/webview/`. Browser emulation is not a Windows DPI test, and these checks do not accept W05/W06 in full or claim W22 completion.

## Connection and preview continuation

Use the production bundle with `vite preview`; strict release CSP blocks Vite's injected development CSS, so a dev-server capture cannot prove visual styling. From frontend directory: `node node_modules/vite/bin/vite.js preview --host 127.0.0.1 --port 4173 --strictPort --outDir ../.cache/frontend`. Then from repository root:

```powershell
. ./scripts/dev-env.ps1
$env:QFREY_QA_BROWSER = 'chrome'
node frontend/tests-e2e/manual-connection-qa.mjs
```

This is mock bridge UI evidence, not API integration. It checks precise draft values through locale changes, preview, manual override rebuilding, unknown/error values, disconnect, credential clearing and four language/theme captures for both connection and preview. Edge launch failure must be reported separately, not counted as a pass through Chrome.

After the normal staging host launch above, `node frontend/tests-e2e/manual-webview-qa.mjs 53047 --owned-mock-connect` adds actual bridge/API integration against only a child `tests-dotnet/QFrey.NativeHelper` mock executable on its own ephemeral loopback port. The runner owns the child, requests graceful stdin quit and verifies its exit. No ordinary qBittorrent endpoint/profile/download is used. It verifies live local process readings and a backend-generated preview plan, then captures RU/EN dark/light. Native args/cwd recovery remains blocked on OS builds outside its verified layout gate; this runner does not prove graceful real-qB lifecycle.

## Legacy backup restore browser QA

Use the built production preview described above. From the repository root:

```powershell
. ./scripts/dev-env.ps1
$env:QFREY_QA_BROWSER = 'chrome' # or 'msedge'; defaults to msedge
node frontend/tests-e2e/manual-restore-browser-qa.mjs http://127.0.0.1:4173/
```

The optional URL argument must identify a local production/preview origin; its default is `http://127.0.0.1:4173/`. The explicit, labeled mock bridge checks RU/EN and dark/light at 1200×840 and 960×680: backup review, conflicts, exact confirmation revision/session/action/expiry, cancellation, stale-dialog dismissal, and an accepted restore command with exact selection/confirmation tokens. It also checks dialog centering and table spacing. JSON command evidence and 32 screenshots are saved under `.cache/e2e/restore-browser/` (`report.json`). Network requests outside the preview origin are blocked. This is frontend mock-only evidence: no real file picker, qBittorrent, downloads, backup persistence or backend readback is tested.
