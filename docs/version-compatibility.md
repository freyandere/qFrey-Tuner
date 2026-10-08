# Compatibility checks for future changes

## Sources and authority

Migration read-only torrent metadata checkpoint (2026-10-07): [tagged 5.1 serializer](https://github.com/qbittorrent/qBittorrent/blob/release-5.1.0/src/webui/api/serialize/serialize_torrent.cpp) confirms payload rates, connected seeds/leechers, progress, save path, total size and the `forcedMetaDL` state. Metadata download is a known state but is not counted as active payload transfer. `total_size` is returned to JavaScript as a decimal string rather than an unsafe large integer. These source checks do not substitute for the required real-client version matrix.

Use these official sources when adding or changing a qBittorrent feature:

1. [WebUI API index](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API), with the pages for [4.1–4.6](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-4.1%29) and [5.x](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29).
2. The **target release tag** in the [official qBittorrent repository](https://github.com/qbittorrent/qBittorrent): `src/webui/api/appcontroller.cpp` defines preference keys and JSON conversions; other endpoint controllers define their own contracts.
3. That tag's `src/base/bittorrent/session.h` and `sessionimpl.cpp` define enum values, units, libtorrent build guards and configuration keys. `src/base/settingsstorage.cpp` defines persistence. Examples: [API controller 5.2.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.0/src/webui/api/appcontroller.cpp), [session enums 5.1.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.1.0/src/base/bittorrent/session.h).
4. [libtorrent settings](https://www.libtorrent.org/reference-Settings.html), [tuning guide](https://www.libtorrent.org/tuning-ref.html), and [migration to 2.0](https://www.libtorrent.org/upgrade_to_2.0-ref.html) describe engine behavior. Match the engine version reported by the target.

Wiki prose/examples can lag behind releases. An observed mismatch must be resolved
against tagged implementation and live readback, then recorded in the
[schema audit](qbittorrent-schema-audit-ru.md). Do not use `master`, forums,
NotebookLM or preset advice as authority for a release's storage/API format.
Our calculator's heuristic values are not upstream guarantees of performance.

## Runtime gate

`QBittorrentClient.connect()` reads `/app/version`, `/app/webapiVersion` and
`/app/buildInfo`, parses all three versions, then reads the live preferences.
Only successful validation sets `connected=True` and unlocks tuning in the UI.

| Component | Accepted |
| --- | --- |
| qBittorrent | Stable 4.6.x, 5.0.x, 5.1.x, 5.2.x |
| Web API | Stable 2.8 or newer within major 2 |
| libtorrent | Parsed stable version within major 1 or 2, including a minor component |

Future qBittorrent minor branches, prerelease/development versions, malformed
versions and other API/engine majors are rejected. Patch releases within an
accepted branch still require the live schema/type checks. Acceptance is not
a claim of real-client testing for every patch or build.

The connection screen shows the reported three versions. Cycle JSON files
retain those versions alongside settings and measurements. Legacy cache and
coalescing controls are excluded for libtorrent 2; OS caching uses the two disk
I/O modes on both engine branches. Changes go through the API with backup and
verified readback; Tuner does not write the selected INI/CONF directly.

## Reproducible checks

### Migration implementation status (2026-10-06)

The unfinished C# adapter is in `src/QFrey.Core/Qbittorrent`. It reads the same
version endpoints and live preferences, strips credentials via an explicit
preference allowlist, rejects redirects and ambient proxies, isolates each
connection's cookies, and bounds serialized requests to 16 pending entries with
an 8-second cancellable deadline including queue wait. Stable post/local build
suffixes preserve the existing Python gate; future minor branches remain denied.
`Tuning/PreferenceMapping.cs` produces canonical API values, with both engine
branches checked against the preserved Python payload fixtures. Its atomic port
pair check additionally blocks a partial `listen_port`/`random_port` proposal.
Desktop connection/read-only telemetry and PlanBuilder preview are now dispatched.
TransferController `dl_info_speed`/`up_info_speed` are validated integer B/s session
values; `dht_nodes` is a session count. Missing/invalid fields are not zero.
The C# transport/coordinator now supports guarded apply, readback and rollback,
with a measured baseline required before applying a plan. Mock checks do not
prove real-client parity. The upstream checker accepts exported actual C# mapper
payloads as well as its default Python payloads. See
[migration progress](migration-v2/progress.md) for actual tests and remaining gates.

With the existing development dependencies installed:

```powershell
uv run --locked --extra dev python -m pytest tests/ -q
uv run --locked --extra dev python scripts/check_qbittorrent_schema.py
```

To check the actual C# mapper against the same pinned upstream tags, export its
verified golden payloads and pass that file to the existing checker:

```powershell
. ./scripts/dev-env.ps1
$env:QFREY_SCHEMA_EXPORT_PATH = Join-Path $PWD '.cache/tests/csharp-schema/payloads.json'
dotnet test tests-dotnet/QFrey.Tests/QFrey.Tests.csproj --no-restore --filter FullyQualifiedName~PreferenceMappingTests.CanonicalPayloadMatchesLegacyFixture
uv run --locked --extra dev python scripts/check_qbittorrent_schema.py --payloads $env:QFREY_SCHEMA_EXPORT_PATH
Remove-Item Env:QFREY_SCHEMA_EXPORT_PATH
```

The exporter writes only within the repository's `.cache`. Mapper regressions
also cover speed conversion from KiB/s to B/s, buffer units, enum values and
libtorrent-2 exclusions; the source checker alone verifies keys and JSON types.

The pytest suite is independent of external documentation/network services;
the HTTP integration fixture starts a temporary local server, not qBittorrent.
GUI tests need Windows/Tk. Relevant checks:

- `test_authentication.py`: accepted/rejected version matrix, failed reconnect,
  version detection, and no writes before validation.
- `test_optimization_cycle.py`: units/enums, libtorrent branch exclusions,
  typed live schema, guarded apply/readback, durable backup and rollback.
- `test_persistence.py`: no direct configuration writes.
- `test_calculator.py`, workload/ramp/report tests and `test_ui_workflow.py`:
  generated values, measured observations and UI workflow.

The separate source checker reads pinned official release **tags**, currently
4.6.0, 4.6.7, 5.0.0, 5.1.0 and 5.2.0. It checks generated preference names against
both read/write paths and accepted JSON conversions, including optional port
and interface fields and both libtorrent branches. A dropped optional key must
fail the check instead of silently disappearing through live-schema filtering.
It does not execute C++, prove enum/units semantics, or run qBittorrent. Those
need the documented source review and targeted regression tests. Network errors
fail this check visibly; they are not reported as a schema match.

Run both checks locally before publishing a release. GitHub Actions are not used.
No test modifies a user's real client.

## Adding a parameter or supported branch

1. Review the exact official release's read/write endpoints, units, enum values,
   build guards and configuration mapping. Update the audit and source links.
2. Reuse the existing API adapter and schema filter. Include changed fields in
   the safe preference snapshot, backup/readback and rollback tests. Document
   deliberate omissions rather than claiming application or performance.
3. Add regression inputs that fail for a wrong key/type/unit/enum or unsupported
   build. For a new minor branch, add its tag to `RELEASES` in the source checker,
   update the runtime version gate, and test both acceptance and rejection.
4. Run both commands above. For a real-client validation, use a separate test
   profile and verify preferences after apply, rollback and graceful restart.
   Record qBittorrent/API/libtorrent versions and the profile/platform. Such a
   manual integration run is distinct from source compatibility and benchmark
   speed claims.
