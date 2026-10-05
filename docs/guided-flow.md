# Guided tuning flow

The app now separates connection and input review from testing, proposed changes, and measured results:

Connect → Network → Hardware → Your goals → Speed test → Review changes → Results.

- Input pages scroll and explain what each group affects.
- Ubuntu download approval starts a traffic wait and then the before test. A stopped, completed, missing, or inactive download produces an actionable error. Cancelling a measurement does not stop the torrent.
- Review changes presents current and proposed values in separate cards with everyday labels. Applying remains blocked without a baseline and a reviewed plan.
- Apply saves original values before writing, reads them back to check the settings, then starts the after test automatically. The user still approves the settings before this operation.
- Results contains measured download and upload bars with actual MiB/s values and the existing uncertainty assessment. Results do not claim a guaranteed improvement.
- Undo restores only this cycle's changed settings and verifies them. Torrent data is kept. Earlier-session recovery is available here too.
- Delete test download confirms removal of the owned Ubuntu torrent and its files through qBittorrent. Endpoint, hash, and ownership tag must match. Personal Ubuntu torrents are never adopted or deleted.

Navigation fixes stale CustomTkinter cleanup callbacks so a callback for an earlier page cannot hide the current page.

Validation: 29 targeted tests pass across UI workflow, test workload, and optimization-cycle guards. No live qBittorrent settings were changed and no actual torrent was downloaded during validation.

