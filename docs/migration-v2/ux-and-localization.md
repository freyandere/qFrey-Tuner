# UX, визуальная система, карточки и RU/EN

Цель: пользователь понимает состояние qBittorrent, смысл предлагаемых изменений и следующий шаг. Интерфейс остаётся полезным при ошибке или во время теста. Этот документ описывает целевой дизайн; проверенного визуального прототипа пока нет.

## 1. Компоновка

Desktop window: старт 1200×840, минимальный размер 960×680 DIPs. Проверить меньшую рабочую область на высоком DPI: окно ограничивается work area, содержимое прокручивается, важные действия не уходят за экран. Боковая навигация 216 px, при узком viewport превращается в компактный ряд с текстовыми подписями. Основное содержимое fluid, максимум около 1440 px. Контекстная помощь 280 px при ширине ≥1280, иначе раскрываемый блок под заголовком.

```text
┌ Стандартная Windows title bar ─────────────────────────────────────┐
│ qFrey-Tuner    ● qBittorrent 5.x · endpoint      RU/EN  Theme  Help │
├────────────────┬──────────────────────────────────────────────────┤
│ Обзор          │ Название раздела                  Что дальше?    │
│ Условия        │ Краткое описание / состояние                     │
│ Рекомендации   │                                                  │
│ Эксперимент    │ Основное содержимое             Контекст/причина │
│ Результаты     │                                                  │
│ История        │                                                  │
├────────────────┴──────────────────────────────────────────────────┤
│ Baseline: прогрев 7/10 с     Настройки не менялись        Отменить │
└───────────────────────────────────────────────────────────────────┘
```

Шесть разделов доступны для просмотра независимо от подключения. «Условия» объединяют Network/Hardware/Goals; рекомендации и замер получают отдельное место, потому что пользователю нужно осознанно посмотреть diff. Подключение — понятный блок обзора и редактируемая панель, а не вечный блокирующий экран. History, Help, theme и locale работают offline.

Нижняя operation bar появляется только во время работы или unresolved recovery. В спокойном состоянии не занимать место пустым прогрессом. Один главный CTA на экран; вторичные кнопки нейтральные, destructive отдельно. Прогресс стадии indeterminate при неизвестной длительности, percentage только при известном количестве отсчётов. Никогда «99%» на неизвестном ожидании сети.

## 2. Экраны и конкретные действия

### Обзор / Overview

- Disconnected: карточка подключения с постоянными labels: URL, способ авторизации, username/password либо API key. Не заполнять поле пароля строкой «Change current password».
- После подключения: endpoint, qBittorrent/API/libtorrent, результат compatibility, local/remote badge; ссылка «Изменить подключение».
- KPI-сетка: загрузка, отдача, подключённые пиры, состояние сеанса; вторым рядом доступные ресурсы. Каждая карточка показывает scope и время последнего обновления.
- Основной live график download/upload за 1/5/10 минут. Заголовок явно «Весь сеанс qBittorrent» или «Тестовая загрузка», переключение scope не смешивает серии.
- Блок «Следующий шаг»: настроить условия / выполнить baseline / посмотреть план / повторить after / решить оставить или откатить.
- Потеря сети: сохраняются последние данные серым, виден возраст, CTA «Подключиться повторно». Не делать бесконечные password retries.

### Условия / Setup

Три группы: «Сеть», «Оборудование», «Цель». Одна компактная сводка принятых inputs сверху, поля ниже. Под каждым вводом указаны единицы и источник: «вручную», «измерено», «определено на этом ПК».

Network: Mbps поля с точным числовым вводом, slider только дополнение; speedtest не округляет измеренное число до шага slider. VPN switch открывает список интерфейсов целевого qBittorrent; не предлагать локальные сетевые имена remote серверу. Internet speedtest явно потребляет трафик и блокируется при torrent benchmark.

Hardware: RAM/CPU/storage, кнопка local detect только если применима, Unknown с ручным вводом. Выбор папки загрузки влияет на выбранный volume, не брать первый физический диск. При remote объяснение «Введите характеристики сервера».

Goals: среда, public/private, leecher/seeder/uploader с короткими пояснениями. Термины публичных/приватных трекеров не маскировать обещаниями privacy.

Изменение поля сразу валидируется после blur/submit без потери введённого текста. Ошибка рядом с полем и в summary по submit. Во время теста поля можно менять как черновик: banner «Изменения будут использованы в следующем плане». Единственная кнопка продолжения — «Проверить условия»/«Построить рекомендации», без скрытого Apply.

### Рекомендации / Recommendations

Наверху: план для конкретного endpoint, версия, дата, число предлагаемых изменений, состояние baseline. Preview-only доступен без baseline, заметно подписан. Apply показывает почему недоступен и ссылку на нужное действие.

Категории рекомендаций: «Скорость и лимиты», «Соединения», «Очередь», «Диск и память», «Протокол и приватность». По умолчанию карточки; переключатель «Таблица» удобен для точной сверки. Поиск по названию и API key, фильтр changed/omitted/warnings. По умолчанию показывать только реальные changes, preserved/unsupported доступны отдельной группой.

Карточка рекомендации:

```text
[Соединения]                              [Эвристика]
Максимум подключений
Сейчас 500                 Предлагается 1000
Почему: для выбранного профиля быстрого канала ...
Возможный компромисс: выше нагрузка на маршрутизатор
[✓ Включить в план]     [Подробнее / API key]
```

Basic mode скрывает ручной override, но показывает число и reason. Advanced открывает override с допустимым диапазоном и reset-to-recommendation. Group selection сохраняет зависимости. Смена checkbox/override аннулирует approval и создаёт новый plan через backend. UI не редактирует исходный plan object.

Диалог Apply: endpoint, число/группы изменений, гарантированно сохраняемые original values, ожидаемый subsequent after test и отдельное предупреждение, если понадобится удалить/заново скачать owned test data. Финальный текст кнопки «Применить N изменений». Если состав или размер новой загрузки изменился — прежнее согласие не подходит.

### Эксперимент / Experiment

Stepper: подготовка → baseline → проверка плана → применение → after → сравнение. В нём показаны completed/active/blocked/failed, не только номер шага. Пройденные шаги доступны для чтения; возврат не повторяет операции автоматически.

Workload selection: собственная текущая загрузка либо управляемый официальный test image. До скачивания показать размер, место на сервере, необходимость повторной загрузки для сопоставления, критерии владения и будущего удаления. Смена image из-за высокой скорости требует нового подтверждения. Не обещать измерение, если каталог не содержит достаточного файла.

Центр: live-кривая, download/upload, seeds/peers, warmup/sample progress. В боковом блоке — условия валидности: активная нагрузка, неизменённые relevant preferences, стабильный scope. Сбой отмечает конкретную причину, не оставляет график под заголовком «успешный результат».

Кнопки отмены различаются: «Отменить замер» не останавливает торрент; «Остановить тестовую загрузку» отдельное действие; «Удалить тест и файлы» отдельное подтверждение. Undo settings тоже отдельное действие. Эти три области пользователь не должен путать.

### Результаты / Results

Сначала фактическое состояние настроек: не применялись / применены и проверены / применение не подтверждено / восстановлены / сохранённый исторический цикл. Затем summary по измерениям, а не общий зелёный «оптимизировано».

Категории результатов: throughput, stability, ramp, connectivity, resources, configuration, limitations. В каждой от 1 до 4 значимых карточек, подробные метрики раскрываются. Порядок фиксирован между запусками: live обновления не переставляют карточки под мышью.

Каждая сравнительная карточка содержит before, after, unit, absolute delta, percent если допустим, verdict и краткую причину. «Недостаточно данных» сопровождается missing reason. Карточки ограничений остаются видимыми без раскрытия всех technical details.

Связь peers и throughput не выдаётся за причинность. Графики before/after используют одинаковую шкалу в одном сравнении; если включён отдельный zoom, это явно видно. Подписи доступные, tooltip не единственный способ прочитать числа — есть таблица.

Основные действия: «Оставить настройки», «Откатить изменения», «Повторить замер», «Экспорт». При unresolved application сначала «Проверить состояние»/«Восстановить», не «Оставить улучшение».

### История / History

Таблица по дате: endpoint alias, версии, итог, число changes, retained/reverted/unknown. Фильтры status/category, открытие read-only результата, выбор двух совместимых циклов для просмотра. Непохожие workloads или analysisVersion дают notComparable, а не удобный percent.

Restore открывает диалог current vs backup и предупреждение о drift. Несовпадающие endpoint/version не обходятся кнопкой «всё равно». Старый отчёт после rollback сохраняет measurements и отметку, что они исторические.

## 3. Семантика карточек

| Тип | Содержимое | Цвет/действие |
| --- | --- | --- |
| LiveMetricCard | Текущее число + unit + scope + freshness + sparkline | Нейтральный; stale заметен текстом и значком |
| RecommendationCard | Current/proposed, reason, support, selection | Accent; heuristic badge |
| ComparisonCard | Before/after/delta, verdict, coverage | Success только при observedImprovement, warning при regression |
| ObservationCard | Факт о peers/stalls/resources без причинного вывода | Нейтральный/info |
| AppliedChangeCard | Показанный plan и результат readback | Verified отдельно от measured benefit |
| LimitationCard | Что не измерено/несопоставимо и почему | Нейтральный/warning, не fake zero |
| RecoveryCard | Что известно о записи, доступный backup, следующий шаг | Error/warning, persistent до решения |

Идентичные рамки, заголовки, spacing и typography; card kind задаёт данные/слоты, а не семь независимых дизайн-систем. Декоративная иконка необязательна, semantic label обязателен. Dashboard и results используют один formatter единиц.

## 4. Визуальные tokens

Предлагаемые стартовые значения — проверить контраст и реальный WebView2 перед фиксацией. Не считать таблицу сертификатом WCAG.

| Token | Dark | Light |
| --- | --- | --- |
| canvas | #10151D | #F4F6FA |
| surface | #19212D | #FFFFFF |
| surfaceRaised | #222D3B | #EDF1F7 |
| textPrimary | #EDF2FA | #172234 |
| textSecondary | #ADBBD0 | #526079 |
| border | #3B4A60 | #CAD3E0 |
| accentText/focus | #8BB5FF | #174EA6 |
| primaryButtonBackground | #244F9E | #174EA6 |
| primaryButtonText | #FFFFFF | #FFFFFF |
| successText | #8AD9AE | #17663A |
| warningText | #F2C66D | #805000 |
| errorText | #FFADB2 | #A51E32 |

Цвета графиков: download blue, upload teal, warmup нейтральная полоса. Помимо цвета использовать подпись/стиль линии. Семантические CSS variables, Tailwind utilities с устойчивыми class names. Не собирать class strings вроде `text-${status}-500`, которые production scanner может не включить.

Spacing: 4/8/12/16/24/32 px. Card radius 12 px, control radius 8 px. Base text 14–16 px, secondary не ниже 12 px, h1 24–28 px. Числа метрик 28–32 px с tabular-nums. System font Segoe UI с корректной кириллицей; не требовать сетевых fonts.

Градиенты, glass blur, бесконечные pulse animations и большие тени не нужны для этого приложения. Transition 120–180 ms для hover/раскрытия, reduced-motion выключает движения. Уважать Windows high contrast/forced-colors, не фиксировать цвета так, чтобы focus исчезал.

## 5. Тема

Настройка themePreference: system/dark/light, effectiveTheme вычисляется отдельно. Default system при первом старте. Переключатель доступен на всех экранах. Изменение system theme влияет на приложение только в режиме system. User preference сохраняется в setup JSON, не в credentials store.

Bootstrap перед первым meaningful paint получает сохранённую тему или нейтральный system fallback; не показывать яркую белую страницу перед dark. WebView default background согласовать с shell. Тема меняет также графики, tooltip, inputs, dialogs и empty/error state, а не только canvas.

Официальная основа Tailwind: [dark mode](https://tailwindcss.com/docs/dark-mode) и [theme colors](https://tailwindcss.com/docs/colors). Style implementation фиксируется одним владельцем tokens; screen agents используют семантические классы.

## 6. Локализация

Поддержка ru-RU и en-US полная. При первом старте ru для русской locale Windows, en для остальных; пользователь может явно выбрать и сохранить. Backend отдаёт codes/messageKeys/params, frontend переводит. Native startup errors до WebView тоже имеют обе локали и fallback English.

Словарь разделён namespace: app, navigation, connection, setup, recommendations, experiment, metrics, results, history, errors, confirmations, accessibility. Английский словарь определяет TranslationKey; русский обязан иметь тот же набор ключей. Тест проверяет отсутствующие/лишние keys и одинаковые placeholders.

Общие правила:

- Не хранить готовые русские/английские тексты в cycle JSON; сохранять code + params и numeric data. В export HTML переводить на выбранный язык; JSON остаётся машиночитаемым.
- Не склеивать перевод из кусочков предложений. Для plural использовать Intl.PluralRules, для чисел Intl.NumberFormat, для времени Intl.DateTimeFormat. Русские one/few/many/other проверяются отдельно.
- Единицы отображать консистентно: исходная сеть Mbps / Мбит/с; transfer MiB/s / МиБ/с. Не менять base bytes при переключении локали. Для ресурсов — MiB/GiB (МиБ/ГиБ) с точной подписью определения.
- Числовой ввод принимает decimal dot или локальную decimal comma, но отвергает двусмысленные thousands separators; backend получает JSON number, не локализованную строку. Тысячные группировки допускаются в read-only display.
- Не переводить API key, endpoint, hash, enum value на wire и diagnostic code. Переводить labels, placeholders, units, aria-label, empty states, confirmation и recovery.
- Переключение языка не пересоздаёт backend session, не очищает черновик и не отменяет измерение.
- Screenshot timestamps и deterministic fixtures фиксируются для visual tests, реальные данные не подгоняются под макет.

| Key | Русский | English |
| --- | --- | --- |
| metrics.stale | Данные устарели · {seconds} с назад | Data is stale · {seconds} s ago |
| results.inconclusive | Убедительное улучшение не обнаружено | No clear improvement observed |
| results.notComparable | Эти замеры нельзя корректно сравнить | These measurements are not comparable |
| apply.unverified | Применение не подтверждено. Резервная копия сохранена. | Application is unverified. The backup is preserved. |
| measurement.cancelNotice | Замер отменён. Торрент продолжает работать. | Measurement cancelled. The torrent is still running. |
| resources.remoteUnavailable | Метрика доступна только для локального процесса | This metric is available only for a local process |
| plan.changed | Условия изменились. Постройте новый план. | Inputs changed. Build a new plan. |

Смешанный русский/английский report существующей версии не переносить как строку; разложить на codes/params. В detailed diagnostics допустим исходный технический код, но рядом должен быть понятный перевод действия.

## 7. Guidance и доступность

Каждая блокировка содержит причину и действие: «Нужен исходный замер → Перейти к эксперименту». Disabled button с tooltip без доступного текста не подходит. Alert dialog только для подтверждения риска или блокирующей ошибки, обычный polling timeout — inline status.

Один polite live region сообщает смену стадии и существенные ошибки; не озвучивать каждое изменение скорости раз в секунду. Клавиатурный focus после навигации идёт на heading, после диалога возвращается к вызвавшей кнопке. Esc закрывает безопасный dialog, не отменяет мутацию скрыто. Tab order соответствует визуальному порядку.

Значимые targets не меньше 32 px высоты, основные кнопки 40 px. Нормальный текст контраст ≥4.5:1, крупный ≥3:1, controls/focus различимы. Проверить 200% zoom и системные масштабы Windows отдельно. Смена цифр не двигает карточки благодаря fixed minimum width/tabular nums.

Не вводить onboarding из десяти popups. Первое подключение и один contextual next-step блок покрывают обучение. Подробнее о настройке можно раскрыть на месте. Help всегда поясняет «что измерено» и «что предположено».

## 8. Предварительная дизайн-приёмка до backend integration

На mocked snapshots показать шесть экранов в dark/light × RU/EN. Обязательные состояния: disconnected, connecting, normal live, stale, preview-only, planReady, applying, appliedVerified, after failed, recoveryRequired, historical result. Demo помечается «Демонстрационные данные» и не экспортируется как измерение.

Проверить: первый экран отвечает «что происходит», Apply показывает точный diff, карточки читаются без tooltip, главная кнопка соответствует стадии, decline/cancel не имеет скрытой мутации. Зафиксировать screenshots и замечания. Backend contract не меняется ради красивого ложного статуса.

После интеграции повторить visual QA внутри WebView2: браузерный screenshot не доказывает корректность окна, DPI, native dialogs и режима запуска EXE.
