# Проверки, доказательства качества и выпуск

Этот документ разделяет проверки Python при подготовке спецификации и обязательные проверки v2. Pass старого теста не является pass новой C#-реализации. Исторические записи ниже относятся к подготовке 2026-10-05; актуальные результаты реализации и ограничения приведены в [progress.md](progress.md).

## 1. Проверки, реально выполненные 2026-10-05

Рабочий каталог `E:\2.Projects\1.Active\qFrey-Tuner`, исходный SHA `398095c13f334a91cc351e7fa62d5ceaab92e7ef`.

| Команда | Результат |
| --- | --- |
| `uv sync --locked --extra dev` через локальный uv | Exit 0, 24 установленных пакета проверены |
| `uv run --locked --extra dev python -m pytest tests/ -q` | **137 passed in 7.53s**, exit 0 после разрешённого запуска вне sandbox |
| `uv run --locked --extra dev python scripts/check_qbittorrent_schema.py` | Exit 0: 4.6.0, 4.6.7, 5.0.0, 5.1.0, 5.2.0; LT1 — 31 key/type, LT2 — 29 |
| `dotnet --list-sdks` | Нет записей; SDK не найден выбранным dotnet |
| `dotnet --list-runtimes` | Есть .NETCore/WindowsDesktop 10.0.11 |
| Node / pnpm / uv version | 24.19.0 / 11.19.0 / 0.12.23 |
| Computer Use import/list_apps | Успешно; управление будущим приложением не проверялось |

Первый sandbox pytest завершился `71 passed, 66 errors`: ошибки подготовки temp из-за WinError 5 при очистке собственной `.cache/tests`. Перед повтором проверены абсолютный путь и отсутствие symlink/junction. Первый sandbox checker завершился WinError 10013 при сети. Это зафиксированные неуспешные запуски; успешные повторы не скрывают причину. Реальные пользовательские настройки и torrents не изменялись. Сборка EXE при подготовке плана не запускалась.

## 2. Повторяемый Python baseline

PowerShell из корня проекта:

```powershell
$env:UV_CACHE_DIR = Join-Path $PWD '.cache/uv'
$env:UV_PROJECT_ENVIRONMENT = Join-Path $PWD '.cache/venv'
$env:PYTHONPYCACHEPREFIX = Join-Path $PWD '.cache/pycache'
$env:PATH = (Join-Path $PWD '.cache/tools/uv/Scripts') + ';' + $env:PATH
uv sync --locked --extra dev
uv run --locked --extra dev python -m pytest tests/ -q
uv run --locked --extra dev python scripts/check_qbittorrent_schema.py
```

Каждый exit code проверить до следующего gate. GUI требует Windows/Tk; checker — интернет. Не выдавать недоступную сеть за schema pass. Не менять пользовательский qBittorrent ради прохождения теста. При проблеме существующей temp папки проверить путь/владение/блокировку; не удалять глобальные temp и неизвестные каталоги.

## 3. Целевая локальная цепочка v2

Ниже **будущие команды после W01**, сейчас соответствующих проектов/scripts ещё нет. W01 закрепляет actual paths/SDK и добавляет эти проверки в существующий build.bat → scripts/build.py маршрут. Отдельного упаковщика не создавать.

```text
uv sync --locked --extra dev
dotnet restore <solution> --locked-mode
pnpm --dir frontend install --frozen-lockfile
dotnet test <tests-dotnet/QFrey.Tests> --no-restore
pnpm --dir frontend run typecheck
pnpm --dir frontend run test:unit
pnpm --dir frontend run build
pnpm --dir frontend run test:e2e:web
uv run --locked --extra dev python -m pytest tests/ -q
uv run --locked --extra dev python scripts/check_qbittorrent_schema.py
uv run --locked --extra dev python scripts/build.py --candidate-only
pnpm --dir frontend run test:e2e:webview
<candidate> --check
<candidate> --smoke-test
```

`--candidate-only` — предлагаемое дополнение к scripts/build.py из W04, а не уже существующий флаг. Final build.bat обязан выполнить полный gate перед публикацией в artifacts/release; готовый staging-кандидат не становится release по одному `dotnet publish`. Команды dotnet publish/frontend bundle запускаются упаковщиком с paths внутри .cache. Не предлагать пользователю отдельную команду PyInstaller или альтернативный output directory.

Добавить проверку lockfiles/SDK versions и metadata commit. Build manifest по-прежнему содержит version/UTC/SHA-256, допускает дополнительные build metadata без credentials. VERSION берётся из pyproject.toml, не из нового package version.

## 4. Карта паритета существующих тестов

| Python эталон | Новый блок проверки | Дополнительные требования v2 |
| --- | --- | --- |
| test_calculator.py | C# calculator golden | Boundary matrix, fixed random port, structured reasons |
| test_authentication.py | API auth/version fixture | Cancellation, concurrent reconnect, redaction |
| test_optimization_cycle.py | Coordinator integration | Idempotency, revision/session guards, crash stages |
| test_persistence.py | Legacy/persistence | schemaVersion, oversized JSON, atomic failure |
| test_process_manager.py | Windows lifecycle | PID start-time identity, unavailable cmdline/cwd |
| test_hardware.py | Hardware adapter | Remote isolation, explicit unknown, exact volume |
| test_network.py | Network test | Cancellation, no overlap benchmark, retained manual inputs |
| test_workload.py | Workload integration | Восстановление владения и все текущие HTTPS/hash guards |
| test_workload_catalog.py | Catalogue golden | High-speed no sufficient image, size-consent identity |
| test_ramp_metrics.py | Metrics golden | Gaps, no duplicate synthetic timestamps |
| test_behavior_report.py | Typed Results | Evidence/category/scope, full RU/EN rendering |
| test_ui_workflow.py | Browser+WebView E2E | Свободная навигация при сохранении mutation guards |
| test_release_tools.py | Packaging regressions | Web assets, runtime detection, archived pair checksum |
| verify_startup.py | Packaged smoke | Bridge initialize, both locales/themes, missing runtime |

Старый test_missing_target_locks_navigation_and_actions не переносить буквально: v2 разрешает просмотр offline. Новая проверка должна запрещать **мутации**, оставляя историю/настройки UI доступными. Иначе тесты закрепят UX, от которого решили отказаться.

## 5. Backend и bridge: обязательные сценарии

| ID | Стимул | Ожидаемый результат |
| --- | --- | --- |
| B01 | Разрешённая версия/API/LT + live schema | validated только после всех проверок |
| B02 | Future minor/prerelease/malformed version | Нет Apply/benchmark и POST |
| B03 | TLS error/redirect/401/403 | Типизированная ошибка, credentials не утекли, нет blind retries |
| B04 | API key либо bypass | Нет лишнего POST login с пустыми credentials |
| B05 | Endpoint изменён после connect | Старый план непригоден |
| B06 | Неизвестный JSON command/origin | Ноль side effects |
| B07 | bool вместо int/unknown enum/NaN/oversize | Ошибка boundary, не частичный plan |
| B08 | Expected revision устарел | PLAN_STALE, zero mutation |
| B09 | Duplicate requestId same payload | Одна операция, тот же accepted/result |
| B10 | Duplicate ID с другим payload | Явный conflict |
| B11 | Правка draft во время sampling | Снимок эксперимента неизменён, новый plan нужен |
| B12 | Missing optional versus required key | Omission versus block как в эталоне |
| B13 | VPN missing/ambiguous interface | Нет частичного privacy apply |
| B14 | Отмена до POST | Никакой записи; точный статус cancelled |
| B15 | Новая session + late old response | Ответ не обновляет новую session |
| B16 | Frontend reload | Initialize возвращает действительное backend состояние |
| B17 | Error message содержит endpoint/user text | Экранировано; нет HTML execution/headers leakage |
| B18 | Select one of coupled port fields | Группа остаётся целой либо validation error |

## 6. Failure injection: данные и мутации

| Момент сбоя | Проверяемая гарантия |
| --- | --- |
| До создания backup | POST не отправлен |
| Запись временного backup не удалась | Старый файл не повреждён, POST отсутствует |
| Flush/replace не удался | Применение заблокировано |
| Backup сохранён, процесс завершился до POST | Восстановление не считает Apply выполненным |
| POST получен mock server, ответ потерян | Нет повторного POST вслепую; unverified/reconcile |
| Readback расходится по одному полю | Не verified; original сохранён; after не начинается |
| Readback совпал, persist verified не удался | Backup существует, restart повторно проверяет actual |
| After test закончился ошибкой | Настройки остаются applied; Retry/Undo доступны |
| External drift перед rollback | Не перезаписывать стороннее значение |
| Частичный rollback/ответ потерян | Recovery required, backup не стирается |
| Диск переполнен при сохранении series | Нет fake successful result; original backup сохранён |
| Legacy unknown version/extra unsafe prefs | Restore отклонён до POST |
| Locked state/EXE | Безопасный отказ, без force kill/delete |
| Delete workload response lost | Повторная проверка ownership/existence; не удалять найденный чужой торрент |
| Crash/restart после KeepChanges | История retained, restore только по актуальному readback |

Сценарии crash моделируются отдельно от обычного throw. Интеграционный subprocess test может завершать только созданный им test host после нужного journal marker. Нельзя завершать реальные приложения пользователя или qBittorrent ради crash test. Mock API хранит список calls и observed preferences для доказательства порядка.

## 7. Live-метрики и конкурентность

Обязательные deterministic tests:

1. Валидные bytes/s появляются без двойной конвертации; 1 MiB/s = 1,048,576 B/s, 100 Mbps = 12,500,000 B/s до коэффициентов калькулятора.
2. Нет transfer поля → unavailable/error, не zero. Нулевой валидный sample остаётся zero.
3. Stale threshold зависит от intended interval; disconnect рисует gap, а не flatline.
4. 600 UI points cap сохраняется после 10,000 samples; raw measurement series содержит все обязательные точки.
5. Dashboard и measurement пользуются одним collector; число API calls не удваивается.
6. Subscriber unsubscribe/reconnect/window close завершают старый loop; no duplicate timers.
7. Одновременно pending максимум один poll данного endpoint; медленный API не порождает unbounded tasks.
8. UI coalescing может терять промежуточное отображение; terminal state/completion не теряется.
9. Смешанный scope peers/session/workload выявляется; unrelated torrent не попадает в workload sum.
10. CPU первый delta unknown; PID restart сбрасывает базу; remote resources unavailable.
11. Знаменатель CPU учитывает logical cores и реальное elapsed; process I/O не маркируется disk latency.
12. Apply при dashboard polling останавливает collector на critical section; resume не смешивает samples до/после apply.
13. Network test и torrent benchmark конфликтуют; History/locale/theme switch не блокируются.
14. Cancel старого operationId не отменяет новый; cancel/complete race имеет один terminal result.
15. Stalled count != диск неисправен; peer count rise != improvement.
16. Unknown measurement fields, scheduler slip, changed relevant prefs и смена active torrent set invalidируют run по исходному контракту.

Стресс: synthetic snapshots 100/1000/5000 torrents и history 1000 циклов. Первоначальные цели: snapshot aggregation ≤50 ms p95 при 1000 torrents на зафиксированной машине, rendering не создаёт main-thread task >100 ms на обычном 1 Hz update. Если бюджет не достигается, измерить, затем выбирать pagination/sync delta/worker; не добавлять всё заранее.

## 8. UI/локализация/доступность

Smoke screenshots: шесть экранов × две темы × два языка = 24 базовые комбинации при стандартном размере. Дополнительно системная тема и switching. Critical states: preview-only, stale, appliedVerified/after failed, unverified/recoveryRequired, rolledBack/history. Каждое проверяется в RU/EN; dark/light coverage распределить так, чтобы каждый component kind был проверен в обеих темах.

Размеры: 960×680, 1200×840 и широкий 1600×1000 CSS viewport. Windows scaling 100/125/150/200% проверить внутри реального host на доступных дисплеях/VM; `deviceScaleFactor` браузера не заменяет настоящий DPI test. Не менять системные настройки пользователя без необходимости: использовать отдельную тестовую среду, если текущий дисплей не покрывает матрицу.

Сценарии E2E:

- Первый запуск → язык/тема → неверный URL → корректное подключение → сохранение UI preferences.
- Locale/theme switch во время live update, baseline и error modal: данные и focus не теряются.
- Setup numeric input с запятой/точкой, неполным вводом, отрицательным/очень большим значением.
- Preview-only показывает diff, но не допускает Apply; action reason ведёт к baseline.
- Plan editing → новый revision → повторное review → Apply: exact payload соответствует видимому diff.
- Running test → History → Results → обратно: тест не перезапустился, cancellation доступна.
- После Apply after failure: постоянная информация о применённых настройках и корректный Undo.
- Cancel measurement не останавливает и не удаляет torrent; Stop/Delete требуют отдельной операции.
- Export HTML malicious remote text не выполняется; JSON числа не локализуются в строки.
- Offline History и восстановление несовместимого backup показывают понятное ограничение.
- Keyboard-only navigation/dialog/focus return, screen reader stage announcements, graph data table.
- 200% text/zoom: нет обрезанного Apply/Undo, локаль RU не ломает кнопки.

Checks: missing/extra translation keys, placeholder parity, required plural forms, raw keys в DOM, hardcoded product strings вне словарей с allowlist технических tokens. Автоматический поиск строк не заменяет чтение русских текстов человеком/агентом.

## 9. Настоящий WebView2 и Computer Use

Два уровня проверок: браузерный frontend с fake bridge быстрый и детерминированный; настоящий WPF/WebView2 проверяет loader, bridge, local origin, native dialogs, startup и DPI. Оба нужны.

[Playwright официально описывает подключение к WebView2 через CDP](https://playwright.dev/dotnet/docs/webview2). Для теста использовать отдельный process, свободный localhost port и уникальную userDataFolder. Порт включается только явным test launch и не слушает LAN; production приложение само не включает remote debugging. Не принимать чужой процесс на занятом порту за тестовый host.

Computer Use использовать для визуального просмотра native окна и доступных UIA элементов. Импорт/обнаружение уже проверены, но фактическая доступность каждого контрола будущего приложения должна быть проверена отдельно. Screenshots не снимаются с чужих приложений ради QA проекта.

Browser mocks помечены demo и не могут менять real qBittorrent. Native dialogs test либо используют безопасный временный путь, либо ручную проверку: mock file chooser не доказывает доступность реального диалога.

## 10. Реальный qBittorrent и совместимость

Автотесты не используют текущий личный qBittorrent. Для интеграции подготовить отдельный профиль, отдельный Web UI порт, отдельную download directory и synthetic/согласованный workload. Проверить фактический endpoint/versions/profile до любой записи. Если профиль не удалось подготовить — записать blocked; нельзя fallback на знакомый localhost:8080.

Сохранить действующий gate без расширения. Upstream source matrix: 4.6.0/4.6.7/5.0.0/5.1.0/5.2.0 с LT1/LT2 preference ветвями. Не заявлять real testing всех сочетаний только потому, что checker прошёл 10 строк. Для живой проверки использовать доступные официальные сборки и записывать actual qBittorrent/API/LT версии; минимально один LT1 и один LT2 клиент, а отсутствие доступной сборки/профиля фиксировать явно.

Для каждой живой конфигурации: connect/schema → safe baseline → plan → backup/apply/readback → after → rollback/readback → graceful shutdown/restart → прочитать preferences снова. Это отделяет runtime API state от фактической persistence qBittorrent. Downloader и его test files должны принадлежать отдельному профилю; torrent contents не запускать.

Проверить remote endpoint чтение/ошибки по mock; реальный NAS/seedbox без доступа не объявлять протестированным. HTTPS с валидным сертификатом и rejected invalid TLS — отдельные integration cases. Никаких отключений TLS validation ради зелёного статуса.

## 11. Производительность: таблица для заполнения

Сейчас таблица пустая намеренно — производительность не измерялась. Исполнитель W23 заполняет raw samples и summary, а не оценочные числа.

| Показатель | Python baseline | v2 | Условия/цель |
| --- | --- | --- | --- |
| Warm startup median / p95 | Не измерено | Не измерено | 20 запусков; отдельные run IDs |
| Cold startup | Не измерено | Не измерено | 5 документированных cold runs; не называть обычный restart cold |
| UI action latency p95 | Не измерено | Не измерено | Цель <100 ms на navigation/controls |
| Startup regression | — | Не измерено | Цель ≤20% к baseline; объяснить превышение |
| Idle process tree private working set | Не измерено | Не измерено | После 60 s покоя; C# + renderer + GPU и другие owned children |
| CPU idle / live / benchmark | Не измерено | Не измерено | Одинаковый workload и частота |
| API request count / duration | Не измерено | Не измерено | Нет duplicate poll при открытии screens |
| Memory trend 30 min | Не измерено | Не измерено | После warmup нет устойчивого роста; cap buffers доказан |
| UI при медленном API | Не измерено | Не измерено | История/темы/навигация остаются доступны |

Систему, CPU/RAM, версию Windows, build SHA, .NET/WebView2 и режим energy/power записать рядом. Private working set для benchmark памяти приложения и private bytes карточки local qBittorrent — разные величины; не сравнивать их как одну метрику. Shared pages не суммировать многократно как private memory.

Не доказывать 60 FPS одной красивой анимацией. SVG series ограничены, rendering проверяется на p95 interaction latency и long tasks. Download improvements оценивать повторными сопоставимыми опытами; network/peers/cache confounders сохраняются.

## 12. Формат evidence

Внутри `.cache` хранить промежуточные logs/screenshots; финальные отчёты — `artifacts/reports/v2/<UTC>/`. В Git можно коммитить обезличенные fixtures и краткое test summary, но не runtime profiles, EXE, credentials и личные торрент-данные.

```text
run.json                 # SHA, tool versions, OS, start/end, exit codes
python-tests.txt
schema-check.txt
dotnet-tests.trx
frontend-tests.json
webview-e2e/             # traces/screenshots без secrets
performance.json        # raw values + summary + method
manual-checks.md         # observed pass/fail/not-run, screenshots references
compatibility.json      # actual versions/profiles and check boundaries
```

Отчёт агента содержит exact command и exit code, число tests, skipped count/reasons, конкретную проверяемую функцию. «Выглядит нормально» и «build probably works» не evidence. No-run/blocked отличаются от fail; ни то ни другое не считается pass.

## 13. Release gate

1. Все mandatory W00–W24 accepted; critical/high-risk review закрыт.
2. Python regression и upstream checker пройдены на итоговом commit; C# emitted preference contract проверен, а не только старый adapter.
3. Frontend typecheck/unit/E2E, C# unit/integration, настоящие WebView2 сценарии пройдены.
4. Проверена установка/запуск на чистой Windows x64 с WebView2 и сценарий отсутствующего runtime; без этого ограничения честно указаны и выпуск не называется fully portable.
5. Оба языка и темы доступны из EXE; bundle не зависит от dev server/CDN.
6. Backup/recovery и archive release пары проходят fault cases; locked EXE безопасно останавливает publish.
7. Сборка только build.bat/scripts/build.py; кандидат в .cache/staging, итог только artifacts/release/qFrey-Tuner.exe + build.json.
8. Перед заменой текущая пара сохранена в artifacts/archive/builds/<UTC>; state и legacy archives не удаляются.
9. Version из pyproject совпадает с manifest; SHA-256 EXE проверен. Не повышать версию автоматически при сборке.
10. Публикация tag/release только по запросу. После неё проверить target commit, asset names/sizes и checksum скачанного/uploaded EXE. Source push сам по себе не бинарный выпуск.

Если тест упал, не публиковать EXE с объяснением «это только GUI». Если новое требование невозможно проверить на текущей машине, сначала подготовить тестовую среду либо оставить gate незавершённым с точным описанием, а не молча уменьшить scope.
