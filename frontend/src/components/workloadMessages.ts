import type { Locale } from '../contracts/protocol';

export const enWorkloadMessages = {
  'confirmation.workload.prepare': 'Prepare {name} ({sizeBytes} bytes) at {serverSavePath} on {endpoint}? Torrent metadata will be added in a stopped state. This does not authorize starting the download.',
  'confirmation.workload.stop': 'Request stopping the owned test torrent {name} ({sizeBytes} bytes) at {serverSavePath} on {endpoint}? No files will be deleted.',
  'errors.workloadPreparationFailed': 'The test torrent could not be prepared. Review the connection and selected workload. Its previous state and creation journal are preserved.',
  'workload.catalogue.title': 'Prepare a separate test torrent',
  'workload.catalogue.select': 'Test image',
  'workload.catalogue.path': 'Save folder on the qBittorrent computer',
  'workload.catalogue.pathHint': 'Use an absolute folder on the target computer. The application cannot prove free space or symlink safety on a remote computer.',
  'workload.catalogue.prepare': 'Review preparation',
  'workload.catalogue.reviewing': 'Preparing review…',
  'workload.catalogue.stopped': 'Ownership is verified. The torrent was requested to be added stopped; its stopped state has not yet been verified.',
  'workload.catalogue.noDownload': 'Preparation adds only torrent metadata requested in a stopped state. Space allocation may depend on qBittorrent settings. Review the full file size and target folder before confirming. This does not authorize starting the download.',
  'workload.catalogue.bytes': 'Full download size: {bytes} bytes',
  'workload.catalogue.source': 'Official metadata source',
  'workload.catalogue.unavailable': 'Preparation is unavailable while an operation or captured experiment is active. Finish that experiment first.',
  'workload.catalogue.noTarget': 'Connect to a validated qBittorrent target before preparing a test torrent.',
  'workload.catalogue.noCatalogue': 'No supported test images are available from the backend.',
  'workload.catalogue.pathInvalid': 'Enter an absolute folder path on the qBittorrent computer without “.” or “..” path segments.',
  'workload.catalogue.confirmTitle': 'Review test torrent preparation',
  'workload.catalogue.confirm': 'Confirm preparation',
  'workload.catalogue.ownedTitle': 'Prepared test workload',
  'workload.catalogue.reviewFailed': 'The preparation review could not be created. Check the target and path, then review again.',
  'workload.catalogue.confirmationStale': 'The target, catalogue selection, path or revision changed. Review the preparation again.',
  'workload.catalogue.confirmationExpired': 'The preparation review expired. Request a new review before continuing.',
  'workload.catalogue.prepareFailed': 'The test torrent could not be prepared. Review the connection and selected workload. Its previous state and creation journal are preserved.',
  'workload.catalogue.requestAccepted': 'Preparation was accepted. This does not start the download.',
  'workload.catalogue.preparing': 'Preparing the test torrent. Download start is not authorized.',
  'workload.catalogue.ownershipUnverified': 'Workload ownership has not been verified. Do not stop or delete it from this screen.',
} as const;

export const ruWorkloadMessages: Record<keyof typeof enWorkloadMessages, string> = {
  'confirmation.workload.prepare': 'Подготовить {name} ({sizeBytes} байт) в {serverSavePath} на {endpoint}? Метаданные торрента будут добавлены в остановленном состоянии. Это не разрешает запуск скачивания.',
  'confirmation.workload.stop': 'Отправить запрос остановки принадлежащего приложению тестового торрента {name} ({sizeBytes} байт) в {serverSavePath} на {endpoint}? Файлы не будут удалены.',
  'errors.workloadPreparationFailed': 'Не удалось подготовить тестовый торрент. Проверьте подключение и выбранную нагрузку. Предыдущее состояние и журнал создания сохранены.',
  'workload.catalogue.title': 'Подготовка отдельного тестового торрента',
  'workload.catalogue.select': 'Тестовый образ',
  'workload.catalogue.path': 'Папка сохранения на компьютере qBittorrent',
  'workload.catalogue.pathHint': 'Укажите абсолютный путь на целевом компьютере. Приложение не может подтвердить свободное место и безопасность символических ссылок на удалённом компьютере.',
  'workload.catalogue.prepare': 'Проверить подготовку',
  'workload.catalogue.reviewing': 'Готовим подтверждение…',
  'workload.catalogue.stopped': 'Принадлежность проверена. Торрент запрошен к добавлению остановленным; состояние остановки ещё не проверено.',
  'workload.catalogue.noDownload': 'Подготовка добавляет только метаданные торрента, запрошенного в остановленном состоянии. Выделение места может зависеть от настроек qBittorrent. Перед подтверждением проверьте полный размер файла и целевую папку. Это не разрешает запуск скачивания.',
  'workload.catalogue.bytes': 'Полный объём скачивания: {bytes} байт',
  'workload.catalogue.source': 'Официальный источник метаданных',
  'workload.catalogue.unavailable': 'Подготовка недоступна во время операции или сохранённого эксперимента. Сначала завершите эксперимент.',
  'workload.catalogue.noTarget': 'Перед подготовкой тестового торрента подключитесь к проверенной цели qBittorrent.',
  'workload.catalogue.noCatalogue': 'Backend не предоставил поддерживаемые тестовые образы.',
  'workload.catalogue.pathInvalid': 'Введите абсолютный путь к папке на компьютере qBittorrent без сегментов «.» и «..».',
  'workload.catalogue.confirmTitle': 'Проверьте подготовку тестового торрента',
  'workload.catalogue.confirm': 'Подтвердить подготовку',
  'workload.catalogue.ownedTitle': 'Подготовленная тестовая нагрузка',
  'workload.catalogue.reviewFailed': 'Не удалось создать подтверждение подготовки. Проверьте цель и путь, затем запросите подтверждение снова.',
  'workload.catalogue.confirmationStale': 'Цель, выбор каталога, путь или версия состояния изменились. Запросите подтверждение подготовки снова.',
  'workload.catalogue.confirmationExpired': 'Срок подтверждения подготовки истёк. Перед продолжением запросите новое.',
  'workload.catalogue.prepareFailed': 'Не удалось подготовить тестовый торрент. Проверьте подключение и выбранную нагрузку. Предыдущее состояние и журнал создания сохранены.',
  'workload.catalogue.requestAccepted': 'Запрос подготовки принят. Загрузка не запускается.',
  'workload.catalogue.preparing': 'Подготавливается тестовый торрент. Запуск скачивания не разрешён.',
  'workload.catalogue.ownershipUnverified': 'Принадлежность нагрузки не проверена. На этом экране нельзя останавливать или удалять её.',
};

export type WorkloadMessageKey = keyof typeof enWorkloadMessages;
export function translateWorkload(locale: Locale, key: string, parameters: Readonly<Record<string, string | number>> = {}): string {
  const dictionary = locale === 'ru-RU' ? ruWorkloadMessages : enWorkloadMessages;
  const template = dictionary[key as WorkloadMessageKey] ?? dictionary['workload.catalogue.unavailable'];
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = parameters[name];
    return typeof value === 'number' && Number.isFinite(value) ? new Intl.NumberFormat(locale).format(value)
      : typeof value === 'string' ? value : '—';
  });
}
