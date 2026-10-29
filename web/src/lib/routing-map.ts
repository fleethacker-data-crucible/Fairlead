// The live routing map's model: one lead per provider (from config and status), where everything sits, and what
// each request in flight has done so far (from /api/live events). Pure functions only, so they run under node:test.
import type { Config, Status } from './schemas';

export const NO_KEY = '(no key)';
export const MORE = '(more)';

/** How a provider's lead looks at rest. 'broken' is red and frayed; it is never drawn as merely idle. */
export type ProviderHealth = 'ok' | 'broken' | 'kedge' | 'disabled' | 'unknown';

export type ClientNode = { id: string; label: string };
export type ProviderNode = {
  id: string;
  name: string;
  local: boolean;
  health: ProviderHealth;
  /** Why it is broken, parked or disabled, in a word or two. */
  why: string | null;
  /** Routes that list one of its models. */
  routes: string[];
  /** Not configured: traffic named it (a deleted provider, or a target typed by hand). */
  dynamic: boolean;
};
export type RouteInfo = {
  id: string;
  strategy: string;
  targets: string[];
  /** A non-auto route with no targets: every request to it falls back (or fails). */
  empty: boolean;
  auto: boolean;
  localFallback: boolean;
  dynamic: boolean;
};
export type Problem = { tone: 'err' | 'warn'; route?: string; short: string; text: string };

export type Graph = {
  clients: ClientNode[];
  providers: ProviderNode[];
  routes: RouteInfo[];
  problems: Problem[];
  kedge: boolean;
};

export type Seen = { clients: string[]; routes: string[]; targets: Array<{ id: string; local: boolean }> };

const MAX_CLIENTS = 8;
const MAX_PROVIDERS = 10;

export function providerOf(ref: string) {
  const i = ref.indexOf('/');
  return i < 0 ? { provider: ref, model: '' } : { provider: ref.slice(0, i), model: ref.slice(i + 1) };
}

/** Bead colour per route: smart, fast and local are fixed so they read the same everywhere; anything else is ink. */
export function routeTone(route: string | null | undefined): string {
  switch ((route ?? '').toLowerCase()) {
    case 'smart': return 'var(--info)';
    case 'fast': return 'var(--brass)';
    case 'local': return 'var(--accent)';
    default: return 'var(--ink)';
  }
}

/** Builds the leads from the real config: one per client key and one per provider. Nothing is fixed. */
export function buildGraph(config: Config, status: Status | undefined, seen: Seen = { clients: [], routes: [], targets: [] }): Graph {
  const kedge = status?.connectivity.kedge ?? false;
  const health = new Map((status?.providers ?? []).map((p) => [p.id, p]));

  const clientIds = config.clients.map((c) => c.name);
  for (const c of seen.clients) if (!clientIds.includes(c)) clientIds.push(c);
  if ((!config.requireClientKey || seen.clients.includes(NO_KEY)) && !clientIds.includes(NO_KEY)) clientIds.push(NO_KEY);
  const shownClients = clientIds.length > MAX_CLIENTS ? clientIds.slice(0, MAX_CLIENTS - 1) : clientIds;
  const clients: ClientNode[] = shownClients.map((id) => ({ id, label: id === NO_KEY ? 'no client key' : id }));
  if (clientIds.length > shownClients.length) clients.push({ id: MORE, label: `+${clientIds.length - shownClients.length} more` });

  const routes: RouteInfo[] = config.routes.map((r) => ({
    id: r.id, strategy: r.strategy, targets: r.strategy === 'auto' ? [] : r.targets,
    empty: r.strategy !== 'auto' && r.targets.length === 0, auto: r.strategy === 'auto', localFallback: r.localFallback, dynamic: false,
  }));
  for (const id of seen.routes) {
    if (routes.some((r) => r.id.toLowerCase() === id.toLowerCase())) continue;
    routes.push({ id, strategy: 'direct', targets: id.includes('/') ? [id] : [], empty: false, auto: false, localFallback: true, dynamic: true });
  }
  const usedBy = (provider: string) => routes.filter((r) => !r.dynamic && r.targets.some((t) => providerOf(t).provider === provider)).map((r) => r.id);

  // Local first: that lead is the Kedge anchor and is always drawn. Then cloud in config order, then what traffic revealed.
  const configured = [...config.providers].sort((a, b) => Number(b.local) - Number(a.local));
  const providers: ProviderNode[] = configured.map((p) => {
    const h = health.get(p.id);
    let state: ProviderHealth = h?.reachable === true ? 'ok' : 'unknown';
    let why: string | null = null;
    if (!p.enabled) { state = 'disabled'; why = 'disabled'; }
    else if (kedge && !p.local) { state = 'kedge'; why = 'Kedge mode'; }
    else if (h?.breaker === 'open') { state = 'broken'; why = 'paused after failures'; }
    else if (h?.authOk === false) { state = 'broken'; why = 'key rejected'; }
    else if (!p.local && p.keySource === 'none') { state = 'broken'; why = 'no key'; }
    else if (h?.reachable === false) { state = 'broken'; why = p.local ? 'not running' : 'unreachable'; }
    return { id: p.id, name: p.name, local: p.local, health: state, why, routes: usedBy(p.id), dynamic: false };
  });
  for (const t of seen.targets) {
    const { provider } = providerOf(t.id);
    if (!providers.some((p) => p.id === provider)) providers.push({ id: provider, name: provider, local: t.local, health: 'broken', why: 'not configured', routes: usedBy(provider), dynamic: true });
  }
  if (providers.length > MAX_PROVIDERS) {
    const extra = providers.length - (MAX_PROVIDERS - 1);
    providers.splice(MAX_PROVIDERS - 1, providers.length, { id: MORE, name: `+${extra} more`, local: false, health: 'unknown', why: null, routes: [], dynamic: false });
  }
  return { clients, providers, routes, problems: problems(config, routes), kedge };
}

function problems(config: Config, routes: RouteInfo[]): Problem[] {
  const out: Problem[] = [];
  const local = routes.find((r) => r.id === 'local');
  const localEmpty = !local || local.empty;
  for (const r of routes.filter((x) => x.empty && !x.dynamic)) {
    let short: string, text: string;
    if (r.id === 'local') {
      short = 'local → no targets, using any local model';
      text = "Route 'local' has no targets. Fairlead picks whatever model it finds on a running local server, or answers 503 when there is none.";
    } else if (!r.localFallback) {
      short = `${r.id} → no targets, requests fail`;
      text = `Route '${r.id}' has no targets and no local fallback. Requests to it fail with 503.`;
    } else {
      short = `${r.id} → no targets, falling back to local`;
      text = localEmpty
        ? `Route '${r.id}' has no targets. Requests to it fall back to 'local', which is empty too, so they go to whatever model Fairlead finds on a local server.`
        : `Route '${r.id}' has no targets. Requests to it fall back to 'local' (${local!.targets.slice(0, 2).join(', ')}${local!.targets.length > 2 ? '…' : ''}).`;
    }
    out.push({ tone: r.id === config.defaultRoute || r.id === config.autoFallbackRoute || r.id === 'local' || !r.localFallback ? 'err' : 'warn', route: r.id, short, text });
  }
  return out;
}

// ---------- layout ----------

export const W = 1000;
export const ROW = 44;
export const NODE_H = 32;
export const COL = {
  client: { x: 8, w: 176 },
  fitting: { cx: 380, r: 22 },
  provider: { x: 700, w: 292 },
} as const;
const PAD = 24;
export const WARN_LINE = 18;

export type Layout = {
  height: number;
  fitting: { x: number; y: number };
  client: Map<string, number>;
  provider: Map<string, number>;
};

export function layout(g: Graph): Layout {
  const rows = Math.max(g.clients.length, g.providers.length, 2);
  // Room under the fairlead for its label and one line per empty-route warning.
  const below = COL.fitting.r + 24 + g.problems.length * WARN_LINE;
  // The nodes fill the top band; the warnings hang below the fairlead and only add height under it.
  const band = rows * ROW + PAD * 2;
  const fy = band / 2;
  const height = Math.max(band, fy + below + PAD);
  const place = (ids: string[]) => {
    const top = (band - ids.length * ROW) / 2 + ROW / 2;
    return new Map(ids.map((id, i) => [id, top + i * ROW]));
  };
  return { height, fitting: { x: COL.fitting.cx, y: fy }, client: place(g.clients.map((c) => c.id)), provider: place(g.providers.map((p) => p.id)) };
}

const curve = (x1: number, y1: number, x2: number, y2: number, sag = 0) => {
  const mx = (x1 + x2) / 2;
  return `M${x1} ${y1} C${mx} ${y1 + sag} ${mx} ${y2 + sag} ${x2} ${y2}`;
};

export const FRAY = 44;

export const paths = {
  clientToFitting: (l: Layout, client: string) => curve(COL.client.x + COL.client.w, l.client.get(client) ?? l.fitting.y, l.fitting.x - COL.fitting.r, l.fitting.y),
  /** The fairlead to a provider. Slack (Kedge) sags; a broken lead stops short of the provider. */
  lead: (l: Layout, provider: string, mode: 'taut' | 'slack' | 'short' = 'taut') => {
    const y = l.provider.get(provider) ?? l.fitting.y;
    const end = mode === 'short' ? COL.provider.x - FRAY : COL.provider.x;
    return curve(l.fitting.x + COL.fitting.r, l.fitting.y, end, y, mode === 'slack' ? 34 : 0);
  },
  /** Loose strands at the end of a broken lead. */
  fray: (l: Layout, provider: string) => {
    const x = COL.provider.x - FRAY, y = l.provider.get(provider) ?? 0;
    return `M${x} ${y} l13 -7 M${x} ${y} l16 1 M${x} ${y} l12 8`;
  },
};

// ---------- live requests ----------

export type AttemptState = 'trying' | 'ok' | 'failed' | 'skipped' | 'lost' | 'cancelled';
export type FlightAttempt = { target: string; via: string | null; state: AttemptState; outcome?: string; error?: string | null; ms?: number | null };
export type Flight = {
  id: string;
  client: string;
  requestedRoute: string | null;
  route: string | null;
  attempts: FlightAttempt[];
  phase: 'routing' | 'trying' | 'answering' | 'done';
  status: number | null;
  error: string | null;
  kedge: boolean;
  sensitive: boolean;
  failover: boolean;
  totalMs: number | null;
  firstByteMs: number | null;
  /** Local clock (ms) of the last event, for expiry and fade-out. */
  at: number;
  /** Made from a finished record only (polling, or the start event was missed): no step-by-step animation. */
  replayed: boolean;
};

type Ev = Record<string, unknown>;
const str = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : null);
const num = (v: unknown) => (typeof v === 'number' ? v : null);

function outcomeState(outcome: string): AttemptState {
  if (outcome === 'ok') return 'ok';
  if (outcome === 'skipped') return 'skipped';
  if (outcome === 'lost-race') return 'lost';
  if (outcome === 'cancelled') return 'cancelled';
  return 'failed';
}

/** Which route a leg belongs to: the request's route, or 'local' after a fallback (inferred from config for replays). */
export function viaOf(g: Graph, f: Flight, a: FlightAttempt): string | null {
  if (a.via === 'discovered') return 'local';
  if (a.via) return a.via;
  const route = g.routes.find((r) => r.id === f.route);
  if (route && !route.targets.includes(a.target) && g.routes.find((r) => r.id === 'local')?.targets.includes(a.target)) return 'local';
  return f.route;
}

export function reduce(flights: Flight[], type: string, data: Ev, now: number): Flight[] {
  const id = str(data.id);
  if (!id) return flights;
  const i = flights.findIndex((f) => f.id === id);
  const cur = i >= 0 ? flights[i]! : null;
  const put = (f: Flight) => (i >= 0 ? flights.map((x, j) => (j === i ? f : x)) : [f, ...flights]);

  if (type === 'start') {
    if (cur) return flights;
    return put({
      id, client: str(data.client) ?? NO_KEY, requestedRoute: str(data.requestedRoute), route: str(data.route), attempts: [],
      phase: str(data.error) ? 'done' : 'routing', status: null, error: str(data.error), kedge: data.kedge === true, sensitive: data.sensitive === true,
      failover: false, totalMs: null, firstByteMs: null, at: now, replayed: false,
    });
  }
  if (type === 'attempt') {
    if (!cur || cur.phase === 'done') return flights;
    const target = str(data.target);
    if (!target) return flights;
    if (data.phase === 'begin') {
      // A replayed begin (the feed reconnected) must not draw a second attempt.
      if (cur.attempts.some((a) => a.target === target)) return flights;
      return put({ ...cur, phase: 'trying', at: now, attempts: [...cur.attempts, { target, via: str(data.via), state: 'trying' }] });
    }
    const outcome = str(data.outcome) ?? 'failed';
    const state = outcomeState(outcome);
    let k = -1;
    for (let j = cur.attempts.length - 1; j >= 0; j--) if (cur.attempts[j]!.target === target && cur.attempts[j]!.state === 'trying') { k = j; break; }
    if (k < 0 && cur.attempts.some((x) => x.target === target && x.outcome === outcome)) return flights;
    const ended: FlightAttempt = { target, via: k >= 0 ? cur.attempts[k]!.via : null, state, outcome, error: str(data.error), ms: num(data.ms) };
    const attempts = k >= 0 ? cur.attempts.map((a, j) => (j === k ? ended : a)) : [...cur.attempts, ended];
    return put({ ...cur, at: now, attempts, phase: state === 'ok' ? 'answering' : cur.phase });
  }
  if (type === 'firstByte') {
    if (!cur || cur.phase === 'done') return flights;
    return put({ ...cur, phase: 'answering', firstByteMs: num(data.ms), at: now });
  }
  if (type === 'done') {
    const attempts: FlightAttempt[] = Array.isArray(data.attempts)
      ? (data.attempts as Ev[]).map((a) => {
          const target = str(a.target) ?? '';
          const prior = cur?.attempts.find((x) => x.target === target);
          return { target, via: prior?.via ?? null, state: outcomeState(str(a.outcome) ?? 'failed'), outcome: str(a.outcome) ?? undefined, error: str(a.error), ms: num(a.ms) };
        })
      : cur?.attempts ?? [];
    const base: Flight = cur ?? {
      id, client: str(data.client) ?? NO_KEY, requestedRoute: null, route: str(data.route), attempts: [], phase: 'done', status: null, error: null,
      kedge: false, sensitive: false, failover: false, totalMs: null, firstByteMs: null, at: now, replayed: true,
    };
    return put({
      ...base, client: str(data.client) ?? base.client, route: str(data.route) ?? base.route, attempts, phase: 'done', status: num(data.status), error: str(data.error),
      kedge: data.kedge === true || base.kedge, sensitive: data.sensitive === true || base.sensitive, failover: data.failover === true,
      totalMs: num(data.totalMs), firstByteMs: num(data.firstByteMs) ?? base.firstByteMs, at: now,
    });
  }
  return flights;
}

/** Keeps finished requests on screen for a moment, drops ones that went silent, and caps the list. */
export function prune(flights: Flight[], now: number, keepDoneMs = 6000, staleMs = 15 * 60 * 1000, max = 40): Flight[] {
  const kept = flights.filter((f) => (f.phase === 'done' ? now - f.at < keepDoneMs : now - f.at < staleMs));
  return kept.length > max ? kept.slice(0, max) : kept;
}

/** What traffic revealed that config does not list (direct targets, discovered local models, clients without keys). */
export function seenFrom(flights: Flight[], prev: Seen): Seen {
  const clients = [...prev.clients], routes = [...prev.routes], targets = [...prev.targets];
  for (const f of flights) {
    if (!clients.includes(f.client)) clients.push(f.client);
    if (f.route && !routes.includes(f.route)) routes.push(f.route);
    for (const a of f.attempts) if (a.target && !targets.some((t) => t.id === a.target)) targets.push({ id: a.target, local: false });
  }
  return { clients, routes, targets };
}

/** One line per request, for screen readers and for anyone who would rather read than watch. */
export function describe(f: Flight): string {
  const hops = [f.client === NO_KEY ? 'no client key' : f.client];
  if (f.requestedRoute && f.requestedRoute !== f.route) hops.push(f.requestedRoute);
  if (f.route) hops.push(f.route);
  const fallback = f.attempts.find((a) => a.via && a.via !== f.route && a.via !== 'discovered');
  if (fallback) hops.push(`fallback ${fallback.via}`);
  else if (f.attempts.some((a) => a.via === 'discovered')) hops.push('fallback to a model found locally');
  const tries = f.attempts.filter((a) => a.state !== 'skipped' && a.state !== 'lost');
  for (const a of tries) hops.push(a.state === 'failed' ? `${a.target} failed (${a.outcome})` : a.state === 'trying' ? `${a.target}…` : a.target);
  const tail = f.phase === 'done' ? (f.status !== null && f.status >= 400 ? ` · ${f.status} ${f.error ?? ''}`.trimEnd() : f.totalMs !== null ? ` · ${f.totalMs} ms` : '') : f.phase === 'answering' ? ' · answering' : '';
  return hops.join(' → ') + tail + (f.kedge ? ' · Kedge mode' : '') + (f.sensitive ? ' · private' : '');
}
