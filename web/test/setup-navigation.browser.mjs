// npm run test:setup (requires installed Edge; no live host, providers or credentials).
// Compile the real setup component with an in-memory config API and exercise it in a browser.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { chromium } from 'playwright';

const web = fileURLToPath(new URL('../', import.meta.url));
const fixture = {
  providers: [{ id: 'mock', enabled: true, local: true, models: [{ id: 'a' }, { id: 'b' }], discovered: [] }],
  routes: [{ id: 'smart', strategy: 'failover', description: 'Synthetic route', targets: ['mock/a', 'mock/b'] }],
  clients: [],
};
const configMock = `
export const useConfig = () => ({ data: window.fixture, error: null });
export async function saveRoute(route) {
  await new Promise(r => setTimeout(r, 150));
  if (window.failSave) throw new Error('Synthetic route save failure');
  window.saved.push(route);
  window.fixture.routes = window.fixture.routes.map(r => r.id === route.id ? route : r);
}
export async function saveSettings(settings) { window.settings.push(settings); }
export const createClient = async () => ({}), discoverProvider = async () => ({}), saveProvider = async () => ({}), testProvider = async () => ({});`;
const result = await build({
  absWorkingDir: web,
  stdin: {
    contents: `import React from 'react'; import {createRoot} from 'react-dom/client'; import {SetupPage} from './src/pages/setup'; import {ToastProvider} from './src/components/ui'; createRoot(document.getElementById('root')).render(<ToastProvider><SetupPage status={undefined} refreshStatus={async()=>{}} /></ToastProvider>);`,
    resolveDir: web, loader: 'tsx',
  },
  bundle: true, write: false, format: 'iife', jsx: 'automatic',
  plugins: [{ name: 'offline-fixture', setup(b) {
    b.onResolve({ filter: /^\.\.\/app$/ }, () => ({ path: 'app', namespace: 'fixture' }));
    b.onResolve({ filter: /^\.\.\/lib\/config$/ }, () => ({ path: 'config', namespace: 'fixture' }));
    b.onLoad({ filter: /.*/, namespace: 'fixture' }, a => ({
      contents: a.path === 'config' ? configMock : `export const navigate = route => { window.finished = route; };`, loader: 'js',
    }));
  }}],
});
const server = createServer((req, res) => {
  if (req.url === '/bundle.js') { res.setHeader('content-type', 'text/javascript'); return res.end(result.outputFiles[0].text); }
  if (req.url.startsWith('/api/')) { res.setHeader('content-type', 'application/json'); return res.end('[]'); }
  res.setHeader('content-type', 'text/html');
  res.end(`<html><body><div id="root"></div><script>window.fixture=${JSON.stringify(fixture)};window.saved=[];window.settings=[];</script><script src="/bundle.js"></script></body></html>`);
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
let browser;
try {
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  const routes = page.getByRole('list', { name: 'Setup steps' }).getByRole('button').nth(2);
  await routes.click();
  const order = () => page.locator('[title^="mock/"]').evaluateAll(nodes => nodes.map(n => n.title));
  assert.deepEqual(await order(), ['mock/a', 'mock/b']);
  await page.getByRole('button', { name: 'Move down', exact: true }).first().click();
  assert.deepEqual(await order(), ['mock/b', 'mock/a']);
  await page.getByRole('button', { name: 'Next: Connect tools' }).click();
  assert.equal(await page.getByRole('button', { name: 'Finish setup' }).isDisabled(), true);
  await page.getByRole('button', { name: 'Back', exact: true }).click();
  assert.deepEqual(await order(), ['mock/b', 'mock/a']);
  await page.getByRole('button', { name: /Cloud providers.*skipped/ }).click();
  await routes.click();
  assert.deepEqual(await order(), ['mock/b', 'mock/a']);
  await page.evaluate(() => { window.failSave = true; });
  await page.getByRole('button', { name: 'Save routes', exact: true }).click();
  await page.getByText('Synthetic route save failure', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Next: Connect tools' }).click();
  assert.equal(await page.getByRole('button', { name: 'Finish setup' }).isDisabled(), true);
  await page.getByRole('button', { name: 'Review route changes', exact: true }).click();
  assert.deepEqual(await order(), ['mock/b', 'mock/a']);
  await page.evaluate(() => { window.failSave = false; });
  await page.getByRole('button', { name: 'Save routes', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: 'Next: Connect tools' }).isDisabled(), true);
  await page.getByText('Routes saved.', { exact: true }).waitFor();
  assert.deepEqual(await order(), ['mock/b', 'mock/a']);
  assert.equal(await page.getByRole('button', { name: 'Save routes', exact: true }).isDisabled(), true);
  await page.getByRole('button', { name: 'Next: Connect tools' }).click();
  assert.equal(await page.getByRole('button', { name: 'Finish setup' }).isDisabled(), false);
  await page.getByRole('button', { name: 'Finish setup' }).click();
  await page.waitForFunction(() => window.finished === 'overview');
  assert.deepEqual(await page.evaluate(() => window.settings), [{ setupComplete: true }]);
  assert.deepEqual(await page.evaluate(() => window.saved[0].targets), ['mock/b', 'mock/a']);
  assert.deepEqual(errors, []);
  console.log('PASS: setup draft navigation, failed-save retention, retry, busy guard and finish');
} finally {
  if (browser) await browser.close();
  await new Promise(r => server.close(r));
}
