import type { Locale } from '../contracts/protocol';

export const enOwnedActionMessages = {
  title: 'Managed test download', start: 'Review download start', stop: 'Review stopping',
  select: 'Select this workload', selectHint: 'Several verified managed workloads were found. Select one to continue. Selection does not change any torrent.',
  confirmTitle: 'Review test download action', confirm: 'Confirm action', cancel: 'Cancel',
  unavailable: 'This action is unavailable. Check the connection, workload ownership and current operation.',
  failed: 'The action failed. Review the latest workload state before trying again.',
  stale: 'The target, workload or revision changed, or this review expired. Request a new review.',
  accepted: 'The request was accepted. Wait for verified backend state.',
  size: 'Full download size: {bytes} bytes', path: 'Folder on the qBittorrent computer: {path}',
  measurement: 'Measure the prepared test workload. Measurement does not start, stop or delete this torrent.',
  unverified: 'The prepared workload ownership is not verified. Measurement is unavailable.',
  'confirmation.workload.start': 'Start the owned test download {name} ({sizeBytes} bytes) at {serverSavePath} on {endpoint}? This consumes traffic and disk space.',
  'confirmation.workload.stop': 'Stop the owned test torrent {name} ({sizeBytes} bytes) at {serverSavePath} on {endpoint}? Files are preserved.',
} as const;
export const ruOwnedActionMessages: Record<keyof typeof enOwnedActionMessages, string> = {
  title: 'Управляемая тестовая загрузка', start: 'Проверить запуск скачивания', stop: 'Проверить остановку',
  select: 'Выбрать эту нагрузку', selectHint: 'Найдено несколько проверенных управляемых нагрузок. Выберите одну для продолжения. Выбор не изменяет торренты.',
  confirmTitle: 'Проверьте действие с тестовой загрузкой', confirm: 'Подтвердить действие', cancel: 'Отмена',
  unavailable: 'Действие недоступно. Проверьте подключение, принадлежность нагрузки и текущую операцию.',
  failed: 'Действие не выполнено. Перед повтором проверьте последнее состояние нагрузки.',
  stale: 'Цель, нагрузка или версия состояния изменились либо срок подтверждения истёк. Запросите новое подтверждение.',
  accepted: 'Запрос принят. Дождитесь проверенного состояния от backend.',
  size: 'Полный объём скачивания: {bytes} байт', path: 'Папка на компьютере qBittorrent: {path}',
  measurement: 'Замер подготовленной тестовой нагрузки. Замер не запускает, не останавливает и не удаляет этот торрент.',
  unverified: 'Принадлежность подготовленной нагрузки не проверена. Замер недоступен.',
  'confirmation.workload.start': 'Запустить принадлежащую приложению тестовую загрузку {name} ({sizeBytes} байт) в {serverSavePath} на {endpoint}? Это расходует трафик и место на диске.',
  'confirmation.workload.stop': 'Остановить принадлежащий приложению тестовый торрент {name} ({sizeBytes} байт) в {serverSavePath} на {endpoint}? Файлы сохраняются.',
};
export function translateOwnedAction(locale: Locale, key: keyof typeof enOwnedActionMessages, parameters: Readonly<Record<string, string | number>> = {}): string {
  const dictionary = locale === 'ru-RU' ? ruOwnedActionMessages : enOwnedActionMessages;
  return dictionary[key].replace(/\{(\w+)\}/g, (_match, name: string) => String(parameters[name] ?? '—'));
}
