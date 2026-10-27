import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseSse } from '../src/lib/live';
import { NO_KEY, buildGraph, describe, layout, prune, reduce, seenFrom, viaOf, type Flight } from '../src/lib/routing-map';
import type { Config, Status } from '../src/lib/schemas';

function config(targets: { smart?: string[]; fast?: string[]; local?: string[] } = {}, extra: Partial<Config> = {}): Config {
  const route = (id: string, strategy: string, t: string[], localFallback = true) => ({ id, description: '', strategy, targets: t, hedgeAfterMs: 0, localFallback });
  return {
    setupComplete: true,
    providers: [
      { id: 'ollama', name: 'Ollama', kind: 'ollama', preset: 'ollama', baseUrl: 'http://127.0.0.1:11434', local: true, enabled: true, apiKeyEnv: null, timeoutSeconds: 600, headers: {}, streamUsage: true, models: [], discovered: [], keySource: 'none', keyHint: null, managed: false, path: null, retries: null },
      { id: 'anthropic', name: 'Anthropic', kind: 'anthropic', preset: 'anthropic', baseUrl: 'https://api.anthropic.com', local: false, enabled: true, apiKeyEnv: 'K', timeoutSeconds: 120, headers: {}, streamUsage: true, models: [], discovered: [], keySource: 'env', keyHint: null, managed: false, path: null, retries: null },
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

test('one lead per configured route and target, not a fixed two', () => {
  const g = buildGraph(config({ smart: ['anthropic/sonnet', 'ollama/qwen'], fast: ['ollama/tiny'], local: ['ollama/qwen'] }), status());
  assert.deepEqual(g.routes.map((r) => r.id), ['auto', 'smart', 'fast', 'local']);
  assert.deepEqual(g.targets.map((t) => t.id), ['anthropic/sonnet', 'ollama/qwen', 'ollama/tiny']);
  assert.deepEqual(g.clients.map((c) => c.id), ['claude-code']);
  assert.deepEqual(g.autoEdges, [{ from: 'auto', to: 'fast' }, { from: 'auto', to: 'smart' }]);
  assert.equal(g.problems.length, 0);
  const l = layout(g);
  assert.equal(l.route.size, 4);
  assert.ok(l.height >= 4 * 40);
});

test('empty routes are flagged, and say where their requests really go', () => {
  const g = buildGraph(config(), status());
  assert.deepEqual(g.routes.filter((r) => r.empty).map((r) => r.id), ['smart', 'fast', 'local']);
  assert.ok(!g.routes.find((r) => r.id === 'auto')!.empty, 'auto has rules, not targets');
  const smart = g.problems.find((p) => p.route === 'smart')!;
  assert.equal(smart.tone, 'err');
  assert.match(smart.text, /no targets/);
  assert.match(smart.text, /whatever model Fairlead finds on a local server/);
  const withLocal = buildGraph(config({ local: ['ollama/qwen'] }), status());
  assert.match(withLocal.problems.find((p) => p.route === 'fast')!.text, /fall back to 'local' \(ollama\/qwen\)/);
});

test('Kedge mode parks cloud targets only', () => {
  const g = buildGraph(config({ smart: ['anthropic/sonnet', 'ollama/qwen'] }), status(true));
  assert.equal(g.kedge, true);
  assert.equal(g.targets.find((t) => t.id === 'anthropic/sonnet')!.state, 'kedge');
  assert.equal(g.targets.find((t) => t.id === 'ollama/qwen')!.state, 'ready');
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
  assert.equal(describe(f), 'claude-code → auto → smart → fallback local → anthropic/sonnet failed (server) → ollama/qwen · 900 ms');
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

test('targets traffic revealed (a model found on a local server) get a lead too', () => {
  const seen = { clients: [], routes: [], targets: [{ id: 'ollama/llama3.2:1b', local: true }] };
  const t = buildGraph(config(), status(), seen).targets.find((x) => x.id === 'ollama/llama3.2:1b')!;
  assert.equal(t.dynamic, true);
  assert.equal(t.local, true);
});

test('SSE parsing handles split chunks, comments and CRLF', () => {
  const got: Array<[string, string]> = [];
  let rest = parseSse(': keepalive\n\nevent: start\ndata: {"id":', (t, d) => got.push([t, d]));
  rest = parseSse(rest + '"a"}\r\n\r\nevent: done\ndata: {}\n\n', (t, d) => got.push([t, d]));
  assert.equal(rest, '');
  assert.deepEqual(got, [['start', '{"id":"a"}'], ['done', '{}']]);
});
