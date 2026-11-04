import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ago, count, ms, usd } from '../src/lib/format';
import { portOf, snippets } from '../src/lib/snippets';

test('snippets carry the real address and key, and Anthropic clients get the root without /v1', () => {
  const list = snippets('http://127.0.0.1:8776/v1', 'fl-abc');
  const claude = list.find((s) => s.id === 'claude-code')!;
  assert.match(claude.code, /ANTHROPIC_BASE_URL = "http:\/\/127\.0\.0\.1:8776"/);
  assert.match(claude.code, /fl-abc/);
  const py = list.find((s) => s.id === 'openai-python')!;
  assert.match(py.code, /base_url="http:\/\/127\.0\.0\.1:8776\/v1"/);
  assert.ok(list.every((s) => !s.code.includes('undefined')));
});

test('snippets fall back to a placeholder key', () => {
  assert.ok(snippets('http://127.0.0.1:8776/v1', '').every((s) => !s.code.includes('fl-abc')));
  assert.match(snippets('http://127.0.0.1:8776/v1', '')[0]!.code, /fl-YOUR-KEY/);
});

test('portOf reads the port, defaulting to the registry port', () => {
  assert.equal(portOf('http://127.0.0.1:9123/v1'), '9123');
  assert.equal(portOf('http://localhost/v1'), '8776');
});

test('missing values are a dash, never a zero', () => {
  assert.equal(ms(null), '—');
  assert.equal(count(undefined), '—');
  assert.equal(usd(null), '—');
  assert.equal(ago(null), '—');
  assert.equal(ms(250), '250 ms');
  assert.equal(ms(2500), '2.50 s');
  assert.equal(usd(0.004), '$0.0040');
});

test('install-for-an-app names: the Fleet tools and any name the server accepts', async () => {
  const { FLEET_APPS, credentialTarget, isValidAppName } = await import('../src/lib/apps');
  assert.ok(FLEET_APPS.every((a) => isValidAppName(a)));
  assert.deepEqual([...FLEET_APPS], ['PacketRat', 'Bowerbird', 'Gangplank', 'Halyard', 'Semaphore', 'SnapShackle', 'BridgeWarden', 'Bosun', 'TalkForge']);
  assert.equal(isValidAppName('my-tool-2'), true);
  for (const bad of ['', '9app', 'has space', '../x', 'a'.repeat(41), '-lead']) assert.equal(isValidAppName(bad), false, bad);
  assert.equal(isValidAppName('a'.repeat(40)), true);
  assert.equal(credentialTarget('PacketRat'), 'Fairlead/PacketRat');
});
