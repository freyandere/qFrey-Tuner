import type { Locale } from '../contracts/protocol';

export const enCalculatorMessages = {
  'calculator.explanation.uploadLimit': 'Upload limit: 80% of the entered upload speed, leaving a calculated margin for TCP acknowledgements.',
  'calculator.explanation.downloadLimit': 'Download limit: unlimited (0).',
  'calculator.explanation.privateUploadSlots': 'Private tracker: 6 upload slots per torrent, using the application heuristic.',
  'calculator.explanation.seedboxUploadSlots': 'Upload slot values selected for the Seedbox profile.',
  'calculator.explanation.roleUploadSlots': 'Upload slot values calculated for the {role} role.',
  'calculator.explanation.privateConnections': 'Connection limits selected for the private tracker profile.',
  'calculator.explanation.seedboxConnections': 'Connection limits selected for the Seedbox profile.',
  'calculator.explanation.connectionsBelow100': 'Connection limits selected below 100 Mbps.',
  'calculator.explanation.connectionsBelow500': 'Connection limits selected from 100 to under 500 Mbps.',
  'calculator.explanation.connectionsAtLeast500': 'Connection limits selected at 500 Mbps or above.',
  'calculator.explanation.queue': 'Active queue limits: {downloads} downloads and {uploads} uploads.',
  'calculator.explanation.trueNasPreAllocation': 'Pre-allocation is disabled for the TrueNAS profile.',
  'calculator.explanation.diskCache': 'Disk cache setting for {profile}: {cacheMb} (MiB when positive, -1 means automatic, 0 means disabled; application heuristic).',
  'calculator.explanation.asyncIo': 'Asynchronous I/O thread count: {threads}.',
  'calculator.explanation.coalesce': 'Small read and write operations are coalesced.',
  'calculator.explanation.seedboxSocketBacklog': 'Socket backlog selected for the Seedbox profile: {size}.',
  'calculator.explanation.seedboxTcpOnly': 'TCP-only mode selected for the Seedbox profile.',
  'calculator.explanation.dockerTcpOnly': 'TCP-only mode selected for the Docker profile.',
  'calculator.explanation.sendBuffer': 'Send buffer selected: {sizeKiB} KiB.',
  'calculator.explanation.sendBufferDefault': 'Default send buffer selected.',
  'calculator.explanation.fiberTcpOnly': 'TCP-only mode selected for the fiber connection profile.',
  'calculator.explanation.randomPort': 'A proposed high listening port is included in this plan.',
  'calculator.explanation.keepPort': 'Keep the current listening port.',
  'calculator.explanation.requireEncryption': 'Require encryption mode selected for the ISP-throttling input.',
  'calculator.explanation.preferEncryption': 'Prefer encryption mode selected.',
  'calculator.explanation.privateAnonymousDisabled': 'Anonymous mode is disabled for the private tracker profile.',
  'calculator.explanation.publicAnonymousEnabled': 'Anonymous mode is enabled for the public tracker profile.',
  'calculator.explanation.privateDhtPexLsdDisabled': 'DHT, PeX and LSD are disabled for the private tracker profile.',
  'calculator.explanation.publicDhtPexLsdEnabled': 'DHT, PeX and LSD are enabled for the public tracker profile.',
  'calculator.explanation.bindInterface': 'Bind the client to interface {interface}.',
  'calculator.explanation.dockerDefaultInterface': 'The Docker profile uses interface tun0 by default.',
  'calculator.explanation.superSeedingEnabled': 'The application heuristic proposes super seeding for the uploader role; it does not change per-torrent settings.',
  'calculator.explanation.superSeedingDisabled': 'Super seeding is disabled.',
  'calculator.warning.privateTrackerUploadSlots': 'Private tracker: the application heuristic lowered the upload slot count.',
  'calculator.warning.seedboxMaxConnections': 'The Seedbox profile selects the maximum connection values in the application heuristic.',
  'calculator.warning.trueNasCachePolicy': 'The TrueNAS profile disables disk cache and pre-allocation while enabling OS cache.',
  'calculator.warning.nasOsCacheDisabled': 'The NAS profile disables OS cache according to the application heuristic.',
  'calculator.warning.randomPort': 'A high listening port was proposed; this does not guarantee that network filtering will be bypassed.',
  'calculator.warning.requireEncryption': 'Require encryption was selected for the ISP-throttling input; compatibility and outcome are not guaranteed.',
  'calculator.warning.privateTrackerAnonymousMode': 'The application proposes disabling anonymous mode for a private tracker; check that tracker’s rules.',
  'calculator.warning.privateTrackerDhtPexLsd': 'The application proposes disabling DHT, PeX and LSD for a private tracker; check that tracker’s rules.',
  'calculator.warning.vpnInterfaceBound': 'The application proposes binding traffic to interface {interface}.',
  'calculator.warning.dockerDefaultInterface': 'The Docker profile selected its default interface tun0.',
  'calculator.warning.vpnInterfaceMissing': 'VPN is enabled, but no interface was supplied.',
  'calculator.warning.superSeeding': 'The application heuristic proposes super seeding for the uploader role. Per-torrent settings are unchanged.',
} as const;

export type CalculatorMessageCode = keyof typeof enCalculatorMessages;

export const ruCalculatorMessages: Record<CalculatorMessageCode, string> = {
  'calculator.explanation.uploadLimit': 'Ограничение отдачи: 80% от указанной скорости; расчёт оставляет запас для подтверждений TCP.',
  'calculator.explanation.downloadLimit': 'Ограничение загрузки: без ограничения (0).',
  'calculator.explanation.privateUploadSlots': 'Частный трекер: выбрано 6 слотов отдачи на торрент по эвристике приложения.',
  'calculator.explanation.seedboxUploadSlots': 'Значения слотов отдачи выбраны для профиля сидбокса.',
  'calculator.explanation.roleUploadSlots': 'Значения слотов отдачи рассчитаны для роли «{role}».',
  'calculator.explanation.privateConnections': 'Лимиты соединений выбраны для профиля частного трекера.',
  'calculator.explanation.seedboxConnections': 'Лимиты соединений выбраны для профиля сидбокса.',
  'calculator.explanation.connectionsBelow100': 'Лимиты соединений выбраны для скорости ниже 100 Мбит/с.',
  'calculator.explanation.connectionsBelow500': 'Лимиты соединений выбраны для скорости от 100 до 500 Мбит/с.',
  'calculator.explanation.connectionsAtLeast500': 'Лимиты соединений выбраны для скорости от 500 Мбит/с.',
  'calculator.explanation.queue': 'Лимиты активной очереди: загрузок — {downloads}, отдач — {uploads}.',
  'calculator.explanation.trueNasPreAllocation': 'Для профиля TrueNAS отключено предварительное выделение места.',
  'calculator.explanation.diskCache': 'Настройка дискового кэша для профиля {profile}: {cacheMb} (положительное значение — МиБ, -1 — автоматически, 0 — выключен; эвристика приложения).',
  'calculator.explanation.asyncIo': 'Число потоков асинхронного ввода-вывода: {threads}.',
  'calculator.explanation.coalesce': 'Мелкие операции чтения и записи объединяются.',
  'calculator.explanation.seedboxSocketBacklog': 'Размер очереди сокета для профиля сидбокса: {size}.',
  'calculator.explanation.seedboxTcpOnly': 'Для профиля сидбокса выбран режим только TCP.',
  'calculator.explanation.dockerTcpOnly': 'Для профиля Docker выбран режим только TCP.',
  'calculator.explanation.sendBuffer': 'Выбранный размер буфера отправки: {sizeKiB} КиБ.',
  'calculator.explanation.sendBufferDefault': 'Выбран стандартный буфер отправки.',
  'calculator.explanation.fiberTcpOnly': 'Для оптоволоконного подключения выбран режим только TCP.',
  'calculator.explanation.randomPort': 'В план добавлен предлагаемый высокий порт для прослушивания.',
  'calculator.explanation.keepPort': 'Сохранить текущий порт для прослушивания.',
  'calculator.explanation.requireEncryption': 'Для входного признака ограничения провайдером выбрано обязательное шифрование.',
  'calculator.explanation.preferEncryption': 'Выбран предпочтительный режим шифрования.',
  'calculator.explanation.privateAnonymousDisabled': 'Для профиля частного трекера анонимный режим выключен.',
  'calculator.explanation.publicAnonymousEnabled': 'Для профиля публичного трекера анонимный режим включён.',
  'calculator.explanation.privateDhtPexLsdDisabled': 'Для профиля частного трекера выключены DHT, PeX и LSD.',
  'calculator.explanation.publicDhtPexLsdEnabled': 'Для профиля публичного трекера включены DHT, PeX и LSD.',
  'calculator.explanation.bindInterface': 'Привязать клиент к интерфейсу {interface}.',
  'calculator.explanation.dockerDefaultInterface': 'Для профиля Docker по умолчанию используется интерфейс tun0.',
  'calculator.explanation.superSeedingEnabled': 'Эвристика приложения предлагает суперсид для роли раздающего; настройки отдельных торрентов не изменяются.',
  'calculator.explanation.superSeedingDisabled': 'Режим суперсида выключен.',
  'calculator.warning.privateTrackerUploadSlots': 'Частный трекер: эвристика приложения уменьшила число слотов отдачи.',
  'calculator.warning.seedboxMaxConnections': 'Эвристика приложения выбрала максимальные значения соединений для профиля сидбокса.',
  'calculator.warning.trueNasCachePolicy': 'Для профиля TrueNAS выключен дисковый кэш и предварительное выделение, кэш ОС включён.',
  'calculator.warning.nasOsCacheDisabled': 'По эвристике приложения для профиля сетевого хранилища выключен кэш ОС.',
  'calculator.warning.randomPort': 'Предложен высокий порт прослушивания; это не гарантирует обход сетевой фильтрации.',
  'calculator.warning.requireEncryption': 'Для признака ограничения провайдером выбрано обязательное шифрование; совместимость и результат не гарантируются.',
  'calculator.warning.privateTrackerAnonymousMode': 'Для частного трекера предлагается выключить анонимный режим; проверьте правила трекера.',
  'calculator.warning.privateTrackerDhtPexLsd': 'Для частного трекера предлагается выключить DHT, PeX и LSD; проверьте правила трекера.',
  'calculator.warning.vpnInterfaceBound': 'Приложение предлагает привязать трафик к интерфейсу {interface}.',
  'calculator.warning.dockerDefaultInterface': 'Для профиля Docker выбран интерфейс по умолчанию tun0.',
  'calculator.warning.vpnInterfaceMissing': 'VPN включён, но интерфейс не указан.',
  'calculator.warning.superSeeding': 'Эвристика приложения предлагает суперсид для роли раздающего. Настройки отдельных торрентов не меняются.',
};

const roleLabels = {
  'en-US': { leecher: 'leecher', seeder: 'seeder', uploader: 'uploader' },
  'ru-RU': { leecher: 'личер', seeder: 'сидер', uploader: 'раздающий' },
} as const;

const profileLabels = {
  'en-US': { trueNas: 'TrueNAS / ZFS', nas: 'NAS', docker: 'Docker container', seedbox: 'seedbox', hdd: 'hard disk drive', ssdSata: 'SATA SSD', nvme: 'NVMe drive' },
  'ru-RU': { trueNas: 'TrueNAS с ZFS', nas: 'сетевое хранилище', docker: 'контейнер Docker', seedbox: 'сидбокс', hdd: 'жёсткий диск', ssdSata: 'SSD с интерфейсом SATA', nvme: 'накопитель NVMe' },
} as const;

export function localizeCalculatorParameters(locale: Locale, parameters: Readonly<Record<string, string | number>>): Record<string, string | number> {
  return Object.fromEntries(Object.entries(parameters).map(([key, value]) => {
    if (typeof value !== 'string') return [key, value];
    if (key === 'role' && Object.hasOwn(roleLabels[locale], value))
      return [key, roleLabels[locale][value as keyof typeof roleLabels['en-US']]];
    if (key === 'profile' && Object.hasOwn(profileLabels[locale], value))
      return [key, profileLabels[locale][value as keyof typeof profileLabels['en-US']]];
    return [key, value];
  }));
}
