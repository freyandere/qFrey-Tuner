import { createRequire } from 'node:module';
import { mkdir, readFile, writeFile, rename } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const require = createRequire(import.meta.url);
const { build } = createRequire(require.resolve('../frontend/node_modules/vite/package.json'))('esbuild');
const bundled = await build({ stdin: { resolveDir: root, loader: 'ts', contents: `
  export { en, ru } from './frontend/src/i18n/messages';
  export { localizeCalculatorParameters } from './frontend/src/i18n/calculatorMessages';
` }, bundle: true, platform: 'node', format: 'esm', write: false });
const { en, ru, localizeCalculatorParameters } = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`);
const keys = Object.keys(en).sort();
if (keys.join('\n') !== Object.keys(ru).sort().join('\n')) throw new Error('Translation key mismatch');
for (const key of keys) {
  const placeholders = text => [...text.matchAll(/\{([a-zA-Z0-9_]+)\}/g)].map(match => match[1]).sort().join(',');
  if (placeholders(en[key]) !== placeholders(ru[key])) throw new Error(`Translation parameters mismatch: ${key}`);
}
const messages = { 'en-US': en, 'ru-RU': ru };
const parameters = {};
for (const locale of Object.keys(messages)) {
  parameters[locale] = {};
  for (const key of ['role', 'profile']) {
    parameters[locale][key] = Object.fromEntries(['leecher','seeder','uploader','trueNas','nas','docker','seedbox','hdd','ssdSata','nvme']
      .map(value => [value, localizeCalculatorParameters(locale, { [key]: value })[key]]));
  }
}
const bytes = Buffer.from(JSON.stringify({ messages, parameters }));
if (bytes.length > 1024 * 1024) throw new Error('Translation resource exceeds 1 MiB');
const destination = path.join(root, '.cache/generated/locales.json');
await mkdir(path.dirname(destination), { recursive: true });
let previous; try { previous = await readFile(destination); } catch (error) { if (error.code !== 'ENOENT') throw error; }
if (!previous?.equals(bytes)) {
  const temporary = `${destination}.${process.pid}.tmp`;
  await writeFile(temporary, bytes); await rename(temporary, destination);
}
