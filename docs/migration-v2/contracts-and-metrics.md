# Контракты, состояния, live-метрики и конкурентность

Статус: обязательная спецификация будущей реализации. Имена типов ниже — проект контракта, не существующий скомпилированный код. Версия протокола: 1; новые persisted циклы: schemaVersion 2. Legacy Python JSON без schemaVersion импортируется отдельно.

## 1. Инварианты

- Только backend может объявить target validated, plan applicable или apply verified.
- Запись qBittorrent разрешена только после live schema/version checks, валидного baseline, подтверждённого плана и долговечного backup.
- Применяется точный набор показанных пользователю значений. Изменение черновика, цели или выбранных строк требует нового плана.
- Ошибка/таймаут после отправки POST не равны «ничего не изменилось». Результат выясняется чтением, исходные значения сохраняются.
- Сырые API-ответы, исключения с URL/headers и credentials не отправляются в frontend и не сохраняются целиком.
- Ни один live unknown/error/stale не превращается в нулевой замер.
- История наблюдений не становится текущим состоянием после отката или подключения к другому серверу.
- Без подключённого клиента можно читать историю, менять тему/язык и редактировать черновик; применять/измерять нельзя.

## 2. Общие правила wire format

JSON camelCase, UTF-8, enum как стабильная английская строка. Стабильные идентификаторы не переводятся. DateTimeOffset — ISO 8601 UTC. Интервалы измеряются Stopwatch/монотонными часами; UTC используется только для времени показа и сохранения.

Числа конечные. Скорости — bytes/second, размеры — bytes, время — milliseconds; UI конвертирует один раз. Значения integer вне безопасного диапазона JavaScript ±(2^53−1) передавать десятичной строкой по явно обозначенному полю, например totalBytesDecimal. Enum настроек qBittorrent остаются проверенными API int, а не порядковым номером C# enum. Boolean не принимается за int.

Максимум обычной команды 64 KiB; максимальная глубина JSON 16. Большие истории выдавать страницами/сериями, не расширять глобальный лимит команды. Для event snapshot целевой предел 256 KiB, исключая отдельную ограниченную выдачу серии; контролировать сериализованный размер. Значения лимитов закрепить тестами и пересмотреть только с измерением реального объёма.

Чтение локального файла — через native file dialog и backend, не через произвольный filePath от JS. Выбор папки возвращает непрозрачный token для локального использования. Путь загрузки на удалённом qBittorrent — строка на сервере: нельзя проверять её через локальный Directory.Exists или создавать её на Windows-клиенте.

## 3. Envelope команд

```ts
type Locale = 'ru-RU' | 'en-US';
type ThemePreference = 'system' | 'dark' | 'light';
type Request<K extends string, P> = {
  protocolVersion: 1;
  requestId: string;          // UUID, новый для нового намерения
  command: K;
  targetSessionId: string | null;
  expectedRevision: number | null;
  payload: P;
};
type Reply<T> =
  | { requestId: string; ok: true; revision: number; data: T }
  | { requestId: string; ok: false; revision: number; error: AppError };
type AppError = {
  code: string;              // например PLAN_STALE
  messageKey: string;        // errors.planStale
  parameters: Record<string, string | number>;
  severity: 'info' | 'warning' | 'error';
  retry: 'safe' | 'reconcileFirst' | 'none';
  recoveryActionIds: string[];
  correlationId: string;
};
```

Конкретные payload описываются отдельными DTO, не свободным Dictionary<string, object>. Frontend union типов команд, исчерпывающий switch backend. Неизвестные команды/enum/поля мутаций отклоняются. Read snapshots допускают добавочные необязательные поля при совместимой protocolVersion. Новый несовместимый протокол вызывает понятную ошибку обновления, не попытку угадать формат.

Initialize возвращает locale/theme, версию приложения и протокола, target summary, experiment summary, available actions и последний snapshot. До Initialize frontend показывает boot/error UI без активных команд мутации.

## 4. Каталог команд

| Команда | Payload/предусловия | Результат/эффект |
| --- | --- | --- |
| Initialize | protocolVersion | AppSnapshot, UI preferences |
| Connect | endpoint, auth discriminated union | Новая targetSessionId после полной проверки; secrets только в памяти |
| Disconnect | sessionId | Отмена read tasks, закрытие HTTP session; backup остаётся |
| SetUiPreferences | locale, theme | Атомарное сохранение только UI-предпочтений |
| DetectLocalHardware | выбранный локальный volume token | Observed facts + unknown reasons; без изменения remote inputs |
| RunNetworkTest | подтверждённый запуск | operationId, измеренная скорость; конфликтует с torrent benchmark |
| BuildPlan | полный DraftInputs, selected/override entries | Plan с revision, типами, API before/after, omissions |
| AcceptPlan | planId, revision | Отметка просмотра конкретного плана; не применение |
| StartMeasurement | baseline/after, workload reference | operationId; после проверки состояния и нагрузки |
| CancelOperation | конкретный operationId | Запрос отмены; итог отдельным событием, не мгновенное ложное completed |
| PrepareWorkload | catalogueId, server save path, approval token | Проверенный owned workload, объём/хеш, прогресс |
| StopOwnedWorkload | workloadId, confirmation token | Остановка только собственного торрента |
| DeleteOwnedWorkload | workloadId, deleteFiles, confirmation token | Удаление после повторной проверки endpoint/hash/tag |
| ApplyPlan | planId, expectedRevision, confirmation token | operationId; durable backup → POST → readback |
| Rollback | cycleId, expectedRevision, confirmation token | Проверенный откат или конфликт/неопределённость |
| RestoreLegacyBackup | native selection token, confirmation token | Проверенный импорт и восстановление с теми же guards |
| KeepChanges | cycleId, appliedVerified | Закрыть цикл как retained; архивная возможность guarded restore сохраняется |
| ListHistory / ReadCycle | page cursor / cycleId | Summary без secrets / типизированные результаты |
| ExportReport | cycleId, json/html, native destination token | Сохранённый файл, локализованный HTML, неизменённые числа |
| StartTarget / StopTarget / RestartTarget | verified local owner + confirmation | Lifecycle только однозначного локального qBittorrent |

Confirmation token backend выдаёт для уже рассчитанного operation summary, связанного с sessionId, plan/workload revision и сроком действия (например 2 минуты). Это защита от устаревшего подтверждения, не аутентификация недоверенного frontend. Подтверждение через понятный UI обязательно для apply, destructive workload и lifecycle. KeepChanges не делает никаких API writes.

Долгая команда возвращает accepted + operationId, не держит одну web Promise до конца замера. Progress/state события имеют sessionId, operationId, sequence и revision. Отмена адресная: Cancel старой операции не отменяет следующую.

requestId повторён с тем же payload в этой session: вернуть уже известный accepted/result, не повторять мутацию. С другим payload: DUPLICATE_REQUEST_CONFLICT. Кэш ограничить 256 завершёнными запросами; незавершённые не вытеснять. После рестарта нельзя автоматически повторять неясный POST — сначала восстановление journal и readback. Revision + plan state должны предотвращать повторное применение даже после вытеснения requestId.

## 5. Источник истины и состояние

Раздельные поля вместо десятков дублирующих bool:

| Измерение состояния | Значения |
| --- | --- |
| ConnectionState | disconnected, connecting, validated, degraded, incompatible |
| ExperimentPhase | draft, baselineReady, planReady, applying, appliedVerified, afterReady, completed, recoveryRequired, rolledBack |
| ActiveOperation | null либо { id, kind, stage, progress, cancellable } |
| MeasurementStatus | pending, warmingUp, sampling, valid, invalid, cancelled |
| DataStatus | fresh, stale, unavailable, error |
| ApplyStatus | notApplied, pending, verified, unverified, reverted |

В degraded временно недоступна сеть: endpoint известен, но это не разрешение писать. При reconnect ревалидация версий/схемы и согласование persisted состояния; telemetry history разделяется границей session.

| Событие | Допустимый переход | Обязательное действие |
| --- | --- | --- |
| Успешный baseline | draft → baselineReady | Сохранить inputs, relevant preferences, workload, сырые samples |
| BuildPlan без baseline | draft → draft | Preview-only; Apply заблокирован |
| BuildPlan с baseline | baselineReady/planReady → planReady | Новый immutable plan, prior approval инвалидируется |
| Правка черновика | planReady остаётся старым снимком, applicable=false | Показать stale и потребовать BuildPlan; run inputs неизменны |
| Apply accepted | planReady → applying | Занять gate; повторно проверить target и state; backup до POST |
| Readback match | applying → appliedVerified | Сохранить verified перед after measurement |
| POST timeout/partial mismatch | applying → recoveryRequired | Backup обязателен, никакого after result |
| After measurement valid | appliedVerified → afterReady | Сохранить series и comparison |
| After test failed/cancelled | остаётся appliedVerified | Measurement invalid/cancelled; «настройки остались применены» |
| Rollback match | appliedVerified/afterReady/recoveryRequired → rolledBack | История остаётся, plan непригоден к повторному Apply |
| Rollback conflict/failure | → recoveryRequired | Ничего не затирать, сохранить backup и конфликтующие keys |
| KeepChanges | appliedVerified/afterReady → completed | Сохранить retained, не заявлять измеренную пользу без after |

Сохранённый флаг verified относится к моменту readback. После старта программы проверять текущие values перед показом «сейчас применены»; до этого показывать «были проверены в сохранённом цикле».

AvailableAction = { id, enabled, reasonCode, messageKey, parameters }. Это данные UI, но backend всё равно повторяет guards при исполнении. ReadHistory, ChangeTheme, ChangeLocale и navigation не блокируются экспериментом. Изменение active target блокируется до завершения/согласования мутации; Disconnect не должен терять recovery state.

## 6. Типы рекомендаций и результатов

Не передавать один большой текст report, из которого frontend извлекает цифры regex-ом.

```ts
type ResultCategory =
  | 'throughput' | 'stability' | 'ramp' | 'connectivity'
  | 'resources' | 'configuration' | 'limitations';
type Verdict =
  | 'observedImprovement' | 'observedRegression' | 'inconclusive'
  | 'notComparable' | 'notMeasured';
type Evidence = 'observed' | 'derived' | 'heuristic' | 'userProvided';
type MetricReading =
  | { status: 'fresh'; value: number; sampledAtUtc: string }
  | { status: 'stale'; lastValue: number; sampledAtUtc: string; reasonCode: string }
  | { status: 'unavailable' | 'error'; reasonCode: string };
type HistoricalValue =
  | { quality: 'valid'; value: number; measuredAtUtc: string }
  | { quality: 'invalid' | 'notMeasured'; reasonCodes: string[] };
type ComparisonValue = {
  metricId: string;
  category: ResultCategory;
  unit: 'bytesPerSecond' | 'bytes' | 'milliseconds' | 'count' | 'percent';
  before: HistoricalValue;
  after: HistoricalValue;
  absoluteDelta: number | null;
  relativeDeltaPercent: number | null;
  verdict: Verdict;
  reasonCodes: string[];
  evidence: Evidence;
  scope: 'session' | 'workload' | 'localProcess' | 'localHost';
};
```

Исторические агрегаты не выдавать за fresh live: ComparisonValue использует HistoricalValue со временем замера, а контейнер ResultCard имеет context='historical'/'currentExperiment'. Неполный или invalid run не преобразуется в valid value для verdict. MetricReading используется только для live-потока, HistoricalValue — для сохранённого измерения.

Recommendation = id, groupId, category, titleKey, reasonKey, reasonParams, evidence='heuristic', currentValue, proposedValue, unit, valueType, editable, allowedRange/allowedValues, supportStatus, selected, cautionCodes. valueType — number/bool/enum/interface; специальные значения -1/0 описываются как auto/unlimited только для конкретного параметра. Не считать все нули unlimited.

Plan хранит canonical API values отдельно от display values. current/proposed enum передаются со стабильным value и labelKey. Связанные поля (например listen_port и random_port) — одна группа выбора; нельзя снять второе поле и получить другой смысл. Диапазоны подтверждаются upstream и локальными правилами, не фантазируются frontend. VPN binding нельзя тихо исключить при partial plan.

ResultCard = id, category, kind, titleKey, explanationKey, evidence, severity, context, cycleId, measuredAt, content. kind — metricComparison / observation / appliedChange / limitation / recovery. Severity — neutral/info/warning/error, а improvement/regression хранится в verdict отдельно. Отсутствие данных не красная «ошибка оптимизации».

Пример: рост числа peers — observation, neutral, без автоматического verdict improvement. Снижение CPU — observation, пока workload scope и метод не допускают сравнения. Результат «нет доказанного улучшения» не равно «ничего не изменилось».

## 7. Каталог live-метрик первого выпуска

| MetricId | Источник | Raw unit / scope | Частота | Ограничение |
| --- | --- | --- | --- | --- |
| transfer.download / upload | transfer/info: dl_info_speed, up_info_speed | B/s, session | 1 s | Это весь qBittorrent, не только test torrent |
| workload.download / upload | torrents/info: dlspeed, upspeed выбранных hashes | B/s, workload | 1 s | Суммировать только выбранный scope |
| peers.seeds / peers.total | num_seeds / num_seeds+num_leechs | count, workload | 1 s | Подключённые peers; unknown хотя бы в одном обязательном поле → aggregate unknown |
| session.dhtNodes | transfer/info: dht_nodes | count, session | 1 s | Отсутствие поля не 0 |
| session.connection | transfer/info connection_status, если audited и доступен | enum, session | 1 s | Статус клиента, не отдельный port reachability test |
| workload.active / stalled / errors | состояния torrents/info | count, workload | 1 s | stalled не диагноз сетевой/дисковой неисправности |
| experiment.elapsed / stage / samples | backend coordinator | ms/count | 1 s | Stage имеет отдельные warmup/sample окна |
| diagnostics.apiDuration | Stopwatch вокруг запросов | ms, endpoint | каждый запрос | Не internet ping и не bufferbloat |
| local.cpu | delta Process.TotalProcessorTime / elapsed / logical CPU count ×100 | %, localProcess | 2 s | Только verified owner; 0–100% от общей CPU capacity, первый отсчёт unknown |
| local.privateMemory | process private memory | bytes, localProcess | 2 s | Не working set; подпись «частная память процесса» |
| local.ioRead / ioWrite | Windows process I/O counters, если доступны | B/s, localProcess | 2 s | Process I/O может включать не только диск; не называть disk bandwidth/latency |

Live статус версии/лимитов/выбранного endpoint берётся из валидированного target snapshot. Preferences для drift: на границах операций и проверок замера; dashboard может обновлять их редко (например 10 s), не запрашивать весь набор ради каждой карточки. Проверки baseline/apply/rollback не полагаются на этот кэш.

Новые поля вроде connection_status включать только после сверки tagged transfer/sync implementation во всех поддерживаемых ветках. Наличие в 5.2.0 не доказывает 4.6.x. Перед реализацией расширить документацию и тестовые контракты чтения. Сначала использовать уже применяемые transfer/info и torrents/info. Переход на sync/maindata — только если проверка большой сессии покажет необходимость; тогда отдельно реализовать rid/full_update/deletions и регрессии для пропущенных delta, не смешивать неполный sync snapshot с полной выборкой.

Для remote endpoint local.cpu/privateMemory/io не запрашиваются как показатели qBittorrent-сервера. Карточка пишет «Недоступно: нужен локальный процесс»; для server metrics потребовался бы отдельный агент, который в scope не входит. Для localhost недостаточно имени процесса: подтвердить владельца Web UI порта, PID и время старта. PID reuse/exit сбрасывают счётчики, не дают отрицательную скорость.

## 8. Частоты, freshness и буферы

Один collector на targetSession, запросы не перекрываются сами с собой. UI имеет последние 600 агрегированных точек (10 минут при 1 Hz); скрытые экраны не создают polling subscriptions. При свёрнутом окне можно снизить dashboard polling до 5 s, только если benchmark не активен. Telemetry transient; не писать бессрочную историю раз в секунду.

fresh: последний успешный отсчёт не старше max(3 × expectedInterval, 3 s). stale: последнее значение есть, но порог превышен; показать возраст и разрыв графика. unavailable: источник отсутствует/remote/нет прав. error: источник ответил некорректно; reason отдельно. При disconnect показывать stopped/stale, не рисовать продолжение неизменной скорости.

У collector единый publish immutable sample: measurement consumer получает каждый валидный sample до потерь, UI получает coalesced latest sample. Нельзя отдавать одну Channel нескольким читателям и считать это broadcast. UI очередь latest-only вместимость 1; terminal operation/state events передаются отдельно и не теряются. Полная серия текущего теста ограничена известной длительностью; при невозможности сохранить обязательный sample run становится invalid, без тихой потери точки.

События UI не более 5 Hz, фактические метрики 1 Hz. Анимация числа/графика не генерирует новые измерения. Dashboard может пересэмплировать/сокращать серию для SVG, но result analysis использует исходные raw samples. На графике разные шкалы для B/s и count; отсутствующие точки не соединять как доказанную непрерывность.

В первой версии сохранить sampling method Python: последовательные чтения и те же guards/интервалы. Collector во время benchmark обслуживает его расписание и публикует те же данные в dashboard вместо второго опроса. Тайминги фиксируются фактически, не подставляются index × 1s. Прогрев 10 s + 60 отсчётов; сетевое время и подготовка workload добавляются отдельно.

## 9. Аналитика

Сохраняемые raw samples: elapsedMs, dl/up Bps session и workload, peer counts, workload states, phase, status/reasons. Versions, endpoint identity, relevant preferences, workload hashes и inputs snapshot — metadata цикла.

Средняя/медиана/стандартное отклонение — по валидному окну, не по warmup. Population stddev как в Python. Relative delta = (after/before−1)×100 только при before>0; при нуле вернуть null и reason noBaselineTraffic, не Infinity. Stability CV = stddev/mean×100, mean=0 → unknown. Zero-speed samples — процент отсчётов, не оценка длительности простоя при нерегулярном расписании.

Сохранить консервативный verdict текущего сравнения: порог max(5% baseline mean, 2×sqrt(varBefore/n + varAfter/m)); это heuristic/noise threshold, не statistical significance. Peers и cache могут меняться; причина изменения конкретной настройки не доказана. Метод идентифицировать analysisVersion, чтобы новый алгоритм не менял старые результаты незаметно.

Разгон: первое наблюдаемое движение, первый наблюдаемый seed/peer, t50/t90 относительно медианы второй половины measurement; минимум три устойчивые точки и проверка расстояния во времени как в ramp_metrics.py. Без fresh-start t50/t90 обозначают время от начала наблюдения, а не от запуска торрента. Не хранить монотонное время как пригодное после restart.

Категория ресурсов имеет live данные; сравнение before/after CPU/RAM допустимо лишь при одинаковом PID identity, достаточном покрытии, одинаковом определении метрики и scope. В первом переносе не добавлять эти показатели в общий «балл оптимизации». Общего искусственного score 0–100 не вводить.

## 10. Конкурентное выполнение

| Работа | Механизм | Ограничение |
| --- | --- | --- |
| WPF/WebView callbacks | UI Dispatcher/STA | Быстро validate/enqueue; никаких .Wait/.Result и файловых циклов |
| API/metadata/network I/O | async HttpClient + token | Отдельные credential sessions для API и публичных metadata |
| Windows discovery/WMI/IOCTL | Фоновые задачи; COM apartment при необходимости | Не вызывать из render/Dispatcher; максимум один discovery |
| Анализ/парсинг больших файлов | Task.Run, bounded input | До двух CPU-задач; короткий calculator не распараллеливать по формулам |
| Telemetry | Один async loop | Один outstanding request на endpoint; во время benchmark один общий источник |
| Мутации/нагрузочные тесты | Gate координатора | Apply/rollback/lifecycle/workload/network test не конфликтуют |
| Persistence | Последовательный writer | Нельзя переупорядочить durable backup и POST |

Task.Run применяется к CPU/blocking Windows work; async I/O не требует отдельного потока на запрос. Это следует [руководству Microsoft по async-сценариям](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-scenarios). Требование «многопоточный backend» означает отсутствие блокировки UI и контролируемую конкурентность, а не произвольные Thread на каждую кнопку.

Gate для изменяющих/нагрузочных операций удерживается весь workflow, поэтому Rollback во время Apply получает operationConflict, а не стоит в скрытой очереди на неожиданный запуск позже. Read telemetry во время apply/readback временно приостанавливается и помечается suspended; после completion возобновляется. UI остаётся доступен.

Cancellation: чтение и ожидания cancellable; durable backup/отправленный POST/readback reconciliation нельзя объявлять безопасно отменёнными посередине. Кнопка показывает «Завершаем проверку состояния»; bounded timeout переводит в recoveryRequired. Закрытие окна при pending mutation сначала пытается bounded graceful completion; crash recovery опирается на backup. Нельзя auto-rollback вслепую при закрытии или crash.

На reconnect отменить старый collector и ждать его завершения, сменить session generation, игнорировать запоздавшие callbacks. Session HttpClient immutable по endpoint/auth; запрещено менять shared headers одновременно с запросом. На shutdown отменить задачи и dispose; обработанные ошибки логировать без credentials, unobserved Task exceptions не оставлять.

Concurrency tests используют barriers/fake clock, не случайные Thread.Sleep. Проверить быстрые double click, cancel+complete race, reconnect+response, apply+poll, rollback+drift, close+persist, peer/process disappear.

## 11. Persistence и recovery

Новые циклы включают schemaVersion, analysisVersion, cycleId, createdUtc/updatedUtc, versions, target identity, inputs, plan, original, intendedApplied, observedReadback, operationStage, run data и outcome. Snapshot allowlist; не сериализовать HttpClient или целый prefs объект.

Порядок Apply: валидировать → снять свежий current → проверить drift → сформировать backup → записать временный файл в том же каталоге → flush на диск → атомарно заменить файл → лишь затем POST → прочитать фактическое состояние → сохранить verified/unknown. Persistence failure до POST гарантирует отсутствие POST. Persistence failure после успешного POST требует recovery и сохранения уже существующего backup; не обнулять original.

Порядок восстановления: выбрать файл → ограничить размер (первоначальный предел 16 MiB для cycle JSON) и depth → распознать версию → проверить endpoint/version/schema/types → прочитать live values → показать diff/conflicts → подтвердить → записать только original keys → readback. Неизвестная будущая schemaVersion не импортируется как legacy. Повреждённый файл не удаляется.

Legacy: fixtures из существующих JSON, optional ramp fields, partial apply, original без applied, другой endpoint/version, неизвестные keys. Перед импортом сохранить неизменённую копию в state; новый формат записать отдельно. Архив не очищать автоматически. Старый verified не доверенный текущий readback.

KeepChanges сохраняет original/intendedApplied и факт сохранения пользователем. Следующий эксперимент получает новый cycleId и новый baseline. Restore из history проверяет drift против конкретного цикла; нельзя перепрыгнуть через чужие изменения молча.

## 12. Источники и граница проверки

- Нормативные правила проекта: [version compatibility](../version-compatibility.md), [schema audit](../qbittorrent-schema-audit-ru.md).
- Tagged [transfercontroller 5.2.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.0/src/webui/api/transfercontroller.cpp) и [synccontroller 5.2.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.0/src/webui/api/synccontroller.cpp) просмотрены при планировании источников telemetry; это не полный аудит новых read fields по всем версиям.
- [WebView2 security](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security): origin checks и ограниченные сообщения; конкретные проверки входят в задачу bridge.
- Существующий checker проверяет preferences, а не всю telemetry, Windows CPU или протокол C#/TS. Его зелёный статус не заменяет новые тесты.

## 13. Реализованный контракт W02/W03 (2026-10-06)

Типы находятся в `src/QFrey.Core/Contracts/{Protocol,Domain,Commands}.cs` и `frontend/src/contracts/{protocol,domain}.ts`. Это реализация границы данных, а не готовность перечисленных обработчиков. Desktop пока исполняет только Initialize и SetUiPreferences; остальные известные команды возвращают COMMAND_NOT_IMPLEMENTED без побочных эффектов.

- `CommandPayloads.Read` использует отдельный DTO для каждой команды; `Protocol.Parse` проверяет размер/depth/duplicate properties, обязательные поля, enum, числовые типы, session/revision и отклоняет неизвестные поля. `JsonElement` присутствует только на envelope границе, не в plan/results/persisted domain.
- `PreferenceValue` — строго int32/bool/string. `null` допустим для отсутствующего current/observed значения, но не в overrides или canonical plan dictionaries. API ranges и зависимости групп дополнительно проверяет будущий W13; структурная проверка не означает разрешение применять значение.
- `MessageParameter` — finite number в безопасном JS диапазоне или string. AppError и AvailableAction имеют parameters; перевод и форматирование не меняют canonical значения.
- Все DateTimeOffset на чтении требуют ISO UTC (`Z`/`+00:00`), на записи нормализуются в UTC. HistoricalValue не используется вместо live MetricReading.
- `AppSnapshot` расширен optional experiment/confirmations/hardware/networkTest/interfaces. HardwareFacts допускает частично неизвестные RAM/CPU/storage, ObservedHardware.Inputs заполняется только при полном наборе. Старые shell snapshots остаются совместимыми; compatible optional additions не отвергаются.
- Для получения confirmation/native selection добавлены read-only RequestConfirmation и SelectNativeFile. SelectNativeFile принимает только purpose (restore/exportJson/exportHtml/volume), локальный путь от JS не принимается. Confirmation subject проверяется будущим coordinator, это не доверенный token из frontend.
- Ответ SelectNativeFile — nullable `NativeSelection { token, purpose, displayName, expiresUtc }`; null означает отмену native dialog. Полный путь остаётся только в backend. Token ограничен purpose, одноразовый, действует 10 минут; хранилище содержит максимум 32 выбора. ExportReport принимает только такой token и пишет проверенный JSON/HTML с атомарной заменой выбранного отчёта. Доступность этой реализации и фактически выполненные проверки отслеживаются в progress.md.
- Catalog tests покрывают 25 команд (23 основных и два вспомогательных вида запросов). Общие protocol fixtures принимают C# и TS; unit conformance не доказывает выполнение API/мутаций или физическую работу hardware.
- Frontend snapshot subscription проверяет initialized session, sequence/revision и весь payload. Production bridge ещё должен подключить backend collector и operation events в W11/W15; отсутствие их сейчас не выдаётся за live integration.

### Реализованные read-only потребители, 2026-10-06

Connect/Disconnect и live snapshot collector теперь подключены к host. `BuildPlan` перечитывает live preferences/interfaces, создаёт новый canonical revision и сохраняет его в памяти для Initialize. Чтение preferences не является baseline: без измерения plan имеет previewOnly, не applicable, approval/Apply не разрешены. `PlanReview` хранит serialized canonical snapshot; mutable DTO, возвращённый UI/вызывающему коду, не меняет authority.

Transfer fields проверены по TransferController пяти pinned tags: `dl_info_speed`, `up_info_speed` — целые B/s всей session; `dht_nodes` — count session. Missing field → unavailable, неправильный тип/отрицательное значение → error; ноль остаётся валидным нулём. `diagnostics.apiDuration` измеряет HTTP round trip Stopwatch без ожидания внутренней очереди, scope endpoint. Это не ping/latency интернета. Новый schema checker C# пока не заменяет проверку Python payload.

Local sampler использует [GetActiveProcessorCount](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getactiveprocessorcount) с ALL_PROCESSOR_GROUPS и [GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters) с query-limited handle. Счётчики [IO_COUNTERS](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-io_counters) учитывают весь process I/O. Обновление localProcess — раз в 2 s, stale threshold 6 s; session/endpoint — 1 s и 3 s. CPU первый delta unknown; private memory не working set. Владение портом/PID/start time проверяется перед и после чтения. Remote/неоднозначное владение не подменяется данными локального Tuner.

### W16 execution extension — bounded catalogue review

Within protocol v1, RequestConfirmation gains optional catalogueId/serverSavePath only for PrepareWorkload. That action requires both and forbids plan/cycle/workload subjects; all other actions reject non-null catalogue/path extras. This identifies the exact user review subject, not write authority. Runtime consent must additionally bind the current validated session, revision, fresh exact versions, catalogue consent identity (including bytes/source), opaque token expiry and one-use consumption.

AppSnapshot has optional workloadCatalogue entries with id/name, canonical UInt64 decimal-string totalBytesDecimal and HTTPS metadataSource without userinfo. It is backend-owned display data; no frontend catalogue hash, path or declared ownership can grant API authority. New metadata adds are held paused on qBittorrent4.6 and stopped on5.x; metadata acceptance is distinct from verified identity and from consent to start a real download. Unknown server path safety still denies owned deletion.
# Runtime extension: guarded legacy restore review

`RequestConfirmation` with action `RestoreLegacyBackup` requires an opaque native `selectionToken`; planId, cycleId and workloadId must be null. Other actions cannot carry this token. Native paths never cross the bridge. Optional snapshot `restore` holds selectionToken, displayName, sourceCycleId and readonly review (diffs, dispositions, block reasons, fingerprint, canRestore). A blocked review never grants consent. The eventual restore consumes separate, expiring confirmation bound to the current session, revision and imported source; durable source archive and pending journal precede any settings POST.
