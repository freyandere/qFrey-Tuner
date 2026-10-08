import { useEffect, useState } from 'react';
import type { Plan, PlanSelection, PreferenceValue, Recommendation } from '../contracts/domain';
import type { Locale } from '../contracts/protocol';
import { en, formatNumber, parseDecimal, translate } from '../i18n/messages';
import { Badge, Card, StatusBanner } from './base';

type Props = { plan: Plan | null; onSelections: (value: PlanSelection[]) => void; busy: boolean; locale: Locale };
type Filter = 'changed' | 'all' | 'omitted' | 'warnings';
type ReviewCategory = 'speed' | 'connections' | 'queue' | 'resources' | 'protocol';
type CategoryFilter = 'all' | ReviewCategory;
type View = 'cards' | 'table';
const categories: ReviewCategory[] = ['speed', 'connections', 'queue', 'resources', 'protocol'];
const equal = (left: PreferenceValue | null, right: PreferenceValue) => Object.is(left, right);
const groups = (plan: Plan) => [...new Set(plan.recommendations.map(item => item.groupId))];
const queueKeys = new Set(['max_active_downloads', 'max_active_uploads', 'max_active_torrents', 'queueing_enabled']);
const speedKeys = new Set(['up_limit', 'dl_limit', 'send_buffer_watermark', 'send_buffer_low_watermark',
  'send_buffer_watermark_factor', 'connection_speed', 'limit_utp_rate']);
const resourceKeys = new Set(['disk_cache', 'disk_io_read_mode', 'disk_io_write_mode', 'enable_coalesce_read_write',
  'preallocate_all', 'async_io_threads']);
const protocolKeys = new Set(['current_network_interface', 'bittorrent_protocol', 'encryption', 'anonymous_mode', 'dht', 'pex', 'lsd']);

function categoryForApiKey(apiKey: string): ReviewCategory | null {
  if (queueKeys.has(apiKey)) return 'queue';
  if (speedKeys.has(apiKey)) return 'speed';
  if (resourceKeys.has(apiKey)) return 'resources';
  if (protocolKeys.has(apiKey)) return 'protocol';
  return null;
}

function reviewCategory(item: Recommendation): ReviewCategory {
  const explicit = categoryForApiKey(item.apiKey);
  if (explicit) return explicit;
  switch (item.category) {
    case 'throughput': return 'speed';
    case 'resources': return 'resources';
    case 'connectivity': return 'protocol';
    default: return 'connections';
  }
}

export function createPlanSelections(plan: Plan, selectedGroup?: string, groupSelected?: boolean,
  overrideKey?: string, overrideValue?: PreferenceValue): PlanSelection[] {
  return groups(plan).map(groupId => {
    const members = plan.recommendations.filter(item => item.groupId === groupId);
    const overrides: Record<string, PreferenceValue> = {};
    for (const item of members) {
      if (item.apiKey === overrideKey && overrideValue !== undefined) {
        if (!equal(overrideValue, item.proposedValue)) overrides[item.apiKey] = overrideValue;
      } else if (item.reason.key === 'recommendations.reason.manualOverride') overrides[item.apiKey] = item.proposedValue;
    }
    return { groupId, selected: groupId === selectedGroup ? groupSelected ?? members.every(item => item.selected) : members.every(item => item.selected), overrides };
  });
}

function unit(item: Recommendation, t: (key: string) => string): string {
  if (item.valueType === 'enum' || item.valueType === 'bool' || item.valueType === 'interface') return '';
  if (item.unit === 'bytesPerSecond') return t('units.kibPerSecond');
  if (item.unit === 'kibibytes') return t('units.kib');
  if (item.unit === 'mebibytes') return t('units.mebibytes');
  if (item.unit === 'connectionsPerSecond') return t('units.connectionsPerSecond');
  if (item.unit === 'percent') return t('units.percent');
  return '';
}

function displayValue(item: Recommendation, value: PreferenceValue | null, locale: Locale,
  t: (key: string) => string): string {
  if (value === null) return '—';
  if (typeof value === 'boolean') return t(value ? 'planning.value.yes' : 'planning.value.no');
  const option = item.allowedValues.find(candidate => equal(candidate.value, value));
  if (option) return option.labelKey.startsWith('settings.interfaces.') ? String(value) : t(option.labelKey);
  if (typeof value === 'number') {
    const shown = item.apiKey === 'up_limit' || item.apiKey === 'dl_limit' ? value / 1024 : value;
    return formatNumber(locale, shown);
  }
  return value;
}

function PlanNumberEditor({ item, value, locale, busy, t, onCommit }: {
  item: Recommendation; value: number; locale: Locale; busy: boolean; t: (key: string, params?: Record<string, string | number>) => string;
  onCommit: (value: number) => void;
}) {
  const binarySpeed = item.apiKey === 'up_limit' || item.apiKey === 'dl_limit';
  const range = item.allowedRange!;
  const [text, setText] = useState(String(binarySpeed ? value / 1024 : value));
  const parsed = parseDecimal(text);
  const canonical = parsed === null ? null : binarySpeed ? parsed * 1024 : parsed;
  const valid = canonical !== null && Number.isFinite(canonical) && Number.isInteger(canonical) && canonical >= range.minimum
    && canonical <= range.maximum && (canonical - range.minimum) % range.step === 0;
  useEffect(() => { setText(String(binarySpeed ? value / 1024 : value)); }, [value, binarySpeed]);
  const commit = () => { if (valid && canonical !== null) onCommit(canonical); };
  return <span style={{ display: 'inline-grid', gap: 4, verticalAlign: 'middle' }}>
    <span style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
      <input type="text" inputMode="decimal" aria-label={`${t('recommendations.proposed')} ${t(item.titleKey)}`} value={text} aria-invalid={!valid}
        disabled={busy || !item.editable || item.supportStatus !== 'supported'} onChange={event => setText(event.target.value)}
        onBlur={commit} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } }} />
      <small>{t(item.unit === 'bytesPerSecond' ? 'units.kibPerSecond' : item.unit === 'kibibytes' ? 'units.kib'
        : item.unit === 'mebibytes' ? 'units.mebibytes' : item.unit === 'connectionsPerSecond' ? 'units.connectionsPerSecond'
          : item.unit === 'percent' ? 'units.percent' : 'metrics.count')}</small>
      <small>{t('recommendations.allowedRange', { minimum: formatNumber(locale, binarySpeed ? range.minimum / 1024 : range.minimum),
        maximum: formatNumber(locale, binarySpeed ? range.maximum / 1024 : range.maximum), step: formatNumber(locale, binarySpeed ? range.step / 1024 : range.step) })}</small>
    </span>
    {!valid && <small className="field-error" role="alert">{t('recommendations.invalidOverride')}</small>}
  </span>;
}

export function RecommendationList({ plan, onSelections, busy, locale }: Props) {
  const [category, setCategory] = useState<CategoryFilter>('all');
  const [filter, setFilter] = useState<Filter>('changed');
  const [query, setQuery] = useState('');
  const [view, setView] = useState<View>('cards');
  const [advanced, setAdvanced] = useState(false);
  const t = (key: string, parameters: Record<string, string | number> = {}) => translate(locale, key, parameters);
  const label = (key: string, parameters: Record<string, string | number> = {}) => Object.hasOwn(en, key) ? t(key, parameters) : t('recommendations.unknownReason');

  useEffect(() => {
    setCategory('all'); setFilter('changed'); setQuery('');
  }, [plan?.id, plan?.revision]);

  if (!plan) return <Card title={t('recommendations.review.title')}><p>{t('recommendations.noPlan')}</p></Card>;

  const normalizedQuery = query.trim().toLocaleLowerCase(locale);
  const omittedKeys = new Set(plan.omissions.map(item => item.apiKey));
  const matchesText = (item: Recommendation) => !normalizedQuery
    || t(item.titleKey).toLocaleLowerCase(locale).includes(normalizedQuery)
    || item.apiKey.toLocaleLowerCase(locale).includes(normalizedQuery);
  const isOmitted = (item: Recommendation) => item.supportStatus !== 'supported' || omittedKeys.has(item.apiKey);
  const visible = plan.recommendations.filter(item => {
    const categoryMatch = category === 'all' || reviewCategory(item) === category;
    const textMatch = matchesText(item);
    const filterMatch = filter === 'all' || filter === 'changed' && item.currentValue !== null && !equal(item.currentValue, item.proposedValue) && !isOmitted(item)
      || filter === 'omitted' && isOmitted(item)
      || filter === 'warnings' && item.cautionCodes.length > 0;
    return categoryMatch && textMatch && filterMatch;
  });
  const visibleOmissions = plan.omissions.filter(item => filter === 'all' || filter === 'omitted')
    .filter(item => category === 'all' || categoryForApiKey(item.apiKey) === category)
    .filter(item => !normalizedQuery || item.apiKey.toLocaleLowerCase(locale).includes(normalizedQuery)
      || label(`recommendations.omission.${item.reasonCode}`).toLocaleLowerCase(locale).includes(normalizedQuery));
  const commit = (item: Recommendation, value: PreferenceValue) => onSelections(createPlanSelections(plan, undefined, undefined, item.apiKey, value));
  const setSelected = (groupId: string, selected: boolean) => onSelections(createPlanSelections(plan, groupId, selected));

  const renderReason = (item: Recommendation) => <div className="recommendation-reason">
    <p>{label(item.reason.key, item.reason.parameters)}</p>
    {item.cautionCodes.length > 0 && <ul>{item.cautionCodes.map(code => <li key={code}>
      {label(code, item.reason.parameters)}{advanced && <> <small>({code})</small></>}
    </li>)}</ul>}
    {advanced && <p><small>{t('recommendations.review.reasonCode')}: <code>{item.reason.key}</code></small></p>}
  </div>;

  const renderProposed = (item: Recommendation) => {
    if (!advanced || !item.editable || item.supportStatus !== 'supported')
      return <>{displayValue(item, item.proposedValue, locale, t)} {unit(item, t)}</>;
    const value = item.proposedValue;
    let editor: React.ReactNode;
    if ((item.valueType === 'enum' || item.valueType === 'interface' || item.apiKey === 'random_port')) {
      const selectedOption = item.allowedValues.find(option => equal(option.value, value));
      editor = <select aria-label={`${t('recommendations.proposed')} ${t(item.titleKey)}`}
        value={selectedOption ? `${typeof selectedOption.value}:${String(selectedOption.value)}` : ''} disabled={busy}
        onChange={event => {
          const option = item.allowedValues.find(candidate => `${typeof candidate.value}:${String(candidate.value)}` === event.target.value);
          if (option) commit(item, option.value);
        }}>
        {item.allowedValues.map(option => <option key={`${typeof option.value}:${String(option.value)}`} value={`${typeof option.value}:${String(option.value)}`}>
          {option.labelKey.startsWith('settings.interfaces.') ? String(option.value) : label(option.labelKey)}
        </option>)}
      </select>;
    } else if (item.valueType === 'bool' && typeof value === 'boolean') {
      editor = <label><input type="checkbox" aria-label={`${t('recommendations.proposed')} ${t(item.titleKey)}`} checked={value} disabled={busy}
        onChange={event => commit(item, event.target.checked)} />{displayValue(item, value, locale, t)}</label>;
    } else if (item.valueType === 'number' && typeof value === 'number' && item.allowedRange) {
      editor = <PlanNumberEditor item={item} value={value} locale={locale} busy={busy} t={t} onCommit={next => commit(item, next)} />;
    } else editor = <>{displayValue(item, value, locale, t)} {unit(item, t)}</>;
    return <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
      {editor}
      {item.reason.key === 'recommendations.reason.manualOverride' && <button type="button" disabled={busy}
        onClick={() => commit(item, item.proposedValue)}>{t('recommendations.review.reset')}</button>}
    </span>;
  };

  const renderSelection = (item: Recommendation) => {
    const members = plan.recommendations.filter(candidate => candidate.groupId === item.groupId);
    const selected = members.every(candidate => candidate.selected);
    return <label className="recommendation-selection"><input type="checkbox" checked={selected} disabled={busy || isOmitted(item)}
      aria-label={`${t('recommendations.include')}: ${t(item.titleKey)}`} onChange={event => setSelected(item.groupId, event.target.checked)} />
      {t('recommendations.include')}</label>;
  };

  const renderCard = (item: Recommendation) => <article className="card recommendation-card" key={item.id}>
    <header className="recommendation-card-header">
      <h3>{t(item.titleKey)}</h3><div className="recommendation-badges"><Badge tone="info">{t('recommendations.heuristic')}</Badge>
      <Badge>{t(`recommendations.review.category.${reviewCategory(item)}`)}</Badge>
      {item.supportStatus !== 'supported' && <Badge tone={item.supportStatus === 'missingRequired' ? 'error' : 'warning'}>
        {t(`recommendations.support.${item.supportStatus}`)}</Badge>}
      </div>
    </header>
    {renderReason(item)}
    <dl className="recommendation-values">
      <div><dt>{t('recommendations.current')}</dt><dd>{displayValue(item, item.currentValue, locale, t)} {item.currentValue === null ? '' : unit(item, t)}</dd></div>
      <div><dt>{t('recommendations.proposed')}</dt><dd>{renderProposed(item)}</dd></div>
    </dl>
    {advanced && <p><small>{t('recommendations.review.apiKey')}: <code>{item.apiKey}</code></small></p>}
    {renderSelection(item)}
    {advanced && (() => {
      const groupItems = plan.recommendations.filter(candidate => candidate.groupId === item.groupId);
      return groupItems.length > 1 ? <small>{t('recommendations.review.group')}: {groupItems.map(candidate => t(candidate.titleKey)).join(', ')}</small> : null;
    })()}
  </article>;

  return <Card title={t('recommendations.review.title')}>
    {plan.previewOnly && <StatusBanner severity="warning">{t('recommendations.previewOnly')} {t('recommendations.noBaseline')}</StatusBanner>}
    {!plan.applicable && plan.blockReasonCodes.length > 0 && <StatusBanner severity="warning">
      <p>{t('recommendations.blocked')}</p><strong>{t('recommendations.blockReasons')}</strong><ul>{plan.blockReasonCodes.map(code => <li key={code}>
        {label(`recommendations.blockReason.${code}`)}{advanced && <> <small>({code})</small></>}
      </li>)}</ul>
    </StatusBanner>}
    <div className="recommendation-controls">
      <div className="recommendation-control-group" role="group" aria-label={t('recommendations.review.viewMode')}>
        <span className="field-label">{t('recommendations.review.viewMode')}</span>
        <div className="form-actions">
          <button type="button" aria-pressed={!advanced} onClick={() => setAdvanced(false)}>{t('recommendations.review.basic')}</button>
          <button type="button" aria-pressed={advanced} onClick={() => setAdvanced(true)}>{t('recommendations.review.advanced')}</button>
        </div>
      </div>
      <div className="recommendation-control-group" role="group" aria-label={t('recommendations.review.layout')}>
        <span className="field-label">{t('recommendations.review.layout')}</span>
        <div className="form-actions">
          <button type="button" aria-pressed={view === 'cards'} onClick={() => setView('cards')}>{t('recommendations.review.cards')}</button>
          <button type="button" aria-pressed={view === 'table'} onClick={() => setView('table')}>{t('recommendations.review.table')}</button>
        </div>
      </div>
    </div>
    <div className="recommendation-filters">
    <label className="field">{t('recommendations.review.search')}
      <input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder={t('recommendations.review.searchHint')} />
    </label>
    <label className="field">{t('recommendations.review.categoryLabel')}<select value={category} onChange={event => setCategory(event.target.value as CategoryFilter)}>
      <option value="all">{t('recommendations.review.category.all')}</option>{categories.map(value =>
        <option key={value} value={value}>{t(`recommendations.review.category.${value}`)}</option>)}
    </select></label>
    <label className="field">{t('recommendations.review.filterLabel')}<select value={filter} onChange={event => setFilter(event.target.value as Filter)}>
      {(['changed', 'all', 'omitted', 'warnings'] as const).map(value =>
        <option key={value} value={value}>{t(`recommendations.filter.${value}`)}</option>)}
    </select></label>
    </div>
    <p className="recommendation-count" aria-live="polite">{t('recommendations.review.count', { count: visible.length + visibleOmissions.length })}</p>
    {visible.length === 0 && visibleOmissions.length === 0 && <p>{t('recommendations.none')}</p>}
    {view === 'cards' && <div className="recommendation-cards">{visible.map(renderCard)}
      {visibleOmissions.length > 0 && <section aria-labelledby="recommendation-omissions">
        <h3 id="recommendation-omissions">{t('recommendations.omissions')}</h3>
        <div style={{ display: 'grid', gap: 12 }}>{visibleOmissions.map(item => <article className="card" key={item.apiKey}>
          <h4>{t(`recommendations.${item.apiKey}`)}</h4>
          <p>{t(item.required ? 'recommendations.omission.required' : 'recommendations.omission.optional')}</p>
          <p>{label(`recommendations.omission.${item.reasonCode}`)}{advanced && <> <small>({item.reasonCode})</small></>}</p>
          {advanced && <p><small>{t('recommendations.review.apiKey')}: <code>{item.apiKey}</code></small></p>}
        </article>)}</div>
      </section>}
    </div>}
    {view === 'table' && <div className="metric-table" role="region" aria-label={t('recommendations.review.table')} tabIndex={0}>
      <table><caption>{t('recommendations.review.tableCaption')}</caption><thead><tr>
        <th scope="col">{t('recommendations.review.tableCategory')}</th><th scope="col">{t('recommendations.review.tableName')}</th>
        <th scope="col">{t('recommendations.current')}</th><th scope="col">{t('recommendations.proposed')}</th>
        <th scope="col">{t('recommendations.review.tableReason')}</th><th scope="col">{t('recommendations.include')}</th>
      </tr></thead><tbody>
        {visible.map(item => <tr key={item.id}>
          <td>{t(`recommendations.review.category.${reviewCategory(item)}`)}</td>
          <th scope="row">{t(item.titleKey)}{advanced && <><br /><code>{item.apiKey}</code></>}</th>
          <td>{displayValue(item, item.currentValue, locale, t)} {item.currentValue === null ? '' : unit(item, t)}</td>
          <td>{renderProposed(item)}</td><td>{label(item.reason.key, item.reason.parameters)}
            {item.cautionCodes.length > 0 && <ul>{item.cautionCodes.map(code => <li key={code}>{label(code, item.reason.parameters)}{advanced && <> <small>({code})</small></>}</li>)}</ul>}
            {advanced && <small>{t('recommendations.review.reasonCode')}: <code>{item.reason.key}</code></small>}
          </td><td>{renderSelection(item)}</td>
        </tr>)}
        {visibleOmissions.map(item => <tr key={`omission-${item.apiKey}`}>
          <td>{t('recommendations.review.category.limitations')}</td><th scope="row">{t(`recommendations.${item.apiKey}`)}{advanced && <><br /><code>{item.apiKey}</code></>}</th>
          <td>—</td><td>{t(item.required ? 'recommendations.omission.required' : 'recommendations.omission.optional')}</td>
          <td>{label(`recommendations.omission.${item.reasonCode}`)}{advanced && <> <small>({item.reasonCode})</small></>}</td><td>—</td>
        </tr>)}
      </tbody></table>
    </div>}
  </Card>;
}
