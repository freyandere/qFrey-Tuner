// Development-only entry: Vite's release input is index.html, which never imports this gallery.
import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Badge, Button, Card, Dialog, Field, StatusBanner, Tabs } from './components/base';
import { translate } from './i18n/messages';
import type { Locale, ThemePreference } from './contracts/protocol';
import './styles/app.css';

function Gallery() {
  const [locale, setLocale] = useState<Locale>('en-US');
  const [theme, setTheme] = useState<ThemePreference>('system');
  const [tab, setTab] = useState('results');
  const [open, setOpen] = useState(false);
  const t = (key: string) => translate(locale, key);
  useEffect(() => { document.documentElement.dataset.theme = theme; document.documentElement.lang = locale; }, [theme, locale]);
  return <main className="component-gallery"><h1>qFrey-Tuner · {t('app.demo')}</h1>
    <Field id="gallery-locale" label={t('language')}><select id="gallery-locale" value={locale} onChange={e => setLocale(e.target.value as Locale)}><option value="en-US">English</option><option value="ru-RU">Русский</option></select></Field>
    <Field id="gallery-theme" label={t('theme')}><select id="gallery-theme" value={theme} onChange={e => setTheme(e.target.value as ThemePreference)}>{(['system', 'dark', 'light'] as const).map(v => <option key={v} value={v}>{t(v)}</option>)}</select></Field>
    <Card title={t('recommendations')}><Button onClick={() => setOpen(true)}>{t('confirmations.confirm')}</Button> <Button disabled>{t('confirmations.confirm')}</Button>
      {(['neutral', 'info', 'warning', 'error'] as const).map(tone => <Badge key={tone} tone={tone}>{t(tone === 'error' ? 'errors.recoveryRequired' : tone === 'warning' ? 'plan.preview' : 'safety')}</Badge>)}</Card>
    <Card title={t('setup')}><Field id="gallery-speed" label={t('setup.downloadMbps')} hint={t('setup.manual')} error={t('errors.invalidOverride')}>
      <input id="gallery-speed" inputMode="decimal" defaultValue="-1" aria-invalid="true" aria-describedby="gallery-speed-hint gallery-speed-error" /></Field></Card>
    <StatusBanner>{t('safety')}</StatusBanner><StatusBanner severity="warning">{t('plan.preview')}</StatusBanner><StatusBanner severity="error">{t('errors.recoveryRequired')}</StatusBanner>
    <Tabs label={t('results')} selected={tab} onSelect={setTab} items={['results', 'history'].map(id => ({ id, label: t(id), panelId: `${id}-panel` }))} />
    {['results', 'history'].map(id => <section key={id} id={`${id}-panel`} role="tabpanel" aria-labelledby={`${id}-tab`} hidden={id !== tab}><p>{t('results.historical')}</p></section>)}
    <Dialog open={open} title={t('confirmations.confirm')} onClose={() => setOpen(false)}><p>{translate(locale, 'confirmations.apply', { count: 2, endpoint: 'https://demo.invalid' })}</p><Button onClick={() => setOpen(false)}>{t('confirmations.cancel')}</Button></Dialog>
  </main>;
}
createRoot(document.getElementById('root')!).render(<Gallery />);
