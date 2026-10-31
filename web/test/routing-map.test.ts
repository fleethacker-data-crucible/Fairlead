import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseSse } from '../src/lib/live';
import { NO_KEY, buildGraph, describe, layout, prune, reduce, routeTone, seenFrom, viaOf, type Flight } from '../src/lib/routing-map';
import type { Config, Status } from '../src/lib/schemas';

function config(targets: { smart?: string[]; fast?: string[]; local?: string[] } = {}, extra: Partial<Config> = {}): Config {
  const route = (id: string, strategy: string, t: string[], localFallback = true) => ({ id, description: '', strategy, targets: t, hedgeAfterMs: 0, localFallback });
  return {
    setupComplete: true,
    providers: [
      { id: 'ollama', name: 'Ollama', kind: 'ollama', preset: 'ollama', baseUrl: 'http://127.0.0.1:11434', local: true, enabled: true, apiKeyEnv: null, timeoutSeconds: 600, headers: {}, streamUsage: true, models: [], discovered: [], keySource: 'none', keyHint: null, managed: false, path: null, retries: null },
      { id: 'anthropic', name: 'Anthropic', kind: 'anthropic', preset: 'anthropic', baseUrl: 'https://api.github.com', local: false, enabled: true, apiKeyEnv: 'K', timeoutSeconds: 120, headers: {}, streamUsage: true, models: [], discovered: [], keySource: 'env', keyHint: null, managed: false, path: null, retries: null },
    ],
    routes: [route('auto', 'auto', []), route('smart', 'failover', targets.smart ?? []), route('fast', 'fastest', targets.fast ?? []), route('local', 'failover', targets.local ?? [], false)],
    rules: [{ name: 'quick', route: 'fast', enabled: true, when: { maxTokens: 1500 } }],
    aliases: {}, defaultRoute: 'auto', autoFallbackRoute: 'smart', kedge: 'auto', requireClientKey: true,
    clients: [{ id: 'c1', name: 'claude-code', prefix: 'fl-x', created: '', routes: [] }],
    network: { probeHosts: [], probeIntervalSeconds: 15, probeTimeoutMs: 2500, offlineAfterFailures: 2, onlineAfterSuccesses: 2, deepCheckMinutes: 10 },
    privacy: { detectSecrets: true, forceLocalWhenSensitive: true }, corsOrigins: [], logContent: false, lazaretteHoldHours: 8,
    ...extra,
  } as Config;
}

function status(kedge = false): Status {
  return {
    connectivity: { internet: kedge ? 'down' : 'up', kedge, mode: 'auto', reached: [], osReportsNetwork: true },
    providers: [
      { id: 'ollama', name: 'Ollama', local: true, enabled: true, reachable: true, models: 1, breaker: 'closed', keySource: 'none' },
      { id: 'anthropic', name: 'Anthropic', local: false, enabled: true, reachable: true, authOk: true, models: 1, breaker: 'closed', keySource: 'env' },
    ],
  } as unknown as Status;
}

test('one lead per configured provider, local first, not a fixed two', () => {
  const g = buildGraph(config({ smart: ['anthropic/sonnet', 'ollama/qwen'], fast: ['ollama/tiny'], local: ['ollama/qwen'] }), status());
  assert.deepEqual(g.providers.map((p) => p.id), ['ollama', 'anthropic']);
  assert.deepEqual(g.providers.map((p) => p.health), ['ok', 'ok']);
  assert.deepEqual(g.providers[0]!.routes, ['smart', 'fast', 'local']);
  assert.deepEqual(g.clients.map((c) => c.id), ['claude-code']);
  assert.equal(g.problems.length, 0);
  assert.equal(layout(g).provider.size, 2);
});

test('empty routes are flagged on the fairlead, saying where their requests really go', () => {
  const g = buildGraph(config(), status());
  assert.deepEqual(g.problems.map((p) => p.short), ['smart â†’ no targets, falling back to local', 'fast â†’ no targets, falling back to local', 'local â†’ no targets, using any local model']);
  assert.equal(g.problems[0]!.tone, 'err');
  assert.match(g.problems[0]!.text, /whatever model Fairlead finds on a local server/);
  const withLocal = buildGraph(config({ local: ['ollama/qwen'] }), status());
  assert.match(withLocal.problems.find((p) => p.route === 'fast')!.text, /fall back to 'local' \(ollama\/qwen\)/);
  // The fairlead's warnings get room under it however few rows there are.
  const l = layout(g);
  assert.ok(l.height - l.fitting.y >= 22 + 24 + 3 * 18);
});

test('Kedge mode slackens cloud leads only; broken is not idle', () => {
  const g = buildGraph(config(), status(true));
  assert.equal(g.kedge, true);
  assert.equal(g.providers.find((p) => p.id === 'anthropic')!.health, 'kedge');
  assert.equal(g.providers.find((p) => p.id === 'ollama')!.health, 'ok');
  const s = status();
  s.providers[1]!.breaker = 'open';
  s.providers[0]!.reachable = false;
  const broken = buildGraph(config(), s);
  assert.deepEqual(broken.providers.map((p) => [p.health, p.why]), [['broken', 'not running'], ['broken', 'paused after failures']]);
  const off = config();
  off.providers[1]!.enabled = false;
  assert.equal(buildGraph(off, status()).providers[1]!.health, 'disabled');
});

test('bead colours follow the route', () => {
  assert.equal(routeTone('smart'), 'var(--info)');
  assert.equal(routeTone('fast'), 'var(--brass)');
  assert.equal(routeTone('local'), 'var(--accent)');
  assert.equal(routeTone('my-route'), 'var(--ink)');
});

test('a failover request: attempt fails, re-leads to the next target, then finishes', () => {
  let fs: Flight[] = [];
  fs = reduce(fs, 'start', { id: 'r1', client: 'claude-code', requestedRoute: 'auto', route: 'smart', candidates: [] }, 0);
  fs = reduce(fs, 'attempt', { id: 'r1', target: 'anthropic/sonnet', via: 'smart', phase: 'begin' }, 1);
  assert.equal(fs[0]!.phase, 'trying');
  fs = reduce(fs, 'attempt', { id: 'r1', target: 'anthropic/sonnet', phase: 'end', outcome: 'server', status: 503, error: 'overloaded' }, 2);
  fs = reduce(fs, 'attempt', { id: 'r1', target: 'ollama/qwen', via: 'local', phase: 'begin' }, 3);
  fs = reduce(fs, 'attempt', { id: 'r1', target: 'ollama/qwen', phase: 'end', outcome: 'ok', status: 200 }, 4);
  fs = reduce(fs, 'firstByte', { id: 'r1', target: 'ollama/qwen', ms: 300 }, 5);
  assert.equal(fs[0]!.phase, 'answering');
  fs = reduce(fs, 'done', { id: 'r1', status: 200, target: 'ollama/qwen', failover: true, totalMs: 900, attempts: [{ target: 'anthropic/sonnet', outcome: 'server' }, { target: 'ollama/qwen', outcome: 'ok' }] }, 6);
  const f = fs[0]!;
  assert.equal(f.phase, 'done');
  assert.deepEqual(f.attempts.map((a) => a.state), ['failed', 'ok']);
  assert.equal(f.attempts[1]!.via, 'local', 'the fallback lead survives the done event');
  assert.equal(describe(f), 'claude-code â†’ auto â†’ smart â†’ fallback local â†’ anthropic/sonnet failed (server) â†’ ollama/qwen Â· 900 ms');
  assert.deepEqual(prune(fs, 6 + 7000), []);
});

test('a finished record alone (polling) still draws, inferring the jump to local from config', () => {
  const g = buildGraph(config({ fast: [], local: ['ollama/qwen'] }), status());
  const fs = reduce([], 'done', { id: 'r2', client: null, route: 'fast', status: 200, attempts: [{ target: 'ollama/qwen', outcome: 'ok' }] }, 0);
  const f = fs[0]!;
  assert.equal(f.replayed, true);
  assert.equal(f.client, NO_KEY);
  assert.equal(viaOf(g, f, f.attempts[0]!), 'local');
  const seen = seenFrom(fs, { clients: [], routes: [], targets: [] });
  assert.ok(buildGraph(config(), status(), seen).clients.some((c) => c.id === NO_KEY), 'a keyless caller gets its own lead');
});

test('a provider only traffic named gets a lead, marked broken (not configured)', () => {
  const seen = { clients: [], routes: [], targets: [{ id: 'openrouter/some-model', local: false }] };
  const p = buildGraph(config(), status(), seen).providers.find((x) => x.id === 'openrouter')!;
  assert.equal(p.dynamic, true);
  assert.equal(p.health, 'broken');
  assert.equal(p.why, 'not configured');
});

test('SSE parsing handles split chunks, comments and CRLF', () => {
  const got: Array<[string, string]> = [];
  let rest = parseSse(': keepalive\n\nevent: start\ndata: {"id":', (t, d) => got.push([t, d]));
  rest = parseSse(rest + '"a"}\r\n\r\nevent: done\ndata: {}\n\n', (t, d) => got.push([t, d]));
  assert.equal(rest, '');
  assert.deepEqual(got, [['start', '{"id":"a"}'], ['done', '{}']]);
});
