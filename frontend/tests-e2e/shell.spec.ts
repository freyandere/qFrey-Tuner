import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

const initialize = JSON.parse(readFileSync(new URL('../../tests-contract/fixtures/protocol/initialize-success.json', import.meta.url), 'utf8'));
const screens = {
  'en-US': ['Overview', 'Setup', 'Recommendations', 'Experiment', 'Results', 'History'],
  'ru-RU': ['Обзор', 'Условия', 'Рекомендации', 'Эксперимент', 'Результаты', 'История'],
} as const;

async function useMockBridge(page: Page, deferHistory = false) {
  await page.addInitScript(({ fixture, deferHistory }) => {
    type TestRequest = { command: string; requestId: string; payload?: { locale?: string; theme?: string } };
    type MessageHandler = (event: MessageEvent<unknown>) => void;
    const handlers: MessageHandler[] = [];
    let revision = fixture.revision;
    let data = structuredClone(fixture.data);
    const historyReplies: ((endpoint: string) => void)[] = [];
    Object.assign(window, { releaseHistory: (index: number, endpoint: string) => historyReplies[index]!(endpoint) });

    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: {
        addEventListener: (_type: 'message', handler: MessageHandler) => handlers.push(handler),
        postMessage: (message: TestRequest) => {
          if (message.command === 'ListHistory' && deferHistory) {
            historyReplies.push(endpoint => handlers.forEach(handler => handler({ data: {
              ...fixture, requestId: message.requestId, data: { items: [{
                cycleId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', createdUtc: '2026-01-01T00:30:00Z',
                endpoint, qbittorrentVersion: '5.1.0', phase: 'completed', applyStatus: 'verified', changeCount: 1,
              }], nextCursor: null }, error: null,
            } } as MessageEvent<unknown>)));
            return;
          }
          if (message.command === 'SetUiPreferences' && message.payload) {
            data.preferences = { ...data.preferences, ...message.payload };
            data.revision = ++revision;
          }
          const reply = { ...fixture, requestId: message.requestId, revision, data: { ...data, revision }, error: null };
          queueMicrotask(() => handlers.forEach(handler => handler({ data: reply } as MessageEvent<unknown>)));
        },
      } },
    });

    const marker = document.createElement('div');
    marker.dataset.testid = 'mock-bridge-marker';
    marker.textContent = 'PLAYWRIGHT MOCK BRIDGE · NO qBittorrent';
    Object.assign(marker.style, {
      position: 'fixed', bottom: '8px', right: '8px', zIndex: '9999',
      padding: '6px 8px', background: '#7c2d12', color: 'white',
      font: '12px/1.2 sans-serif', borderRadius: '4px',
    });
    const addMarker = () => document.body.append(marker);
    if (document.body) addMarker();
    else document.addEventListener('DOMContentLoaded', addMarker, { once: true });
  }, { fixture: initialize, deferHistory });
}

async function expectNoHorizontalOverflow(page: Page) {
  const dimensions = await page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    document: document.documentElement.scrollWidth,
  }));
  expect(dimensions.document, `horizontal overflow at ${dimensions.viewport}px`).toBeLessThanOrEqual(dimensions.viewport);
}

test('shell screenshots: all screens, locales and explicit themes', async ({ page }, testInfo) => {
  await useMockBridge(page);
  await page.goto('/');
  await expect(page.getByTestId('mock-bridge-marker')).toBeVisible();
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

  for (const locale of ['en-US', 'ru-RU'] as const) {
    await page.getByRole('combobox').nth(0).selectOption(locale);
    const themeLabel = locale === 'en-US' ? 'Theme' : 'Тема';

    for (const theme of ['dark', 'light'] as const) {
      await page.getByRole('combobox').nth(1).selectOption(theme);
      for (const [index, screen] of screens[locale].entries()) {
        const navigation = page.getByRole('navigation', { name: 'qFrey-Tuner' });
        await navigation.getByRole('button', { name: screen, exact: true }).click();
        await expect(navigation.getByRole('button', { name: screen, exact: true })).toHaveAttribute('aria-current', 'page');
        await expect(page.getByTestId('mock-bridge-marker')).toBeVisible();

        await page.setViewportSize({ width: 1200, height: 840 });
        await expectNoHorizontalOverflow(page);
        await page.screenshot({
          path: testInfo.outputPath(`shell-${locale}-${theme}-${index + 1}.png`),
          fullPage: true,
        });

        await page.setViewportSize({ width: 960, height: 680 });
        await expectNoHorizontalOverflow(page);
      }
    }
  }
});

test('system theme follows OS preference and navigation is keyboard accessible', async ({ page }) => {
  await useMockBridge(page);
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  const canvas = () => page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--canvas').trim());
  await expect.poll(canvas).toBe('#10151d');
  await page.emulateMedia({ colorScheme: 'light' });
  await expect.poll(canvas).toBe('#f4f6fa');

  const navigation = page.getByRole('navigation', { name: 'qFrey-Tuner' });
  await navigation.getByRole('button', { name: 'Overview' }).focus();
  await page.keyboard.press('Tab');
  await expect(navigation.getByRole('button', { name: 'Setup' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Setup');
  await expect(page.getByRole('heading', { level: 1 })).toBeFocused();
});

test('setup preserves incomplete numeric edits across navigation and empty screens offer next steps', async ({ page }) => {
  await useMockBridge(page);
  await page.goto('/');
  const navigation = page.getByRole('navigation', { name: 'qFrey-Tuner' });
  await navigation.getByRole('button', { name: 'Setup', exact: true }).click();
  await page.locator('#setup-download').fill('-');
  await navigation.getByRole('button', { name: 'Recommendations', exact: true }).click();
  await expect(page.locator('#setup-download')).toBeHidden();
  await page.getByRole('button', { name: 'Go to setup and build a plan', exact: true }).click();
  await expect(page.locator('#setup-download')).toHaveValue('-');
  await navigation.getByRole('button', { name: 'Results', exact: true }).click();
  await page.getByRole('button', { name: 'Go to the experiment', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Experiment');
});

test('history ignores an older page after leaving and reopening the tab', async ({ page }) => {
  await useMockBridge(page, true);
  await page.goto('/');
  const navigation = page.getByRole('navigation', { name: 'qFrey-Tuner' });
  await navigation.getByRole('button', { name: 'History', exact: true }).click();
  await expect(page.getByText('Loading saved cycles…', { exact: true })).toBeVisible();
  await navigation.getByRole('button', { name: 'Overview', exact: true }).click();
  await navigation.getByRole('button', { name: 'History', exact: true }).click();
  await expect(page.getByText('Loading saved cycles…', { exact: true })).toBeVisible();
  await page.evaluate(() => (window as Window & { releaseHistory: (index: number, endpoint: string) => void }).releaseHistory(1, 'http://latest.invalid'));
  await expect(page.getByRole('cell', { name: 'http://latest.invalid', exact: true })).toBeVisible();
  await page.evaluate(() => (window as Window & { releaseHistory: (index: number, endpoint: string) => void }).releaseHistory(0, 'http://old.invalid'));
  await expect(page.getByRole('cell', { name: 'http://latest.invalid', exact: true })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'http://old.invalid', exact: true })).toHaveCount(0);
});
