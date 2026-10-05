# Verified optimization cycle

## Authority and schema

All settings and statistics use one authenticated `QBittorrentClient` endpoint. The API reports application/API/libtorrent versions. A known API key allowlist plus the live preference response determines compatibility; the application never infers effective preferences from a guessed INI schema.

Source references:

- [Official Web API documentation](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-5.0))
- [Application API implementation](https://github.com/qbittorrent/qBittorrent/blob/master/src/webui/api/appcontroller.cpp)
- [Session settings implementation](https://github.com/qbittorrent/qBittorrent/blob/master/src/base/bittorrent/sessionimpl.cpp)

API speed-limit values are bytes/second, as implemented by the API controller and session accessors. Send-buffer watermarks use KiB. Benchmark throughput is reported in MiB/s. Documentation examples and prose have historically been inconsistent about speed-limit units; implementation and readback are the authority here.

Internet speeds use decimal Mbps. The calculator converts the 80% upload recommendation to KiB/s before the API adapter converts to bytes/s. For 100 Mbps upload, the result is 9,765 KiB/s (9,999,360 bytes/s), without the previous decimal/binary overstatement.

## State transitions

```text
Disconnected -> Validated target -> Valid baseline -> Reviewed plan
             -> Durable original-value backup -> Apply -> Readback verified
             -> Valid after measurement -> Comparison -> Keep or verified rollback
```

The guided controller confirms Network, Hardware and Usage before calculating. A separate recommendations-only preview is allowed without a baseline; it does not unlock Apply or claim measured gains. A valid baseline is required for mutation in the measured cycle. Input edits invalidate an unapplied preview. Target preference drift invalidates the baseline/plan. Unsupported required keys, unknown versions/builds, and absent VPN interfaces block mutation. An apply failure preserves original values and does not unlock the after measurement.

API write readback confirms qBittorrent's effective preference state. It cannot prove every underlying libtorrent behavior or VPN failure condition. Known obsolete libtorrent-2 cache settings are not proposed. Optional unsupported settings are shown as omissions; super seeding is not applied globally.

## Measurement

Default run: 10-second warm-up, then 60 samples, nominally one second apart. Sampling uses monotonic time in a worker thread. Cancellation, malformed/error responses, schedule slippage, alternative/scheduled speed limits, all-zero transfer activity, or different active torrent sets invalidate the run. Relevant preferences must remain unchanged during each run.

Results contain raw samples, means, standard deviations, relevant preference snapshots, versions, and workload context. A conservative noise/5% threshold labels observations as improvement, regression, or inconclusive. The heuristic threshold is not a significance test: samples may be correlated and peer/cache conditions vary. Repeat trials with comparable workloads.

No hard-coded ISO magnet is added. Start the intended workload in qBittorrent yourself; the tuner does not silently create or delete torrents.

## Backup and rollback

Each cycle is stored as an atomically replaced JSON file. Original values are flushed to disk before the settings mutation. Backup files contain only a curated settings set and experiment metadata, not credentials or cookies. After application restart, choose the saved cycle on Setup to restore its original values. Endpoint/version and setting types must match. Values modified outside the cycle block rollback to avoid overwriting unrelated edits.

## Process lifecycle

`psutil` discovers accessible local qBittorrent processes and resolves the owner of the configured loopback Web UI port. Graceful stop uses `/app/shutdown`, then waits up to 30 seconds for that exact process. Restart reuses its original command and working directory. A timeout never causes a force kill or a duplicate restart. Nonlocal endpoints and ambiguous/inaccessible ownership block lifecycle actions.

Start accepts a manually selected qBittorrent executable only when no accessible existing qBittorrent process is found. Custom profiles should be launched using their existing shortcut or service command; start does not synthesize profile arguments.

## Verification boundary

Automated tests use a local HTTP API fixture and isolated GUI windows. A real-client end-to-end performance claim requires an actual installation, credentials, valid workload, and measured before/after trials. No test applies settings to the user's real client. Runtime validation is therefore mandatory every time a target is connected.
