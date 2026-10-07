# Задачи для координатора и экономичных субагентов

Все задачи ниже пока **TODO**. Выполнены только исходная оценка среды и проверки Python, записанные в README/verification. Нельзя отмечать задачу выполненной по наличию этого плана.

## 1. Правила исполнения

Один агент — одна ограниченная задача с собственными файлами и тестами. Не давать дешёвому исполнителю задание «перепиши backend целиком». Если задача требует самостоятельного изменения контракта, она возвращается координатору с конкретным предложением, а не реализуется по догадке.

Координатор держит актуальные contracts, integration build и список состояния задач. У каждой задачи: pending/running/review/accepted/blocked, commit или список файлов, exact commands, test result и известные limitations. Progress status не заменяет pass/fail. Никаких команд на пользовательском реальном qBittorrent в автоматических тестах.

Профили исполнителей:

- **C — координатор:** архитектура, общие DTO, зависимости, gate, релиз. Не обязательно пишет каждый экран.
- **B — экономичный backend исполнитель:** перенос чистых функций и маленьких модулей по fixtures.
- **F — экономичный frontend исполнитель:** экраны/карточки по DTO и tokens, без изменения backend guards.
- **T — экономичный QA исполнитель:** независимые сценарии по контракту, не тесты, повторяющие реализацию строка в строку.
- **R — сильный reviewer:** security/data-loss/concurrency/native boundaries. Может совпадать с координатором, но выполняет отдельный review-pass.

При доступных четырёх слотах: C + до трёх исполнителей. Не заполнять слоты ради параллелизма; задачи с общими файлами выполняются последовательно. Модели выбираются при запуске; названия, стоимость и доступность не зафиксированы в этом плане.

## 2. Владение общими файлами

| Зона | Единственный владелец |
| --- | --- |
| AGENTS.md, global.json, .csproj, lockfiles, package.json, Vite/TS configs | C |
| Core/Contracts, frontend/contracts, protocol fixtures | C, задачи W02/W03 |
| App shell, root navigation, state reducer | F-shell, передача владения явно |
| frontend/i18n dictionaries | F-i18n; screens передают список новых keys |
| styles tokens, base components | F-design |
| scripts/build.py, build.bat, Run.bat, spec, release tools | C-build |
| Тесты отдельного модуля | Исполнитель модуля до review, затем T |
| Сквозные tests-e2e | T-e2e |

Shared checkout: разрешённые пути обязательны; координатор применяет изменения общих файлов. Если используются worktrees, каждый указывает уникальные `.cache` и output locations внутри своего workspace; нельзя собирать два кандидата в общий release. Создание worktree не переносит незакоммиченные документы автоматически — сначала обеспечить доступность plan commit/файлов. Не git reset/clean чужие изменения.

## 3. Граф работ

| ID | Работа | Зависит от | Профиль | Gate |
| --- | --- | --- | --- | --- |
| W00 | Эталон и перенос regression fixtures | — | C/T | G0 |
| W01 | SDK и воспроизводимый toolchain | — | C | G0 |
| W02 | DTO/state/command/error контракт | W00 | C/R | G1 |
| W03 | Contract fixtures и conformance tests | W02,W01 | T/C | G1 |
| W04 | WPF/WebView2 shell и ранняя упаковка | W01,W02 | C/R | G1 |
| W05 | Tokens/base components/themes | W01,W02 | F | G1 |
| W06 | RU/EN + formatters + native strings | W02,W05 | F | G1 |
| W07 | Secure bridge и UI snapshot state | W03,W04 | C/R | G1 |
| W08 | C# models/calculator | W00,W03 | B | G2 |
| W09 | API authentication/version/schema | W00,W03 | B/R | G2 |
| W10 | Windows hardware/owner/lifecycle | W01,W03,W09 | B/R | G2/G3 |
| W11 | Live collector + process telemetry | W09,W10 | B/R | G2 |
| W12 | Overview/Setup UI | W05,W06,W07,W08,W11 | F | G2 |
| W13 | Plan builder + advanced validation | W08,W09,W03 | B/R | G3 |
| W14 | Durable persistence + legacy import | W00,W03 | B/R | G3 |
| W15 | Mutation coordinator + apply/rollback | W07,W09,W13,W14 | C/R | G3 |
| W16 | Test workload/metadata ownership | W09,W14,W15 | B/R | G3 |
| W17 | Measurement/analysis/report DTO | W11,W14,W15,W16 | B/R | G3 |
| W18 | Recommendations UI | W05,W06,W07,W13,W15 | F | G4 |
| W19 | Experiment UI | W05,W06,W07,W16,W17 | F | G4 |
| W20 | Results/history/export | W06,W07,W14,W17 | F/B | G4 |
| W21 | Fault/concurrency integration tests | W15,W16,W17 | T/R | G4 |
| W22 | Browser/WebView2 visual/accessibility QA | W12,W18,W19,W20 | T/F | G4 |
| W23 | Performance/load/live validation | W21,W22 | T/R | G5 |
| W24 | Final packaging/compat docs/parity signoff | W23 | C/R | G5 |

Практические волны: W00+W01; W02 затем W03/W04/W05; W06/W07/W08; W09/W13/W14 с соблюдением зависимостей; W10/W15; W11/W16/W18; W12/W17; W19/W20/W21; W22 → W23 → W24. Внутри волны зависимость из таблицы имеет приоритет. Mock UI разрешено разрабатывать раньше интеграции, но принять задачу можно только после её обязательных зависимостей.

## 4. Карточки заданий

### W00 — эталон

Прочитать optimizer, tests, docs/version-compatibility.md, qbittorrent-schema-audit-ru.md, build-layout-design.md. Изменять только tests-contract/fixtures/legacy и docs/migration-v2/verification.md. Сохранить обезличенные fixtures inputs/output, settings payload, old cycle/workload; для random port фиксировать value. Создать карту Python scenario → будущий test ID; отдельно пометить намеренные UX-изменения (свободная навигация вместо старого gate).

Приёмка: текущие pytest и checker проходят, fixtures не содержат credentials/реальные hash/path пользователя, baseline SHA записан. Не изменять production Python, чтобы эталон стал зелёным. Реальные perf numbers заполнять только после измерений.

### W01 — SDK и toolchain

Владелец root configs и зависимостей. Установить .NET 10 SDK в разрешённую локальную tools область или использовать найденный SDK; закрепить точную версию. Подготовить pnpm, React/TS/Vite/Tailwind, xUnit/Vitest/Playwright, NuGet locked restore, frontend frozen lockfile. Никаких глобальных случайных npm install.

Настроить весь obj/bin/publish/frontend build/test outputs в .cache, NuGet и pnpm cache тоже туда. Учесть project-specific obj subpaths, чтобы MSBuild-generated files проектов не сталкивались. Пользовательский runtime profile — LocalAppData, тестовый — .cache/tests/webview/<run-id>.

Приёмка: новый пустой Core/Tests build проходит в локальной среде, повторная установка по lockfile не меняет tracked files. Только C добавляет зависимости; screen agents не правят package.json.

### W02 — контракт

Владелец Contracts и их TS-зеркала. Из contracts-and-metrics.md вывести C# records/enums и TS discriminated unions. Уточнить payload каждой команды, полный AppSnapshot и error code set. C# nullable enabled, TypeScript strict; запрет any на bridge и object blobs в domain.

Приёмка: все 25 будущих модулей используют одни enum/units; historical values отделены от live freshness; manual override имеет ranges/group dependencies. Contract version 1 и schemaVersion 2 зафиксированы. R проверяет мутации, stale revision, target identity и secrets до W03.

### W03 — contract fixtures

Владелец tests-contract/fixtures/protocol и conformance tests. Пары: initialize, normal/stale telemetry, plan, accepted operation, appliedVerified, unverified, cancelled, historical result, invalid message. Каждую положительную fixture принимает C# и TS; отрицательные отвергаются на runtime boundary.

Приёмка: wrong enum, bool вместо int, NaN/Infinity, excess command size, unknown mutation fields, missing sessionId и unsupported protocol version отклоняются; serialization roundtrip стабилен. Не строить отдельный универсальный code generator/schema framework.

### W04 — shell и packaging spike

Владелец Desktop App/MainWindow и build orchestration по согласованию C-build. Один WPF WebView2, local assets, runtime detection, native missing-runtime dialog RU/EN, independent state/userData dirs. Single-file win-x64 self-contained гипотезу проверить сейчас. При извлечении embedded web assets — version/hash-scoped cache, без перезаписи активных files и без включения secrets.

Добавить ранние `--check`/`--smoke-test` modes без обращения к real qBittorrent. Сборка только scripts/build.py, экспериментальный candidate остаётся staging. Если нужен новый `--candidate-only` режим — добавить в этот модуль, покрыть тестом; отдельный упаковщик запрещён.

Приёмка: реальное окно загружает bundled page offline, resources найдены после запуска из пути с пробелами/кириллицей; нет developer server dependency. Два release assets технически возможны; runtime не найден → корректный message/exit. До успеха G1 не раздавать остальную миграцию массово.

### W05 — дизайн-система

Владелец styles, base Button/Field/Card/Badge/Dialog/Tabs/StatusBanner. Реализовать semantic tokens, dark/light/system, responsive shell, focus, reduced motion, high contrast, graph tokens. Иконки — малый набор доступных SVG, без шрифта с CDN.

Приёмка: gallery/story screen на mock fixtures показывает все base states в обеих темах, keyboard focus виден, есть contrast report. Эта test gallery не является ещё одним продуктовым экраном и не входит в release navigation.

### W06 — i18n

Владелец i18n и native startup translations. Типизированные en/ru keys, placeholder parity, Intl number/date/plural formatters, locale-aware numeric input. Перевести error/confirmation catalog, доступные labels и result texts, а не только меню.

Приёмка: нет missing translations; русский 1/2/5/11/21/22 проверен; comma/dot input нормализуется без двусмысленности; locale switch во время mock measurement сохраняет draft/session. Все новые keys принимаются через этого владельца.

### W07 — bridge

Владелец Desktop/Bridge, frontend/bridge и frontend/state. Origin allowlist, блокировка navigation/frames, точечный dispatcher, read-only snapshot subscription, request IDs/operation IDs/revision, stale event rejection, safe error mapping. Никаких arbitrary execute/file/http methods.

Приёмка: malformed/cross-origin message не выполняет операцию; duplicate apply mock выполняется один раз; event другой session игнорируется; полный snapshot восстанавливает UI после reload. Test CDP включается только явным локальным test launch, без постоянного debug port в release.

### W08 — calculator

Владелец Core/Tuning calculator и models, соответствующих unit tests. Перенести формулы/границы из calculator.py, units из models.py. Алгоритм сначала идентичен, объяснения перевести в keys/params. Random port вынести как зафиксированный вход плана, чтобы повторная сериализация не меняла значение.

Приёмка: matrix public/private × роли × среды × storage + boundary values скоростей даёт числовой паритет Python. Не «улучшать» эвристики в процессе переноса без отдельной задачи/fixtures.

### W09 — API

Владелец Core/Qbittorrent и tests. Перенести session isolation, Referer, API key/password/bypass, no empty login, timeouts, TLS validation и no redirects. Version gate идентичен, live types проверяются, новые telemetry fields документируются по pinned tags. Safe wrappers не разрешают записи до coordinator validation.

Приёмка: version matrix и localhost HTTP mock tests, secret-redaction, auth reconnect isolation, timeout/error categories. Проверить transport payload setPreferences form/json и реальные enum/units на fixtures. No ambient proxy credentials; публичный downloader не получает API cookies.

### W10 — Windows integration

Владелец Desktop/Platform. RAM/CPU/core/storage detection, owner PID по localhost listening port и lifetime, executable/cmdline/cwd recovery. Не заменять точное владение выбором первого qbittorrent.exe. Ручной fallback при permissions/UNC/unknown. Lifecycle методами Web API с ожиданием exact process exit, не TerminateProcess.

Приёмка: remote/ambiguous owner/access denied/PID reuse/selected volume/timeout fixtures; graceful restart сохраняет args+cwd; невозможность восстановить args блокирует restart. Не включать admin mode для всей оболочки.

### W11 — telemetry

Владелец Core/Metrics collector и Desktop/Platform local process metrics; если W10 ещё редактирует ту же папку — отдельные файлы и явная передача. Общий collector, clock abstraction только там, где нужна deterministic проверка, 600-point ring, latest UI coalescing и отдельный lossless measurement consumer.

Приёмка: 1 Hz/2 Hz расписание, stale threshold, no overlap, bounded queue, exact scopes, missing !=0, dropped UI point не теряется в measurement. Remote CPU unavailable. После переключения target нет данных старой session. Bench и dashboard не удваивают API polling.

### W12 — Overview и Setup

Владелец только соответствующих screens и screen tests. Использовать готовые components/contracts/i18n. Connection states, KPI, live chart, source-labelled inputs, manual fallback, draft semantics, next action.

Приёмка: disconnected/offline/stale/remote/hardware unknown snapshots корректны в RU/EN/dark/light; changing draft не вызывает mutation; full precision measured speed сохраняется. Endpoint edit не сохраняет прежнюю validated identity.

### W13 — plan builder и advanced

Владелец Core/Tuning PlanBuilder и tests. Diff current/proposed, stable IDs, selection groups, editable ranges, omissions, valid interface resolution, baseline/inputs fingerprint, immutable revision. Уточнить difference между plan created и plan approved.

Приёмка: optional unavailable показан как omission, required unavailable блокирует; unsupported VPN не исчезает; port/random flag атомарны; invalid override отвергается backend; no effective changes не создаёт Apply. Approval нового revision обязателен.

### W14 — persistence

Владелец Core/Persistence и fixture tests. Atomic durable write, restore read limits/schema/type gates, legacy adapter, setup prefs, cycle history. Использовать trusted root/file chooser tokens, не произвольные пути bridge.

Приёмка: write failure, locked file, corrupt/truncated/future-version JSON, incompatible target, absent optional historical fields, preserved original backup. Crash points до/после flush/replace не создают «успешную» пустую копию. Секреты не сохраняются.

### W15 — coordinator

Владелец Core/Tuning Coordinator/OperationGate и tests. Guards/state transitions, bounded tasks, apply/readback/rollback, conflicts, reconcile, KeepChanges. Применение строго после durable backup. Cancellation semantics прописаны в контрактах.

Приёмка: все state transition/fault scenarios из verification; after не начинается после unverified; failed after не притворяется rollback; rollback не затирает внешние изменения; double click и stale revision не выполняют повторный POST. R review обязателен перед real-client stage.

### W16 — workload

Владелец Core/Workloads и tests. Catalogue, safe metadata fetch, bounded bencode, exact info-slice hash, size, source, unique tag, recovery record, duplicate detection, prepare/wait/stop/delete/restart. Сохранить ограничение 2,000,000 bytes и depth 16 либо ужесточить с regression evidence, не ослаблять молча.

Приёмка: HTTPS downgrade/redirect loop/truncated metadata/hash mismatch/oversize/personal torrent/dropped add response тесты. Delete требует всех доказательств владения; cancelled measurement не вызывает Stop/Delete. Новый image/size требует нового согласия.

### W17 — measurement и analysis

Владелец Core/Metrics Measurement/Analysis и report DTO mapper. Прогрев/расписание/валидность как Python, same samples для dashboard и analysis, stats/ramp/behavior parity, honest outcome typing. Сохранить 10s+60 defaults, missing and alternative limits invalidate.

Приёмка: golden series, correlated-noise caveat, noBaselineTraffic, changes in active set/settings, all zero, completed download, schedule slip, stale sample. Не добавлять causal claim/AI score. Before/after scope и method version проверяются.

### W18 — Recommendations

Владелец screens/Recommendations и RecommendationCard. Category/filter/card/table, detailed explanations, selection/overrides, preview-only, exact review/apply confirmation.

Приёмка: displayed values совпадают с canonical plan с корректным unit conversion; disabled Apply имеет actionable reason; изменения выбора требуют нового approval. Словари пополняет W06 owner, не hardcoded JSX strings.

### W19 — Experiment

Владелец screens/Experiment и operation bar. Guided stages, workload choices/consent, progress and curves, cancellation, failure/recovery links. Navigation остаётся доступна.

Приёмка: cancel measurement не удаляет/останавливает торрент; повторный download запрашивается по условиям; stale events не завершают новый test; error stage не стирает последнюю достоверную информацию.

### W20 — Results, History и export

Разбить между F и B последовательно: сначала typed history/export backend, затем screens. Владелец B — Core/Persistence History/Exporter; F — screens Results/History и typed cards. HTML encode всех user/remote strings, local bundled styling, JSON stable codes/units.

Приёмка: категория и evidence верны; no percent при baseline zero; comparison scopes не смешиваются; исторические результаты после undo подписаны; export не содержит secrets и не выполняет HTML из torrent name. Restore всегда через guarded coordinator. В history список paginated, не загрузка всех series в DOM.

### W21 — fault integration

Владелец tests-dotnet integration suite/mock API. Использовать таблицу failures из verification, управляемые delays/barriers и synthetic clock. Проверять взаимодействия, а не копировать внутренние private methods.

Приёмка: нет unhandled Task errors, pending mutation всегда имеет recovery artifact, отсутствуют лишние POST и blind retries, queues bounded. Любое найденное исправление направить владельцу production module; test agent не редактирует все модули параллельно с ними.

### W22 — UI QA

Владелец tests-e2e и QA evidence. Playwright browser fixtures + real WebView2 integration, computer-use для окна/native dialogs. Проверить обе темы/языка и DPI, keyboard focus, translations, accessible chart table, stale/error states.

Приёмка: приложены screenshots/report по verification, не только `test passed`. Не принимать reference screenshots, если они содержат обрезанные labels или непонятный recovery. Ошибки native automation честно отделить от pass.

### W23 — нагрузка, производительность, real-client

Владелец evidence и benchmark scripts без новой точки упаковки. Замер cold/warm startup, суммарная память процесса и WebView children, CPU/UI latency, 30-minute run, synthetic 100/1000/5000 torrents. Проверка отдельного qBittorrent test profile по версии/engine и отдельной папке данных.

Приёмка: исходные данные, median/p95, hardware/runtime/build SHA, нет claims без чисел. Тестовая real-client операция никогда не перенаправляется на обычный пользовательский профиль при ошибке подключения. Результаты загрузок сравнивать только как наблюдение с шумом.

### W24 — финальная интеграция

Владелец сборочных файлов и compatibility docs. Уточнить старую противоречивую документацию, checker фактического C# payload, full local gate, packaged check/smoke, archive current pair, verified manifest. Сохранить pyproject source version, Run.bat и отсутствие GitHub Actions.

Приёмка: кандидат воспроизводим, работает из EXE с bundled frontend; current EXE заменён только после успеха всех проверок и сохранения архива. При locked EXE безопасный отказ. Source push, tag и GitHub Release не выполнять без соответствующего запроса.

## 5. Шаблон задания для экономичного исполнителя

```text
Задача: Wxx — <название>, на основе commit <SHA>.
Цель: <один наблюдаемый результат>.
Прочитай: AGENTS.md; docs/migration-v2/<точные разделы>; <2–5 исходных файлов>.
Контракт: protocolVersion 1, <точные DTO/fixtures>.
Можно менять: <точные файлы/папки production и tests>.
Общие файлы/зависимости не меняй; необходимое изменение сообщи координатору.
Реализуй: <3–6 конкретных требований>.
Не меняй: <смежные модули, алгоритм, формат, границы задачи>.
Приёмка: <положительный сценарий + ошибки/границы из карточки>.
Проверки: <точные существующие команды, только после подготовки W01>.
Никаких операций с реальным пользовательским qBittorrent.
Верни: изменённые файлы, команды/результаты, ограничения, вопросы контракта.
Готовность: реализация + runnable checks; mock-only результат пометь mock-only.
```

Не передавать каждому агенту всю историю переписки. Передавать этот task packet, нужные sections и fixtures. Маленький контекст не означает разрешение не читать реальный код вызовов. Bug fix проверяется по всем callers затронутого метода.

## 6. Протокол интеграции и принятия

1. Координатор подтверждает зависимости, file ownership и чистоту base snapshot.
2. Агент выполняет targeted checks, отдаёт конкретный diff и evidence.
3. Reviewer проверяет контракт/инварианты и отсутствующие edge cases; unsafe modules проходят R.
4. Координатор интегрирует, запускает смежные проверки. Общий regression gate — на завершении волны, полный — G5.
5. Только затем task accepted. При failure — вернуть исполнителю конкретный сценарий, не выдавать зелёный статус с «почти готово».

Если агент дважды неверно трактует один контракт, остановить новые изменения и дать точный failing fixture. Не решать проблему бесконечным увеличением prompt. Если решение требует нового API/key/version, отдельная contract change задача с upstream review предшествует исправлению.
