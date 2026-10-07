# W00: Python baseline и карта паритета

Эталон относится к Python checkout SHA `398095c13f334a91cc351e7fa62d5ceaab92e7ef` на 2026-10-05. JSON в `tests-contract/fixtures/legacy/` получен из чистых Python функций и `OptimizationCycle.persist`; генератор — `generate_baseline.py`. Пересоздание из корня проекта:

```powershell
$env:PYTHONPYCACHEPREFIX = Join-Path $PWD '.cache/pycache'
.\.cache\venv\Scripts\python.exe tests-contract/fixtures/legacy/generate_baseline.py
```

В матрице 117 расчетных случаев: полный cross-product tracker × role × environment × storage (108 случаев), границы скоростей 50/100/300/500 Mbps (8 случаев) и throttling с портом, закреплённым на `55000`. Выход включает все поля `OptimizedSettings`, предупреждения и объяснения. `settings-payloads.json` фиксирует фактический результат `recommended_preferences` для libtorrent 1.2 и 2.0 на синтетических типизированных схемах. Тестовый порт передаётся как конкретный `listen_port`, `random_port=false`.

`legacy-cycle.json` сохраняет старую структуру без `schemaVersion`, с baseline samples, plan, исходным snapshot, отсутствующим `applied` и обезличенным workload identity. `legacy-workload-and-ramp.json` содержит записи workload/catalog и ramp trace, полученные чистыми функциями; сетевой metadata fetch и взаимодействие с qBittorrent не выполняются. Endpoint — loopback fixture, hash/tag искусственные, путь заменён маркером. Credentials отсутствуют. Эти данные — Python эталон, не доказательство C# паритета.

| Python сценарий / исходная проверка | Будущий тест-ID и владелец | Что фиксируется |
| --- | --- | --- |
| `test_calculator.py`; полная матрица `calculator-matrix.json` | `CALC-GOLDEN-01` (W08) | Равенство всех числовых, enum, bool, warning и explanation полей; boundary cases и фиксированный порт |
| `recommended_preferences` в `test_optimization_cycle.py`; `settings-payloads.json` | `API-PREFS-01` (W09) | B/s conversion, enum ints, exact JSON types, random port, LT1-only keys, interface binding |
| `test_authentication.py` | `API-VERSION-01`, `API-AUTH-01` (W09) | Version gate, live schema before validation, no empty login, redaction, reconnect failure clears state |
| `test_optimization_cycle.py`; `legacy-cycle.json` | `CYCLE-IMPORT-01` (W14), `APPLY-RECOVERY-01` (W15) | Legacy import, backup before write, readback, rollback, partial/unknown apply, drift and duplicate-operation guards |
| `test_optimization_cycle.py` measurement scenarios | `MEASURE-VALIDITY-01` (W17), `MEASURE-SCOPE-01` (W16/W17) | Active traffic/workload, completion and scope guards, fixed sampling semantics, invalid runs do not become results |
| `test_ramp_metrics.py`; ramp section in `legacy-workload-and-ramp.json` | `RAMP-GOLDEN-01` (W17) | First traffic/seed/peer, sustained t50/t90, no synthetic times, unknown peer counts |
| `test_workload_catalog.py` | `WORKLOAD-CATALOG-01` (W16) | Decimal Mbps sizing, smallest sufficient image, image-switch boundaries, invalid speeds |
| `test_workload.py`; workload record fixture | `WORKLOAD-OWNERSHIP-01` (W16) | Exact endpoint/hash/tag ownership, duplicate/lost-response recovery, safe cancel/delete and moved-state recovery |
| `test_persistence.py` | `LEGACY-IMPORT-01`, `PERSIST-FAILURE-01` (W14) | No offline INI writes; legacy schema, malformed/oversized input, atomic failure and retained backup |
| `test_behavior_report.py` | `RESULTS-EVIDENCE-01` (W17/W20) | Scope-aware comparisons, unknowns remain unknown, observed versus causal claims |
| `test_hardware.py`, `test_process_manager.py` | `PLATFORM-OWNER-01` (W10/W11) | Exact local process/volume ownership, permission and remote-data limits |
| `test_network.py` | `NETWORK-MEASURE-01` (W16/W17) | Mbps unit conversion, cancellation, request overlap and retained test inputs |
| `test_ui_workflow.py` | `NAV-OFFLINE-01` (W22) | Keep navigation/history/theme/locale available while disconnected; keep mutation actions guarded |

Намеренное UX-изменение: старый `test_missing_target_locks_navigation_and_actions` переносить буквально нельзя. Новая проверка `NAV-OFFLINE-01` разрешает историю, настройки интерфейса и навигацию offline; Apply и измерения остаются запрещены без валидированной цели.

Обязательные Python pytest и schema checker запускает координатор как общий baseline. Эта задача не меняет production Python и не запускала C# сборки/тесты, сеть, реальный qBittorrent или benchmark. Сборка и производительность здесь не подтверждались.
