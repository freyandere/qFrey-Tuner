import type { ExperimentSummary, HistoricalValue, ResultCard, ResultCategory, ResultContent } from '../contracts/domain';
import type { ApplyStatus, Locale, MetricUnit } from '../contracts/protocol';
import { en, formatDate, formatNumber, translate } from '../i18n/messages';
import { resultMessage, resultReason, resultScopeKey, resultUnitKey } from '../i18n/resultMessages';
import { Badge, Card, StatusBanner } from './base';

type Props = { summary: ExperimentSummary | null; locale: Locale; historical: boolean };
const categories: ResultCategory[] = ['throughput', 'stability', 'ramp', 'connectivity', 'resources', 'configuration', 'limitations'];

export function ResultCards({ summary, locale, historical }: Props) {
  const t = (key: string, parameters: Readonly<Record<string, string | number>> = {}) => resultMessage(locale, key, parameters);
  if (!summary) return <Card title={t('results.title')}><p>{t('results.summaryUnavailable')}</p></Card>;

  return <Card title={t('results.title')}>
    <StatusBanner>{t(historical ? 'results.historicalBanner' : 'results.currentBanner')}</StatusBanner>
    <dl>
      <div><dt>{t('results.cycleId')}</dt><dd>{summary.cycleId ?? '—'}</dd></div>
      <MeasurementLine label={t('results.baseline')} measurement={summary.baseline} locale={locale} />
      <MeasurementLine label={t('results.after')} measurement={summary.after} locale={locale} />
    </dl>
    {summary.results.length === 0 && <p>{t('results.empty')}</p>}
    {categories.map(category => {
      const cards = summary.results.filter(result => result.category === category);
      return cards.length === 0 ? null : <section key={category}>
        <h3>{t(`results.category.${category}`)}</h3>
        {cards.map(result => <ResultDetail key={result.id} result={result} locale={locale} historical={historical} />)}
      </section>;
    })}
  </Card>;
}

function MeasurementLine({ label, measurement, locale }: {
  label: string; measurement: ExperimentSummary['baseline']; locale: Locale;
}) {
  const t = (key: string) => resultMessage(locale, key);
  if (!measurement) return <div><dt>{label}</dt><dd>{t('results.notMeasured')}</dd></div>;
  const statusKey = `results.status.${measurement.status}`;
  const status = resultMessage(locale, statusKey);
  const measured = Number.isFinite(Date.parse(measurement.startedUtc))
    ? formatDate(locale, new Date(measurement.startedUtc), { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })
    : t('results.valueUnavailable');
  return <div><dt>{label}</dt><dd><Badge tone={measurement.status === 'valid' ? 'info' : measurement.status === 'invalid' ? 'warning' : 'neutral'}>{status}</Badge>
    {' · '}{measured}{' · '}{t(resultScopeKey(measurement.scope))}{' · '}{t('results.analysisVersion')}: {measurement.analysisVersion}</dd></div>;
}

function ResultDetail({ result, locale, historical }: { result: ResultCard; locale: Locale; historical: boolean }) {
  const t = (key: string, parameters: Readonly<Record<string, string | number>> = {}) => resultMessage(locale, key, parameters);
  const message = t(result.explanation.key, result.explanation.parameters);
  const title = resultMessage(locale, result.titleKey);
  const contextKey = historical || result.context === 'historical' ? 'results.historicalBanner' : 'results.currentBanner';
  return <article className="card">
    <h4>{title}</h4>
    <p>{message}</p>
    <dl>
      <div><dt>{t('results.kind.' + result.kind)}</dt><dd>{t('results.severity.' + result.severity)}</dd></div>
      <div><dt>{t('results.source')}</dt><dd>{t('results.evidence.' + result.evidence)}</dd></div>
      <div><dt>{t('results.context')}</dt><dd>{t(contextKey)}</dd></div>
      {result.measuredAtUtc && <div><dt>{t('results.measuredAt')}</dt><dd>{formatUtc(locale, result.measuredAtUtc)}</dd></div>}
    </dl>
    <ResultContentView content={result.content} locale={locale} />
  </article>;
}

function ResultContentView({ content, locale }: { content: ResultContent; locale: Locale }) {
  switch (content.type) {
    case 'comparison': {
      const comparison = content.comparison;
      const verdictTone = comparison.verdict === 'observedRegression' ? 'warning'
        : comparison.verdict === 'observedImprovement' ? 'info' : 'neutral';
      return <>
        <dl>
          <div><dt>{resultMessage(locale, 'results.before')}</dt><dd><HistoricalValueView value={comparison.before} unit={comparison.unit} locale={locale} /></dd></div>
          <div><dt>{resultMessage(locale, 'results.afterValue')}</dt><dd><HistoricalValueView value={comparison.after} unit={comparison.unit} locale={locale} /></dd></div>
          <div><dt>{resultMessage(locale, 'results.absoluteDelta')}</dt><dd>{comparison.absoluteDelta === null
            ? resultMessage(locale, 'results.notMeasured') : `${formatSigned(locale, comparison.absoluteDelta)} ${resultMessage(locale, resultUnitKey(comparison.unit))}`}</dd></div>
          <div><dt>{resultMessage(locale, 'results.relativeDelta')}</dt><dd>{comparison.relativeDeltaPercent === null
            ? resultMessage(locale, 'results.notMeasured') : `${formatSigned(locale, comparison.relativeDeltaPercent)}${resultMessage(locale, resultUnitKey('percent'))}`}</dd></div>
          <div><dt>{resultMessage(locale, 'results.verdict')}</dt><dd><Badge tone={verdictTone}>{resultMessage(locale, `results.${comparison.verdict}`)}</Badge></dd></div>
          <div><dt>{resultMessage(locale, 'results.scope')}</dt><dd>{resultMessage(locale, resultScopeKey(comparison.scope))}</dd></div>
        </dl>
        <StatusBanner>{resultMessage(locale, 'results.comparisonCaveat')}</StatusBanner>
        <ReasonList reasons={comparison.reasonCodes} locale={locale} />
      </>;
    }
    case 'observation':
      return <dl>
        <div><dt>{resultMessage(locale, 'results.scope')}</dt><dd>{resultMessage(locale, resultScopeKey(content.scope))}</dd></div>
        <div><dt>{resultMessage(locale, 'results.details')}</dt><dd><HistoricalValueView value={content.value} unit={content.unit} locale={locale} /></dd></div>
      </dl>;
    case 'change':
      return <dl>
        <div><dt>{resultMessage(locale, 'results.setting')}</dt><dd>{settingLabel(locale, content.apiKey)}</dd></div>
        <div><dt>{resultMessage(locale, 'results.before')}</dt><dd>{preferenceValue(locale, content.before)}</dd></div>
        <div><dt>{resultMessage(locale, 'results.intended')}</dt><dd>{preferenceValue(locale, content.intended)}</dd></div>
        <div><dt>{resultMessage(locale, 'results.observed')}</dt><dd>{content.observed === null
          ? resultMessage(locale, 'results.valueUnavailable') : preferenceValue(locale, content.observed)}</dd></div>
        <div><dt>{resultMessage(locale, 'results.changeStatus')}</dt><dd>{resultMessage(locale, applyStatusKey(content.status))}</dd></div>
      </dl>;
    case 'limitation':
      return <ReasonList reasons={content.reasonCodes} locale={locale} />;
    case 'recovery':
      return <>
        <dl><div><dt>{resultMessage(locale, 'results.backupAvailable')}</dt><dd>{resultMessage(locale, content.backupAvailable ? 'results.yes' : 'results.no')}</dd></div></dl>
        <TextList label={resultMessage(locale, 'results.conflicts')} values={content.conflictKeys} empty={resultMessage(locale, 'results.noConflicts')} code />
        <TextList label={resultMessage(locale, 'results.nextSteps')} values={content.actionIds} empty={resultMessage(locale, 'results.noNextSteps')} code />
      </>;
  }
}

function HistoricalValueView({ value, unit, locale }: { value: HistoricalValue; unit: MetricUnit; locale: Locale }) {
  if (value.quality === 'valid') {
    if (Number.isFinite(value.value)) return <>{formatNumber(locale, value.value)} {resultMessage(locale, resultUnitKey(unit))}
      {' · '}{formatUtc(locale, value.measuredAtUtc)}</>;
    return <>{resultMessage(locale, 'results.invalid')}<ReasonList reasons={[]} locale={locale} /></>;
  }
  const label = value.quality === 'invalid' ? resultMessage(locale, 'results.invalid') : resultMessage(locale, 'results.notMeasured');
  return <>{label}<ReasonList reasons={value.reasonCodes} locale={locale} /></>;
}

function ReasonList({ reasons, locale }: { reasons: readonly string[]; locale: Locale }) {
  if (reasons.length === 0) return null;
  return <div><strong>{resultMessage(locale, 'results.reason')}</strong><ul>{reasons.map((reason, index) =>
    <li key={`${reason}-${index}`}>{resultReason(locale, reason)}</li>)}</ul></div>;
}

function TextList({ label, values, empty, code = false }: { label: string; values: readonly string[]; empty: string; code?: boolean }) {
  return <div><strong>{label}</strong>{values.length === 0 ? <p>{empty}</p> : <ul>{values.map((value, index) =>
    <li key={`${value}-${index}`}>{code ? <code>{value}</code> : value}</li>)}</ul>}</div>;
}

function settingLabel(locale: Locale, apiKey: string): string {
  const key = `recommendations.${apiKey}`;
  return Object.hasOwn(en, key) ? translate(locale, key) : apiKey;
}

function preferenceValue(locale: Locale, value: number | boolean | string): string {
  if (typeof value === 'boolean') return resultMessage(locale, value ? 'planning.value.yes' : 'planning.value.no');
  return typeof value === 'number' ? formatNumber(locale, value) : value;
}

function applyStatusKey(status: ApplyStatus): string { return `results.apply.${status}`; }
function formatSigned(locale: Locale, value: number): string { return formatNumber(locale, value, { signDisplay: 'always', maximumFractionDigits: 3 }); }
function formatUtc(locale: Locale, value: string): string {
  const instant = new Date(value);
  return Number.isFinite(instant.getTime())
    ? formatDate(locale, instant, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })
    : resultMessage(locale, 'results.valueUnavailable');
}
