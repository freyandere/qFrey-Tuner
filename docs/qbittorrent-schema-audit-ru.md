# Сверка схемы настроек qBittorrent

Проверено 5 октября 2026 года. Все генерируемые поля сверены с чтением,
обработчиком записи и JSON-типами официальных `appcontroller.cpp` релизов
4.6.0, 4.6.7, 5.0.0, 5.1.0 и 5.2.0: 31 поле для libtorrent 1.x,
29 для 2.x, включая условные порт и интерфейс. Это проверка контракта;
соответствующие сборки программы не запускались.

## Путь применения

`calculate_optimal_settings` → `recommended_preferences` →
`POST /api/v2/app/setPreferences` → повторный `GET /api/v2/app/preferences`.
Существующая проверка схемы дополнительно исключает отсутствующие поля и
несовместимые типы конкретного подключённого клиента. До изменения сохраняются
исходные значения; несовпадение обратного чтения блокирует последующий тест.

Tuner не пишет выбранный `.ini/.conf`: `ConfigManager.apply_settings` блокирует
прямую запись. qBittorrent сам сохраняет параметры через SettingsStorage.
Обратное чтение API подтверждает состояние настроек, но не завершённую запись
на диск: [SettingsStorage](https://github.com/qbittorrent/qBittorrent/blob/release-5.1.0/src/base/settingsstorage.cpp)
откладывает сохранение таймером. Проверка файла после штатного завершения и
повторного запуска реального клиента здесь не проводилась.

## Соответствие API и файла

Ниже ключи хранения из
[SessionImpl 5.1.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.1.0/src/base/bittorrent/sessionimpl.cpp).
Для строк `Session\…` секция файла — `[BitTorrent]`. Это справочная карта,
а не шаблон для прямой записи.

| Поле API | Ключ файла | Тип / единица API |
| --- | --- | --- |
| `up_limit` | `Session\GlobalUPSpeedLimit` | int, B/s |
| `dl_limit` | `Session\GlobalDLSpeedLimit` | int, B/s |
| `max_connec` | `Session\MaxConnections` | int |
| `max_connec_per_torrent` | `Session\MaxConnectionsPerTorrent` | int |
| `max_uploads` | `Session\MaxUploads` | int |
| `max_uploads_per_torrent` | `Session\MaxUploadsPerTorrent` | int |
| `limit_utp_rate` | `Session\uTPRateLimited` | bool |
| `queueing_enabled` | `Session\QueueingSystemEnabled` | bool |
| `max_active_downloads` | `Session\MaxActiveDownloads` | int |
| `max_active_uploads` | `Session\MaxActiveUploads` | int |
| `max_active_torrents` | `Session\MaxActiveTorrents` | int |
| `preallocate_all` | `Session\Preallocation` | bool |
| `async_io_threads` | `Session\AsyncIOThreadsCount` | int |
| `disk_io_read_mode` | `Session\DiskIOReadMode` | int, enum |
| `disk_io_write_mode` | `Session\DiskIOWriteMode` | int, enum |
| `bittorrent_protocol` | `Session\BTProtocol` | int, enum |
| `send_buffer_watermark` | `Session\SendBufferWatermark` | int, KiB |
| `send_buffer_low_watermark` | `Session\SendBufferLowWatermark` | int, KiB |
| `send_buffer_watermark_factor` | `Session\SendBufferWatermarkFactor` | int, % |
| `socket_backlog_size` | `Session\SocketBacklogSize` | int |
| `connection_speed` | `Session\ConnectionSpeed` | int, connections/s |
| `encryption` | `Session\Encryption` | int, enum |
| `anonymous_mode` | `Session\AnonymousModeEnabled` | bool |
| `dht` | `Session\DHTEnabled` | bool |
| `pex` | `Session\PeXEnabled` | bool |
| `lsd` | `Session\LSDEnabled` | bool |
| `disk_cache` | `Session\DiskCacheSize` | int, MiB; только LT1 |
| `enable_coalesce_read_write` | `Session\CoalesceReadWrite` | bool; только LT1 |
| `current_network_interface` | `Session\Interface` | string |
| `listen_port` | `Session\Port` | int |
| `random_port` | производное от `Session\Port` | bool; устаревающее поле |

Лимиты скорости **в файле** хранятся в KiB/s, хотя API принимает B/s.
Это подтверждается преобразованием ×1024/÷1024 в
[SessionImpl 4.6.0](https://github.com/qbittorrent/qBittorrent/blob/release-4.6.0/src/base/bittorrent/sessionimpl.cpp).
Для исходной отдачи 100 Mbps: рекомендация 9765 KiB/s → API 9 999 360 B/s →
файл 9765. Пороги send buffer передаются без умножения: преобразование KiB
в байты для libtorrent выполняет сам qBittorrent. Размер кэша −1 означает auto.

## Исправленное несоответствие

Ранее OS cache отправлялся как `enable_os_cache`; такого поля в проверенных
контроллерах нет. Оно осталось в примере Wiki, поэтому одного примера документации
недостаточно. Теперь отправляются `disk_io_read_mode` и `disk_io_write_mode`:
0 — отключить OS cache, 1 — включить. Оба поля поддерживаются с LT1 и LT2.
Значение write mode 2 — write-through с LT2; оно корректно сохраняется для отката.
Источник enum:
[session.h 4.6.7](https://github.com/qbittorrent/qBittorrent/blob/release-4.6.7/src/base/bittorrent/session.h),
[session.h 5.1.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.1.0/src/base/bittorrent/session.h).

Протокол: 0 TCP+µTP, 1 TCP, 2 µTP. Шифрование: 0 prefer, 1 require,
2 disabled. Для предлагаемого случайного высокого порта передаётся конкретное
число и `random_port=false`; это не случайный выбор при каждом запуске.
Super seeding не глобальный параметр и автоматически не применяется.
Legacy disk cache/coalescing исключаются с LT2, но OS cache не исключается.

Правила 80% отдачи, 4×ядра, подбор слотов/очереди/буферов — эвристики Tuner.
Документация описывает формат и смысл параметров, а не доказывает оптимальность
этих значений. Torrent-тесты сравнивают результат и разрешают проверенный цикл
применения; алгоритм не обучается на их результатах.

## Официальные источники

- [Индекс WebUI API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API).
- [Wiki API 4.1–4.6](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-4.1%29).
- [Wiki API 5.x](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29).
- Контроллер API: [4.6.0](https://github.com/qbittorrent/qBittorrent/blob/release-4.6.0/src/webui/api/appcontroller.cpp), [4.6.7](https://github.com/qbittorrent/qBittorrent/blob/release-4.6.7/src/webui/api/appcontroller.cpp), [5.0.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.0.0/src/webui/api/appcontroller.cpp), [5.1.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.1.0/src/webui/api/appcontroller.cpp), [5.2.0](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.0/src/webui/api/appcontroller.cpp).
- [Настройки libtorrent](https://www.libtorrent.org/reference-Settings.html), [переход на 2.0](https://www.libtorrent.org/upgrade_to_2.0-ref.html).

После исправления прошли 56 проверок расчёта, применения/обратного чтения,
отката, обнаружения конфигурации, отчёта и GUI. Проверка использует тестовые
клиенты; реальные настройки пользователя не изменялись.
