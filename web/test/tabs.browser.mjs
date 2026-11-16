// Exercises the real Tabs and ConfirmDialog in a browser: arrow keys, Tab leaving the set,
// and the dialog name. No live Fairlead host. Run: node test/tabs.browser.mjs
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { chromium } from 'playwright';

const web = fileURLToPath(new URL('../', import.meta.url));
const result = await build({
  absWorkingDir: web,
  stdin: {
    contents: `
      import React, { useState } from 'react';
      import { createRoot } from 'react-dom/client';
      import { ConfirmDialog, Tabs } from './src/components/ui';
      function Harness() {
        const [tab, setTab] = useState('one');
        const [open, setOpen] = useState(false);
        return (
          <div>
            <Tabs id="demo" label="Demo sections" value={tab} onChange={setTab} items={[{ value: 'one', label: 'One' }, { value: 'two', label: 'Two' }, { value: 'three', label: 'Three' }]}>
              <p>Panel {tab}</p>
              <button type="button" id="inside">Inside panel</button>
            </Tabs>
            <button type="button" id="after">After tabs</button>
            <button type="button" id="ask" onClick={() => setOpen(true)}>Ask</button>
            <ConfirmDialog open={open} title="Revoke the key?" confirmLabel="Revoke key" onConfirm={() => setOpen(false)} onCancel={() => setOpen(false)}>
              Tools using this key get 401.
            </ConfirmDialog>
          </div>
        );
      }
      createRoot(document.getElementById('root')).render(<Harness />);
    `,
    resolveDir: web,
    loader: 'tsx',
  },
  bundle: true,
  write: false,
  format: 'iife',
  jsx: 'automatic',
});
const server = createServer((req, res) => {
  if (req.url === '/bundle.js') {
    res.setHeader('content-type', 'text/javascript');
    return res.end(result.outputFiles[0].text);
  }
  res.setHeader('content-type', 'text/html');
  res.end('<html><body><div id="root"></div><script src="/bundle.js"></script></body></html>');
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
let browser;
try {
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true });
  } catch {
    browser = await chromium.launch({ headless: true });
  }
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`http://127.0.0.1:${port}`);
  const tab = (name) => page.getByRole('tab', { name });
  await tab('One').focus();
  assert.equal(await tab('One').getAttribute('aria-selected'), 'true');
  assert.equal(await tab('One').getAttribute('tabindex'), '0');
  assert.equal(await tab('Two').getAttribute('tabindex'), '-1');
  const panelId = await tab('One').getAttribute('aria-controls');
  assert.equal(await page.locator(`[id="${panelId}"]`).getAttribute('role'), 'tabpanel');
  assert.equal(await page.locator(`[id="${panelId}"]`).getAttribute('aria-labelledby'), await tab('One').getAttribute('id'));

  await page.keyboard.press('ArrowRight');
  assert.equal(await tab('Two').getAttribute('aria-selected'), 'true');
  assert.equal(await page.evaluate(() => document.activeElement?.textContent), 'Two');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  assert.equal(await tab('One').getAttribute('aria-selected'), 'true');
  await page.keyboard.press('End');
  assert.equal(await tab('Three').getAttribute('aria-selected'), 'true');
  await page.keyboard.press('Home');
  assert.equal(await tab('One').getAttribute('aria-selected'), 'true');

  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => document.activeElement?.id), 'inside');
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => document.activeElement?.id), 'after');

  await page.locator('#ask').click();
  const dialog = page.getByRole('dialog');
  await dialog.waitFor();
  const labelledBy = await dialog.getAttribute('aria-labelledby');
  assert.equal(await page.locator(`[id="${labelledBy}"]`).textContent(), 'Revoke the key?');
  const focused = await page.evaluate(() => {
    const active = document.activeElement;
    const dialog = document.querySelector('dialog');
    return { open: dialog?.open === true, inside: dialog?.contains(active) === true };
  });
  assert.equal(focused.open, true);
  assert.equal(focused.inside, true);
  await page.keyboard.press('Escape');
  await dialog.waitFor({ state: 'hidden' });
  assert.deepEqual(errors, []);
} finally {
  if (browser) await browser.close();
  server.close();
}
