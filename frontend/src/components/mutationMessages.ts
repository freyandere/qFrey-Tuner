import type { Locale } from '../contracts/protocol';

export const enMutationMessages = {
  'mutation.closedReverted': 'Original settings were restored and verified. This cycle is finished; its measurements remain available in Results and History. Build a new plan to start another experiment.',
  'mutation.closedKept': 'Verified settings were kept. This cycle is finished and its backup remains in History. Build a new plan to start another experiment.',
  'mutation.title': 'Plan and settings',
  'mutation.planUnavailable': 'A current plan is not available.',
  'mutation.invalidTarget': 'Connect to a validated target and review a plan for that session.',
  'mutation.baselineRequired': 'A valid baseline is required before settings can be applied.',
  'mutation.planPreview': 'This plan is preview-only and cannot be applied.',
  'mutation.planNotApplicable': 'Resolve the plan omissions or validation issues before continuing.',
  'mutation.planApproved': 'This plan has already been approved.',
  'mutation.approve': 'Approve plan',
  'mutation.approving': 'Approving…',
  'mutation.apply': 'Review apply',
  'mutation.rollback': 'Review rollback',
  'mutation.keep': 'Keep verified settings',
  'mutation.busy': 'Another operation is running. Wait for it to finish before changing settings.',
  'mutation.noCycle': 'A current experiment cycle is required for this action.',
  'mutation.alreadyApplied': 'The plan is not awaiting its first application.',
  'mutation.rollbackUnavailable': 'Rollback is available only while the current cycle may need recovery.',
  'mutation.keepUnavailable': 'Settings can be kept only after verified application and at the after-measurement stage.',
  'mutation.requesting': 'Preparing a confirmation…',
  'mutation.confirmTitle': 'Review this settings change',
  'mutation.confirm': 'Confirm and continue',
  'mutation.cancel': 'Cancel',
  'mutation.expires': 'Confirmation expires at {time}.',
  'mutation.expired': 'This confirmation has expired. Request a new review.',
  'mutation.stale': 'The target or plan changed. Request a new review.',
  'mutation.failed': 'The action could not be completed. Review the current state before trying again.',
  'mutation.approvedNotice': 'Approval applies to this plan revision only. A changed plan needs a new review.',
  'mutation.keepNotice': 'This records your choice to keep verified settings; it does not claim a performance improvement.',
  'mutation.unavailable': 'Settings changes are unavailable until the backend confirms the current plan and target.',
} as const;

export const ruMutationMessages: Record<keyof typeof enMutationMessages, string> = {
  'mutation.closedReverted': 'Исходные настройки восстановлены и проверены. Цикл завершён; измерения доступны в результатах и истории. Для нового эксперимента создайте новый план.',
  'mutation.closedKept': 'Проверенные настройки сохранены. Цикл завершён, резервная копия остаётся в истории. Для нового эксперимента создайте новый план.',
  'mutation.title': 'План и настройки',
  'mutation.planUnavailable': 'Актуальный план недоступен.',
  'mutation.invalidTarget': 'Подключитесь к проверенной цели и просмотрите план для этого сеанса.',
  'mutation.baselineRequired': 'Перед применением настроек нужен достоверный исходный замер.',
  'mutation.planPreview': 'Этот план доступен только для просмотра и не может быть применён.',
  'mutation.planNotApplicable': 'Устраните пропуски или ошибки проверки плана перед продолжением.',
  'mutation.planApproved': 'Этот план уже подтверждён.',
  'mutation.approve': 'Подтвердить план',
  'mutation.approving': 'Подтверждение…',
  'mutation.apply': 'Просмотреть применение',
  'mutation.rollback': 'Просмотреть откат',
  'mutation.keep': 'Оставить проверенные настройки',
  'mutation.busy': 'Выполняется другая операция. Дождитесь её завершения перед изменением настроек.',
  'mutation.noCycle': 'Для этого действия нужен текущий цикл эксперимента.',
  'mutation.alreadyApplied': 'План не ожидает первого применения.',
  'mutation.rollbackUnavailable': 'Откат доступен, только пока текущему циклу может потребоваться восстановление.',
  'mutation.keepUnavailable': 'Оставить настройки можно только после подтверждённого применения на этапе повторного замера.',
  'mutation.requesting': 'Подготавливаем подтверждение…',
  'mutation.confirmTitle': 'Проверьте изменение настроек',
  'mutation.confirm': 'Подтвердить и продолжить',
  'mutation.cancel': 'Отмена',
  'mutation.expires': 'Подтверждение истекает в {time}.',
  'mutation.expired': 'Срок подтверждения истёк. Запросите новое подтверждение.',
  'mutation.stale': 'Цель или план изменились. Запросите новое подтверждение.',
  'mutation.failed': 'Не удалось выполнить действие. Проверьте текущее состояние перед повтором.',
  'mutation.approvedNotice': 'Подтверждение относится только к этой версии плана. Для изменённого плана нужно новое подтверждение.',
  'mutation.keepNotice': 'Это фиксирует решение оставить проверенные настройки, но не утверждает улучшение производительности.',
  'mutation.unavailable': 'Изменение настроек недоступно, пока backend не подтвердит текущий план и цель.',
};

export function translateMutation(locale: Locale, key: keyof typeof enMutationMessages, parameters: Readonly<Record<string, string | number>> = {}): string {
  const template = (locale === 'ru-RU' ? ruMutationMessages : enMutationMessages)[key];
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = parameters[name];
    if (typeof value === 'number' && Number.isFinite(value)) return new Intl.NumberFormat(locale).format(value);
    return typeof value === 'string' ? value : '—';
  });
}
