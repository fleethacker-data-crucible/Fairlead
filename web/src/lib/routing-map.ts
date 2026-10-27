// The live routing map's model: which leads exist (from config and status), where they sit, and what each
// request in flight has done so far (from /api/live events). Pure functions only, so they run under node:test.
import type { Config, Status } from './schemas';

export const NO_KEY = '(no key)';
export const MORE = '(more)';

export type TargetState = 'ready' | 'kedge' | 'down' | 'paused' | 'disabled' | 'missing' | 'nokey' | 'unknown';

export type ClientNode = { id: string; label: string };
export type RouteNode = {
  id: string;
  strategy: string;
  targets: string[];
  /** A non-auto route with no targets: every request to it falls back (or fails). */
  empty: boolean;
  auto: boolean;
  localFallback: boolean;
  /** Not in config: a model named a provider target directly, or the route was deleted. */
  dynamic: boolean;
};
export type TargetNode = { id: string; provider: string; model: string; local: boolean; state: TargetState; dynamic: boolean };
export type Problem = { tone: 'err' | 'warn'; route?: string; text: string };

export type Graph = {
  clients: ClientNode[];
  routes: RouteNode[];
  targets: TargetNode[];
  /** Routes an auto route can choose (rules plus the auto fallback). */
  autoEdges: Array<{ from: string; to: string }>;
  problems: Problem[];
  kedge: boolean;
  hiddenClients: number;
  hiddenTargets: number;
};

export type Seen = { clients: string[]; routes: string[]; targets: Array<{ id: string; local: boolean }> };

const MAX_CLIENTS = 8;
const MAX_TARGETS = 14;

function providerOf(ref: string) {
  const i = ref.indexOf('/');
  return i < 0 ? { provider: ref, model: '' } : { provider: ref.slice(0, i), model: ref.slice(i + 1) };
}

/** Builds the leads from the real config: one per client key, per route, per route target. Nothing is fixed. */
export function buildGraph(config: Config, status: Status | undefined, seen: Seen = { clients: [], routes: [], targets: [] }): Graph {
  const kedge = status?.connectivity.kedge ?? false;
  const providers = new Map(config.providers.map((p) => [p.id, p]));
  const health = new Map((status?.providers ?? []).map((p) => [p.id, p]));

  const clientIds = config.clients.map((c) => c.name);
  for (const c of seen.clients) if (!clientIds.includes(c)) clientIds.push(c);
  if ((!config.requireClientKey || seen.clients.includes(NO_KEY)) && !clientIds.includes(NO_KEY)) clientIds.push(NO_KEY);
  const shownClients = clientIds.length > MAX_CLIENTS ? clientIds.slice(0, MAX_CLIENTS - 1) : clientIds;
  const clients: ClientNode[] = shownClients.map((id) => ({ id, label: id === NO_KEY ? 'no client key' : id }));
  const hiddenClients = clientIds.length - shownClients.length;
  if (hiddenClients > 0) clients.push({ id: MORE, label: `+${hiddenClients} more` });

  // auto routes first (they hand off to the others), then config order, then routes only traffic revealed.
  const configured = [...config.routes].sort((a, b) => Number(b.strategy === 'auto') - Number(a.strategy === 'auto'));
  const routes: RouteNode[] = configured.map((r) => ({
    id: r.id,
    strategy: r.strategy,
    targets: r.strategy === 'auto' ? [] : r.targets,
    empty: r.strategy !== 'auto' && r.targets.length === 0,
    auto: r.strategy === 'auto',
    localFallback: r.localFallback,
    dynamic: false,
  }));
  for (const id of seen.routes) {
    if (routes.some((r) => r.id.toLowerCase() === id.toLowerCase())) continue;
    routes.push({ id, strategy: 'direct', targets: id.includes('/') ? [id] : [], empty: false, auto: false, localFallback: true, dynamic: true });
  }

  const autoEdges: Graph['autoEdges'] = [];
  for (const r of routes.filter((x) => x.auto)) {
    const to = new Set<string>();
    for (const rule of config.rules) if (rule.enabled) to.add(rule.route);
    to.add(config.autoFallbackRoute);
    for (const t of to) if (t !== r.id && routes.some((x) => x.id === t)) autoEdges.push({ from: r.id, to: t });
  }

  // Targets grouped by the first route that uses them, so leads cross as little as possible.
  const targetIds: string[] = [];
  for (const r of routes) for (const t of r.targets) if (!targetIds.includes(t)) targetIds.push(t);
  for (const t of seen.targets) if (!targetIds.includes(t.id)) targetIds.push(t.id);
  const stateOf = (ref: string): { local: boolean; state: TargetState } => {
    const { provider } = providerOf(ref);
    const p = providers.get(provider);
    const fromSeen = seen.targets.find((t) => t.id === ref);
    if (!p) return { local: fromSeen?.local ?? false, state: 'missing' };
    const h = health.get(provider);
    if (!p.enabled) return { local: p.local, state: 'disabled' };
    if (kedge && !p.local) return { local: p.local, state: 'kedge' };
    if (h?.breaker === 'open') return { local: p.local, state: 'paused' };
    if (!p.local && p.keySource === 'none') return { local: p.local, state: 'nokey' };
    if (h?.reachable === false || h?.authOk === false) return { local: p.local, state: 'down' };
    if (h?.reachable === true) return { local: p.local, state: 'ready' };
    return { local: p.local, state: 'unknown' };
  };
  const shownTargets = targetIds.length > MAX_TARGETS ? targetIds.slice(0, MAX_TARGETS - 1) : targetIds;
  const targets: TargetNode[] = shownTargets.map((id) => {
    const { provider, model } = providerOf(id);
    return { id, provider, model, ...stateOf(id), dynamic: !routes.some((r) => r.targets.includes(id)) };
  });
  const hiddenTargets = targetIds.length - shownTargets.length;
  if (hiddenTargets > 0) targets.push({ id: MORE, provider: '', model: `+${hiddenTargets} more`, local: false, state: 'unknown', dynamic: false });

  return { clients, routes, targets, autoEdges, problems: problems(config, routes, targets), kedge, hiddenClients, hiddenTargets };
}

function problems(config: Config, routes: RouteNode[], targets: TargetNode[]): Problem[] {
  const out: Problem[] = [];
  const local = routes.find((r) => r.id === 'local');
  const localEmpty = !local || local.empty;
  const empty = routes.filter((r) => r.empty && !r.dynamic);
  for (const r of empty) {
    let where: string;
    if (r.id === 'local') where = 'Fairlead picks whatever model it finds on a running local server, or answers 503 when there is none.';
    else if (!r.localFallback) where = 'Requests to it fail with 503.';
    else if (localEmpty) where = "Requests to it fall back to 'local', which is empty too, so they go to whatever model Fairlead finds on a local server.";
    else where = `Requests to it fall back to 'local' (${local!.targets.slice(0, 2).join(', ')}${local!.targets.length > 2 ? '…' : ''}).`;
    out.push({ tone: r.id === config.defaultRoute || r.id === config.autoFallbackRoute || r.id === 'local' ? 'err' : 'warn', route: r.id, text: `Route '${r.id}' has no targets. ${where}` });
  }
  for (const t of targets.filter((x) => x.state === 'missing' && !x.dynamic)) {
    out.push({ tone: 'warn', text: `Target ${t.id} names provider '${t.provider}', which is not configured; it is skipped.` });
  }
  return out;
}

// ---------- layout ----------

export const W = 1000;
export const ROW = 40;
export const NODE_H = 30;
export const COL = {
  client: { x: 8, w: 168 },
  fitting: { cx: 268, r: 18 },
  route: { x: 372, w: 168 },
  target: { x: 704, w: 288 },
} as const;
const PAD = 24;

export type Layout = {
  height: number;
  fitting: { x: number; y: number };
  client: Map<string, number>;
  route: Map<string, number>;
  target: Map<string, number>;
};

export function layout(g: Graph): Layout {
  const rows = Math.max(g.clients.length, g.routes.length, g.targets.length, 3);
  const height = rows * ROW + PAD * 2;
  const place = (ids: string[]) => {
    const top = (height - ids.length * ROW) / 2 + ROW / 2;
    return new Map(ids.map((id, i) => [id, top + i * ROW]));
  };
  return { height, fitting: { x: COL.fitting.cx, y: height / 2 }, client: place(g.clients.map((c) => c.id)), route: place(g.routes.map((r) => r.id)), target: place(g.targets.map((t) => t.id)) };
}

const curve = (x1: number, y1: number, x2: number, y2: number) => {
  const mx = (x1 + x2) / 2;
  return `M${x1} ${y1} C${mx} ${y1} ${mx} ${y2} ${x2} ${y2}`;
};

export const paths = {
  clientToFitting: (l: Layout, client: string) => curve(COL.client.x + COL.client.w, l.client.get(client) ?? l.fitting.y, l.fitting.x - COL.fitting.r, l.fitting.y),
  fittingToRoute: (l: Layout, route: string) => curve(l.fitting.x + COL.fitting.r, l.fitting.y, COL.route.x, l.route.get(route) ?? l.fitting.y),
  routeToTarget: (l: Layout, route: string, target: string) => curve(COL.route.x + COL.route.w, l.route.get(route) ?? 0, COL.target.x, l.target.get(target) ?? 0),
  /** auto hands off to the route its rules chose: an arc on the left of the route column. */
  autoArc: (l: Layout, from: string, to: string) => {
    const y1 = l.route.get(from) ?? 0, y2 = l.route.get(to) ?? 0;
    const bulge = COL.route.x - 22 - Math.min(40, Math.abs(y2 - y1) / 6);
    return `M${COL.route.x} ${y1} C${bulge} ${y1} ${bulge} ${y2} ${COL.route.x} ${y2}`;
  },
  /** An empty or dead route falls back to 'local': an arc on the right of the route column. */
  fallbackArc: (l: Layout, from: string, to: string) => {
    const y1 = l.route.get(from) ?? 0, y2 = l.route.get(to) ?? 0;
    const right = COL.route.x + COL.route.w;
    const bulge = right + 26 + Math.min(40, Math.abs(y2 - y1) / 6);
    return `M${right} ${y1} C${bulge} ${y1} ${bulge} ${y2} ${right} ${y2}`;
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

/** Where a target's lead hangs from: the route itself, or 'local' after a fallback (inferred from config for replays). */
export function viaOf(g: Graph, f: Flight, a: FlightAttempt): string | null {
  if (a.via === 'discovered') return f.route;
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
