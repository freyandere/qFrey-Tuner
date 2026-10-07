import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { expect, test } from 'vitest';
import initializeReply from '../../tests-contract/fixtures/protocol/initialize-success.json';
import type { AppSnapshot } from '../src/contracts/protocol';
import { ConnectionForm } from '../src/components/ConnectionForm';

test('an active measurement freezes target editing and reconnect controls', () => {
  const snapshot = { ...initializeReply.data, activeOperation: {
    id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', kind: 'measurement', stage: 'sampling', progress: null, cancellable: true,
  } } as AppSnapshot;
  const noop = () => {};
  const html = renderToStaticMarkup(createElement(ConnectionForm, {
    snapshot, onSnapshot: noop, onError: noop, onEndpointEdited: noop, onConnected: noop,
  }));
  const controls = html.match(/<(?:input|select|button)\b[^>]*>/g) ?? [];
  expect(controls.length).toBeGreaterThan(3);
  expect(controls.every(control => control.includes('disabled=""'))).toBe(true);
});
