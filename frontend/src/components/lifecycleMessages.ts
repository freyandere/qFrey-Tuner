import type { Locale } from '../contracts/protocol';

export const enLifecycleMessages = {
  title: 'Local qBittorrent process', stop: 'Review process stop', restart: 'Review process restart',
  confirmTitle: 'Review local process action', confirm: 'Confirm action', cancel: 'Cancel',
  unavailable: 'Process control is unavailable. A verified local connection, idle operation and safe settings state are required.',
  ownerUnavailable: 'The backend could not verify the local process or recover its original launch parameters. Process control is unavailable.',
  recoveryRequired: 'Resolve settings recovery or pending changes before controlling the process.',
  operationConflict: 'Wait for the current operation to finish.',
  failed: 'The process action failed. Review the latest connection and process state before trying again.',
  stale: 'The target or state changed, or the confirmation expired. Request a new review.',
  accepted: 'The request was accepted. Wait for the verified process and connection state.',
  'confirmation.lifecycle.stop': 'Stop the verified local qBittorrent process {processId} serving {endpoint} through its Web API? Torrent transfers will stop. Files and settings are preserved.',
  'confirmation.lifecycle.restart': 'Restart the verified local qBittorrent process {processId} serving {endpoint} through its Web API? Torrent transfers will be interrupted. The backend must preserve the verified executable, original launch parameters and working directory; files and settings are preserved.',
} as const;

export const ruLifecycleMessages: Record<keyof typeof enLifecycleMessages, string> = {
  title: 'Локальный процесс qBittorrent', stop: 'Проверить остановку процесса', restart: 'Проверить перезапуск процесса',
  confirmTitle: 'Проверьте действие с локальным процессом', confirm: 'Подтвердить действие', cancel: 'Отмена',
  unavailable: 'Управление процессом недоступно. Требуются проверенное локальное подключение, отсутствие активной операции и безопасное состояние настроек.',
  ownerUnavailable: 'Backend не смог подтвердить локальный процесс или восстановить исходные параметры запуска. Управление процессом недоступно.',
  recoveryRequired: 'Завершите восстановление настроек или незавершённые изменения перед управлением процессом.',
  operationConflict: 'Дождитесь завершения текущей операции.',
  failed: 'Действие с процессом не выполнено. Перед повтором проверьте последнее состояние подключения и процесса.',
  stale: 'Цель или состояние изменились либо срок подтверждения истёк. Запросите новое подтверждение.',
  accepted: 'Запрос принят. Дождитесь проверенного состояния процесса и подключения.',
  'confirmation.lifecycle.stop': 'Остановить проверенный локальный процесс qBittorrent {processId}, обслуживающий {endpoint}, через его Web API? Передачи торрентов остановятся. Файлы и настройки сохраняются.',
  'confirmation.lifecycle.restart': 'Перезапустить проверенный локальный процесс qBittorrent {processId}, обслуживающий {endpoint}, через его Web API? Передачи торрентов будут прерваны. Backend должен сохранить проверенный исполняемый файл, исходные параметры запуска и рабочую папку; файлы и настройки сохраняются.',
};

export function translateLifecycle(locale: Locale, key: keyof typeof enLifecycleMessages, parameters: Readonly<Record<string, string | number>> = {}): string {
  return (locale === 'ru-RU' ? ruLifecycleMessages : enLifecycleMessages)[key]
    .replace(/\{(\w+)\}/g, (_match, name: string) => String(parameters[name] ?? '—'));
}
