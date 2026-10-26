// Screenshots of the Fairlead admin UI from the built web/dist, with the admin API answered by an
// in-page mock that returns the engine's own response shapes (src/Fairlead.Core/Host/ApiRoutes.cs).
// Synthetic data only: no engine, no model server, no key, no network. The page is loaded from
// http://127.0.0.1:8776 so the status bar shows the registry address; Playwright answers every
// request itself, nothing listens on that port.
//
//   cd web && npm ci && npm run build
//   node docs/screenshots/capture-screenshots.mjs                      README pair -> docs/screenshots/
//   node docs/screenshots/capture-screenshots.mjs --walkthrough [dir]  walkthrough 01-07 -> dir
//                                                                      (default docs/assets/walkthrough/)
//
// Needs Playwright with Chromium (require('playwright') must resolve, e.g. through NODE_PATH, or
// `npm install --no-save playwright` in web/). 1440x900 at 100% scale, like every Fleet screenshot.
import { createRequire } from 'node:module';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const dist = join(root, 'web', 'dist');
if (!existsSync(join(dist, 'index.html'))) {
  console.error('web/dist is missing: run `npm ci && npm run build` in web/ first.');
  process.exit(2);
}
const req = createRequire(join(root, 'web', 'package.json'));
let playwright;
try { playwright = req('playwright'); } catch { playwright = createRequire(import.meta.url)('playwright'); }
const { chromium } = playwright;

const args = process.argv.slice(2);
const walkthrough = args.includes('--walkthrough');
const outArg = args.filter((a) => !a.startsWith('--'))[0];
const out = resolve(outArg ?? (walkthrough ? join(root, 'docs', 'assets', 'walkthrough') : join(root, 'docs', 'screenshots')));
const BASE = 'http://127.0.0.1:8776';
const TOKEN = 'demo-session';

// ---------- synthetic engine state ----------

const now = Date.now();
const iso = (msAgo) => new Date(now - msAgo).toISOString();
const model = (id, contextWindow, tools = true, extra = {}) => ({ id, contextWindow, tools, vision: null, embedding: false, inputPricePerMTok: null, outputPricePerMTok: null, discovered: true, ...extra });
const LLAMA = model('llama3.2:1b', 131072);
const QWEN = model('qwen2.5:14b', 32768);

function provider(id, name, kind, baseUrl, local, models, extra = {}) {
  return { id, name, kind, preset: id, baseUrl, local, enabled: true, apiKeyEnv: null, timeoutSeconds: local ? 600 : 120, headers: {}, streamUsage: true, models, discovered: models, keySource: 'none', keyHint: null, ...extra };
}

const DEFAULT_RULES = [
  { name: 'private-stays-local', route: 'local', enabled: true, when: { sensitive: true } },
  { name: 'long-context', route: 'smart', enabled: true, when: { minTokens: 24000 } },
  { name: 'agentic-tools', route: 'smart', enabled: true, when: { hasTools: true } },
  { name: 'images', route: 'smart', enabled: true, when: { hasImages: true } },
  { name: 'deep-reasoning', route: 'smart', enabled: true, when: { keywords: ['step by step', 'prove', 'architect', 'design a', 'refactor', 'debug', 'root cause', 'analyze', 'analyse', 'trade-off', 'tradeoff'] } },
  { name: 'quick-chat', route: 'fast', enabled: true, when: { maxTokens: 1500 } },
];
const DEFAULT_ALIASES = { '*haiku*': 'fast', '*-mini*': 'fast', '*flash*': 'fast', 'claude-*': 'smart', 'gpt-*': 'smart', 'o[0-9]*': 'smart', 'gemini-*': 'smart' };

function routes(smart, fast, local) {
  return [
    { id: 'auto', description: 'Chooses smart, fast or local per request with the routing rules.', strategy: 'auto', targets: [], hedgeAfterMs: 0, localFallback: true },
    { id: 'smart', description: 'Strongest available model: long context, tools, reasoning.', strategy: 'failover', targets: smart, hedgeAfterMs: 0, localFallback: true },
    { id: 'fast', description: 'Quick, cheap answers for short prompts.', strategy: 'fastest', targets: fast, hedgeAfterMs: 0, localFallback: true },
    { id: 'local', description: 'Models on this machine only. Kedge mode and private requests land here.', strategy: 'failover', targets: local, hedgeAfterMs: 0, localFallback: false },
  ];
}

function config(s) {
  return {
    setupComplete: s.setupComplete, providers: s.providers, routes: s.routes, rules: DEFAULT_RULES, aliases: DEFAULT_ALIASES,
    defaultRoute: 'auto', autoFallbackRoute: 'smart', kedge: 'auto', requireClientKey: true, clients: s.clients,
    network: { probeHosts: ['1.1.1.1:443', '8.8.8.8:443', '9.9.9.9:443'], probeIntervalSeconds: 15, probeTimeoutMs: 2500, offlineAfterFailures: 2, onlineAfterSuccesses: 2, deepCheckMinutes: 10 },
    privacy: { detectSecrets: true, forceLocalWhenSensitive: true }, corsOrigins: [], logContent: false,
  };
}

const pctl = (xs, p) => { if (!xs.length) return null; const s = [...xs].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.max(0, Math.ceil(p * s.length) - 1))]; };

function metrics(s) {
  const r = s.requests;
  const ok = r.filter((x) => x.status < 400);
  const sum = (f) => r.reduce((a, x) => a + (f(x) ?? 0), 0);
  return {
    since: s.startedAt, total: r.length, failed: r.length - ok.length, failovers: r.filter((x) => x.failover).length,
    servedLocally: ok.filter((x) => x.local).length, kedge: r.filter((x) => x.kedge).length, sensitive: r.filter((x) => x.sensitive).length,
    lastHour: {
      requests: r.length, failed: r.length - ok.length, inputTokens: sum((x) => x.inputTokens), outputTokens: sum((x) => x.outputTokens), cost: null,
      p50Ms: pctl(r.map((x) => x.totalMs), 0.5), p95Ms: pctl(r.map((x) => x.totalMs), 0.95), p50FirstByteMs: pctl(r.filter((x) => x.firstByteMs != null).map((x) => x.firstByteMs), 0.5),
    },
    targets: [],
  };
}

function status(s) {
  return {
    version: '0.1.0', port: 8776, registryPort: 8776, preferredPort: 8776, gatewayUrl: `${BASE}/v1`, lan: false, startedAt: s.startedAt,
    autoStop: true, setupComplete: s.setupComplete, requireClientKey: true, clients: s.clients.length, configFile: null, logFile: null,
    connectivity: { internet: 'up', kedge: false, kedgeReason: null, mode: 'auto', since: s.startedAt, lastProbe: iso(4000), reached: ['1.1.1.1:443', '8.8.8.8:443', '9.9.9.9:443'], osReportsNetwork: true },
    providers: s.providers.map((p) => ({ id: p.id, name: p.name, local: p.local, enabled: p.enabled, reachable: true, authOk: p.local ? null : true, probeMs: s.probeMs[p.id] ?? null, lastError: null, models: p.discovered.length, breaker: 'closed', keySource: p.keySource })),
    routes: s.routes.map((r) => ({ id: r.id, strategy: r.strategy, targets: r.targets.length })),
    launcher: {}, metrics: metrics(s),
  };
}

function record(id, msAgo, fields) {
  return {
    id, time: iso(msAgo), client: null, dialect: 'openai', endpoint: '/v1/chat/completions', requestedModel: 'auto', route: 'fast', decision: [], attempts: [],
    target: null, local: true, kedge: false, sensitive: false, failover: false, stream: false, status: 200, error: null, firstByteMs: null, totalMs: 0,
    estimatedTokens: 0, inputTokens: null, outputTokens: null, cost: null, promptPreview: null, responsePreview: null, ...fields,
  };
}

const DETECT_IDLE = [['lmstudio', 'LM Studio', 'http://127.0.0.1:1234/v1'], ['llamacpp', 'llama.cpp server', 'http://127.0.0.1:8080/v1'], ['vllm', 'vLLM', 'http://127.0.0.1:8000/v1']]
  .map(([preset, name, baseUrl]) => ({ preset, name, baseUrl, running: false, configured: null, models: [], error: `Cannot reach ${baseUrl}: connection refused.` }));

// The walkthrough machine: Ollama with one small model, Jan running with none loaded, no cloud keys.
function walkthroughState({ setupComplete }) {
  const ollama = provider('ollama', 'Ollama', 'ollama', 'http://127.0.0.1:11434', true, setupComplete ? [LLAMA] : []);
  ollama.discovered = [LLAMA];
  const ref = 'ollama/llama3.2:1b';
  return {
    startedAt: iso(20 * 60 * 1000), setupComplete, providers: [ollama], probeMs: { ollama: 2 },
    routes: setupComplete ? routes([ref], [ref], [ref]) : routes([], [], []),
    clients: [], requests: [],
    detect: [{ preset: 'ollama', name: 'Ollama', baseUrl: 'http://127.0.0.1:11434', running: true, configured: 'ollama', models: [LLAMA], error: null }, ...DETECT_IDLE,
      { preset: 'jan', name: 'Jan', baseUrl: 'http://127.0.0.1:1337/v1', running: true, configured: null, models: [], error: null }],
  };
}

const QUICK = ["Model 'auto' is a Fairlead route.", "Auto: rule 'quick-chat' matched (~14 <= 1500 tokens) -> 'fast'.", 'First choice ollama/llama3.2:1b.'];

// The README machine: two local models and one cloud provider whose key comes from an environment variable.
function readmeState() {
  const ollama = provider('ollama', 'Ollama', 'ollama', 'http://127.0.0.1:11434', true, [LLAMA, QWEN]);
  const anthropic = provider('anthropic', 'Anthropic', 'anthropic', 'https://api.anthropic.com', false, [model('claude-sonnet-4-5', 200000)], { apiKeyEnv: 'ANTHROPIC_API_KEY', keySource: 'env' });
  const smart = 'anthropic/claude-sonnet-4-5', fast = 'ollama/llama3.2:1b', big = 'ollama/qwen2.5:14b';
  const att = (target, ms) => [{ target, outcome: 'ok', status: 200, ms, error: null }];
  const requests = [
    record('a41c09e2d7b3', 40 * 1000, { client: 'claude-code', dialect: 'anthropic', endpoint: '/v1/messages', requestedModel: 'claude-sonnet-4-5', route: 'smart', target: smart, local: false, stream: true, firstByteMs: 910, totalMs: 6420, estimatedTokens: 5210, inputTokens: 5388, outputTokens: 412, attempts: att(smart, 6420) }),
    record('9be20f4417ac', 3 * 60 * 1000, { client: 'open-webui', requestedModel: 'auto', route: 'fast', target: fast, stream: true, firstByteMs: 180, totalMs: 1240, estimatedTokens: 38, inputTokens: 41, outputTokens: 96, attempts: att(fast, 1240) }),
    record('5d7f61c0b28e', 7 * 60 * 1000, { client: 'claude-code', dialect: 'anthropic', endpoint: '/v1/messages', requestedModel: 'claude-haiku-4-5', route: 'fast', target: fast, totalMs: 860, estimatedTokens: 120, inputTokens: 131, outputTokens: 58, attempts: att(fast, 860) }),
    record('c3a8e95f0d14', 12 * 60 * 1000, { client: 'open-webui', requestedModel: 'auto', route: 'local', target: big, sensitive: true, stream: true, firstByteMs: 420, totalMs: 3980, estimatedTokens: 260, inputTokens: 274, outputTokens: 188, attempts: att(big, 3980) }),
  ];
  return {
    startedAt: iso(45 * 60 * 1000), setupComplete: true, providers: [ollama, anthropic], probeMs: { ollama: 3, anthropic: 41 },
    routes: routes([smart, big], [fast], [big, fast]), requests,
    clients: [{ id: 'c1', name: 'claude-code', prefix: 'fl-xxxx', created: iso(40 * 60 * 1000), routes: [] }, { id: 'c2', name: 'open-webui', prefix: 'fl-xxxx', created: iso(38 * 60 * 1000), routes: [] }],
    detect: [],
  };
}

// ---------- the mock host ----------

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.ico': 'image/x-icon', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };

async function attach(page, state) {
  await page.route(`${BASE}/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const json = (body, code = 200) => route.fulfill({ status: code, contentType: 'application/json', body: JSON.stringify(body) });
    const p = url.pathname;
    if (!p.startsWith('/api/')) {
      const file = resolve(dist, '.' + (p === '/' ? '/index.html' : decodeURIComponent(p)));
      if (!file.startsWith(dist + sep) || !existsSync(file)) return route.fulfill({ status: 404, body: '' });
      return route.fulfill({ status: 200, contentType: TYPES[extname(file)] ?? 'application/octet-stream', body: readFileSync(file) });
    }
    const body = request.postDataJSON?.() ?? null;
    if (p === '/api/status') return json(status(state));
    if (p === '/api/config') return json(config(state));
    if (p === '/api/detect') return json(state.detect);
    if (p === '/api/presets') return json([]);
    if (p === '/api/requests') return json(state.requests.slice(0, Number(url.searchParams.get('limit') ?? 200)));
    if (p === '/api/heartbeat') return json({ ok: true });
    if (p === '/api/explain') {
      const r = state.explain ?? { route: 'fast', decision: QUICK, candidates: [{ target: 'ollama/llama3.2:1b', local: true, parked: false, parkedBecause: null, dialect: 'openai' }] };
      return json({ route: r.route, decision: r.decision, error: null, kedge: false, localOnly: false, profile: { estimatedTokens: 14, hasTools: !!body?.withTools, hasImages: false, sensitive: false, sensitiveReasons: [], stream: false, requestedModel: body?.model ?? 'auto' }, candidates: r.candidates });
    }
    return json({ error: `The screenshot mock does not answer ${request.method()} ${p}.`, code: 'NOT_MOCKED' }, 404);
  });
}

async function shoot(browser, { file, scheme = 'dark', state, hash, act }) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, colorScheme: scheme, timezoneId: 'America/New_York', locale: 'en-US' });
  const page = await context.newPage();
  await attach(page, state);
  await page.goto(`${BASE}/?session=${TOKEN}${hash ? '#/' + hash : ''}`);
  await page.getByText('Fairlead 0.1.0').waitFor();
  if (act) await act(page);
  await page.waitForTimeout(600);
  await page.screenshot({ path: join(out, file) });
  console.log('wrote ' + join(out, file));
  await context.close();
}

mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
try {
  if (!walkthrough) {
    for (const [scheme, theme] of [['dark', 'night-watch'], ['light', 'chart-room']]) {
      await shoot(browser, { file: `overview-${theme}.png`, scheme, state: readmeState(), hash: 'overview', act: (pg) => pg.getByText('ollama/qwen2.5:14b').first().waitFor() });
    }
  } else {
    const heading = (name) => (pg) => pg.getByRole('heading', { name }).waitFor();
    await shoot(browser, { file: '01-first-launch.png', state: walkthroughState({ setupComplete: false }), act: async (pg) => { await pg.getByText('llama3.2:1b').first().waitFor(); } });
    await shoot(browser, { file: '02-setup-routes.png', state: walkthroughState({ setupComplete: false }), act: async (pg) => {
      await pg.getByRole('button', { name: /Next: Cloud providers/ }).click();
      await pg.getByRole('button', { name: /Next: Routes/ }).click();
      await pg.getByRole('button', { name: 'Suggest targets' }).click();
    } });
    await shoot(browser, { file: '03-overview.png', state: walkthroughState({ setupComplete: true }), hash: 'overview', act: async (pg) => { await pg.getByText('No traffic yet', { exact: true }).waitFor(); } });
    await shoot(browser, { file: '04-routes.png', state: walkthroughState({ setupComplete: true }), hash: 'routes', act: async (pg) => { await heading('Routes')(pg); await pg.getByRole('button', { name: /smart/ }).first().click(); } });
    const withClient = walkthroughState({ setupComplete: true });
    withClient.clients = [{ id: 'c1', name: 'demo-walkthrough-client', prefix: 'fl-xxxx', created: iso(20 * 1000), routes: [] }];
    await shoot(browser, { file: '05-clients.png', state: withClient, hash: 'clients', act: heading('Clients') });
    await shoot(browser, { file: '06-playground-explain.png', state: walkthroughState({ setupComplete: true }), hash: 'playground', act: async (pg) => {
      await pg.getByLabel('Prompt').fill('Summarize the benefits of a checklist in two sentences.');
      await pg.getByRole('button', { name: 'Explain' }).click();
      await pg.getByText('Plan: route').waitFor();
    } });
    const withTraffic = walkthroughState({ setupComplete: true });
    withTraffic.requests = [record('040db259a7aa', 60 * 1000, { decision: QUICK, target: 'ollama/llama3.2:1b', totalMs: 3900, firstByteMs: 3880, estimatedTokens: 14, inputTokens: 47, outputTokens: 66, attempts: [{ target: 'ollama/llama3.2:1b', outcome: 'ok', status: 200, ms: 3880, error: null }] })];
    await shoot(browser, { file: '07-activity-trace.png', state: withTraffic, hash: 'activity', act: async (pg) => { await pg.getByRole('cell', { name: 'ollama/llama3.2:1b' }).click(); await pg.getByText('Decision').waitFor(); } });
  }
} finally {
  await browser.close();
}
