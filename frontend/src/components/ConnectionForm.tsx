import { useState } from 'react';
import type { Authentication } from '../contracts/domain';
import type { AppSnapshot } from '../contracts/protocol';
import { sendCommand } from '../bridge/client';
import { translate } from '../i18n/messages';

export function ConnectionForm({ snapshot, onSnapshot, onError, onEndpointEdited, onConnected }: {
  snapshot: AppSnapshot; onSnapshot: (value: AppSnapshot) => void; onError: (key: string | null) => void;
  onEndpointEdited: () => void; onConnected: () => void;
}) {
  const [endpoint, setEndpoint] = useState('');
  const [method, setMethod] = useState<'password' | 'apiKey' | 'bypass'>('password');
  const [username, setUsername] = useState('');
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const locked = busy || snapshot.activeOperation !== null;
  const t = (key: string) => translate(snapshot.preferences.locale, key);
  async function submit(disconnect = false) {
    if (locked) { onError('errors.operationConflict'); return; }
    setBusy(true); onError(null);
    const auth: Authentication = method === 'password' ? { kind: method, username, password: secret }
      : method === 'apiKey' ? { kind: method, apiKey: secret } : { kind: method };
    setSecret(''); setUsername('');
    try {
      onSnapshot(await sendCommand(disconnect ? { command: 'Disconnect', payload: {} }
        : { command: 'Connect', payload: { endpoint: endpoint.trim(), auth } }));
      if (!disconnect) onConnected();
    } catch (error) {
      const key = error instanceof Error ? error.message : 'errors.unknown';
      // A failed reconnect invalidates the old target. Refresh authoritative state.
      try { onSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } })); } catch { /* Keep the original actionable failure. */ }
      onError(key);
    } finally { setBusy(false); }
  }
  return <section className="card"><h2>{t('connection.title')}</h2>
    {snapshot.target && <p>{snapshot.target.endpoint} · qBittorrent {snapshot.target.qbittorrentVersion} · API {snapshot.target.apiVersion} · libtorrent {snapshot.target.libtorrentVersion}</p>}
    <form onSubmit={event => { event.preventDefault(); void submit(); }}>
      <label className="field">{t('connection.endpoint')}<input type="url" value={endpoint} onChange={event => {
        setEndpoint(event.target.value); onEndpointEdited(); if (snapshot.target) void submit(true);
      }} required disabled={locked} autoComplete="off" placeholder="http://127.0.0.1:8080" /></label>
      <label className="field">{t('connection.auth')}<select value={method} disabled={locked} onChange={event => { setMethod(event.target.value as typeof method); setSecret(''); }}>
        <option value="password">{t('connection.password')}</option><option value="apiKey">{t('connection.apiKey')}</option><option value="bypass">{t('connection.bypass')}</option>
      </select></label>
      {method === 'password' && <label className="field">{t('connection.username')}<input value={username} onChange={event => setUsername(event.target.value)} required disabled={locked} autoComplete="off" /></label>}
      {method !== 'bypass' && <label className="field">{t(method === 'password' ? 'connection.password' : 'connection.apiKey')}<input type="password" value={secret} onChange={event => setSecret(event.target.value)} required disabled={locked} autoComplete="off" /></label>}
      <p>{t('connection.credentialsMemory')}</p>
      <div className="form-actions"><button type="submit" disabled={locked}>{t(busy ? 'connection.connecting' : 'connection.connect')}</button>
      {snapshot.target && <button type="button" disabled={locked} onClick={() => void submit(true)}>{t('connection.disconnect')}</button>}</div>
    </form>
  </section>;
}
