import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';

const frontendDir = fileURLToPath(new URL('.', import.meta.url));

export default defineConfig({
  testDir: './tests-e2e',
  testMatch: '**/*.spec.ts',
  outputDir: '../.cache/e2e/test-results',
  reporter: 'list',
  workers: 1,
  use: {
    baseURL: 'http://127.0.0.1:4173',
    browserName: 'chromium',
    channel: 'msedge',
    viewport: { width: 1200, height: 840 },
  },
  webServer: {
    command: 'node ./node_modules/vite/bin/vite.js preview --outDir ../.cache/frontend --host 127.0.0.1 --port 4173 --strictPort',
    cwd: frontendDir,
    url: 'http://127.0.0.1:4173',
    reuseExistingServer: false,
    timeout: 30_000,
  },
});
