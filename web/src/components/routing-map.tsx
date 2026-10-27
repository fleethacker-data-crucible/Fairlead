// The live routing map on the Overview: calling clients → the fairlead → one lead per route → one lead per target,
// all drawn from the real config. Each request lights its actual path as /api/live reports it: a failed attempt
// snaps red and the request re-leads to the next target, a fallback jumps to 'local', Kedge mode greys the cloud.
import { useEffect, useMemo, useRef, useState } from 'react';
import { LuCloud, LuCloudOff, LuHardDrive, LuTriangleAlert } from 'react-icons/lu';
import { navigate } from '../app';
import type { Config, Status } from '../lib/schemas';
import { useLive, type LiveState } from '../lib/live';
import {
  COL, MORE, NODE_H, NO_KEY, W, buildGraph, describe, layout, paths, prune, reduce, seenFrom, viaOf,
  type Flight, type FlightAttempt, type Graph, type Layout, type Seen, type TargetState,
} from '../lib/routing-map';
import { Button, Pill, cn } from './ui';

const HEAD = 22;

export function RoutingMap({ config, status }: { config: Config; status: Status }) {
  const [flights, setFlights] = useState<Flight[]>([]);
  const [recent, setRecent] = useState<Flight[]>([]);
  const [seen, setSeen] = useState<Seen>({ clients: [], routes: [], targets: [] });
  const [now, setNow] = useState(() => Date.now());

  const live = useLive((type, data) => {
    const t = Date.now();
    setFlights((fs) => {
      const next = prune(reduce(fs, type, data, t), t);
      setSeen((s) => seenFrom(next, s));
      if (type === 'done') {
        const f = next.find((x) => x.id === data.id);
        if (f) setRecent((r) => [f, ...r.filter((x) => x.id !== f.id)].slice(0, 5));
      }
      return next;
    });
  });

  // Tick while anything is on screen, so finished requests fade and drop off.
  const active = flights.length > 0;
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => { const t = Date.now(); setNow(t); setFlights((fs) => prune(fs, t)); }, 250);
    return () => clearInterval(id);
  }, [active]);

  const graph = useMemo(() => buildGraph(config, status, seen), [config, status, seen]);
  const l = useMemo(() => layout(graph), [graph]);
  const reduced = useReducedMotion();
  const c = status.connectivity;
  const localReady = status.providers.some((p) => p.local && p.enabled && p.reachable);

  return (
    <section className="panel overflow-hidden" aria-label="Live routing">
      <header className="flex flex-wrap items-center justify-between gap-3 px-5 pt-4">
        <div className="flex flex-col">
          <h2 className="text-[16px] font-semibold">Where traffic goes</h2>
          <span className="text-[12.5px] text-ink-2">Every request lights its real path: client, route, then the target that answered. <span className="mono">{status.gatewayUrl}</span></span>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {c.kedge ? <Pill tone="warn"><LuCloudOff size={13} className="mr-1 inline" />Kedge mode: cloud leads parked</Pill> : c.internet === 'down' ? <Pill tone="warn">Cloud unreachable</Pill> : null}
          <LiveBadge state={live} />
        </div>
      </header>

      {graph.problems.length > 0 ? (
        <div className="mx-5 mt-3 flex flex-col gap-1 rounded-[6px] border border-warn bg-warn-bg px-3 py-2 text-[13px]" role="alert">
          {graph.problems.map((p, i) => (
            <div key={i} className="flex items-start gap-2">
              <LuTriangleAlert size={16} className={cn('mt-[2px] shrink-0', p.tone === 'err' ? 'text-err' : 'text-warn')} aria-hidden />
              <span>{p.text}</span>
            </div>
          ))}
          <div><Button size="sm" variant="quiet" onClick={() => navigate('routes')}>Add targets on Routes</Button></div>
        </div>
      ) : null}

      <svg viewBox={`0 0 ${W} ${l.height + HEAD}`} className="block w-full" role="img" aria-label={summary(graph)}>
        <g fontSize="12" fill="var(--ink-2)">
          <text x={COL.client.x} y={14}>Clients</text>
          <text x={COL.fitting.cx} y={14} textAnchor="middle">Fairlead</text>
          <text x={COL.route.x} y={14}>Routes</text>
          <text x={COL.target.x} y={14}>Targets</text>
        </g>
        <g transform={`translate(0 ${HEAD})`}>
          <BaseLeads g={graph} l={l} />
          {flights.map((f) => <FlightLeads key={f.id} f={f} g={graph} l={l} now={now} />)}
          <Nodes g={graph} l={l} flights={flights} />
          {!reduced ? flights.map((f) => <FlightPackets key={f.id} f={f} g={graph} l={l} />) : null}
        </g>
      </svg>

      <div className="hairline flex flex-col gap-1 px-5 py-2 text-[12.5px]">
        <div aria-live="polite" className="flex flex-col gap-0.5">
          {recent.length === 0 && flights.length === 0 ? (
            <span className="text-ink-2">{live === 'live' ? 'Waiting for the next request. Each one will light its path above.' : live === 'polling' ? 'Watching the activity log; finished requests appear here.' : 'Connecting to the live feed…'}</span>
          ) : (
            [...flights.filter((f) => f.phase !== 'done'), ...recent].slice(0, 5).map((f) => (
              <span key={f.id} className={cn('mono truncate', f.phase === 'done' && f.status !== null && f.status >= 400 ? 'text-err' : f.phase === 'done' ? 'text-ink-2' : 'text-ink')}>{describe(f)}</span>
            ))
          )}
        </div>
        {!localReady ? <span className="text-warn">No local model server is reachable, so there is nothing to fall back to when the cloud drops. Start Ollama (or add one on Providers).</span> : null}
      </div>
    </section>
  );
}

function LiveBadge({ state }: { state: LiveState }) {
  if (state === 'live') return <Pill tone="ok">Live</Pill>;
  if (state === 'polling') return <Pill tone="neutral">Polling every 2 s</Pill>;
  if (state === 'offline') return <Pill tone="warn">Reconnecting</Pill>;
  return <Pill tone="neutral">Connecting</Pill>;
}

function summary(g: Graph) {
  const empty = g.routes.filter((r) => r.empty).map((r) => r.id);
  return `${g.clients.length} clients, ${g.routes.length} routes, ${g.targets.length} targets` + (empty.length ? `; routes with no targets: ${empty.join(', ')}` : '') + (g.kedge ? '; Kedge mode, cloud targets parked' : '');
}

function useReducedMotion() {
  const [reduced, setReduced] = useState(() => typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true);
  useEffect(() => {
    const m = window.matchMedia?.('(prefers-reduced-motion: reduce)');
    if (!m) return;
    const on = () => setReduced(m.matches);
    m.addEventListener('change', on);
    return () => m.removeEventListener('change', on);
  }, []);
  return reduced;
}

const trunc = (s: string, n: number) => (s.length > n ? s.slice(0, n - 1) + '…' : s);
const parked = (s: TargetState) => s !== 'ready' && s !== 'unknown';

// ---------- the leads at rest ----------

function BaseLeads({ g, l }: { g: Graph; l: Layout }) {
  const local = g.routes.find((r) => r.id === 'local');
  return (
    <g fill="none" strokeWidth="1.5">
      {g.clients.map((c) => <path key={c.id} d={paths.clientToFitting(l, c.id)} stroke="var(--line-2)" />)}
      {g.routes.map((r) => <path key={r.id} d={paths.fittingToRoute(l, r.id)} stroke="var(--line-2)" />)}
      {g.autoEdges.map((e) => <path key={e.from + e.to} d={paths.autoArc(l, e.from, e.to)} stroke="var(--line-2)" strokeDasharray="2 4" />)}
      {g.routes.flatMap((r) => r.targets.filter((t) => l.target.has(t)).map((t) => {
        const target = g.targets.find((x) => x.id === t);
        const off = target ? parked(target.state) : true;
        return <path key={r.id + t} d={paths.routeToTarget(l, r.id, t)} stroke="var(--line-2)" strokeDasharray={off ? '2 6' : undefined} opacity={off ? 0.7 : 1} />;
      }))}
      {/* Empty routes: the lead that silently jumps to 'local', drawn so it is not silent any more. */}
      {local ? g.routes.filter((r) => r.empty && r.id !== 'local' && r.localFallback).map((r) => (
        <path key={'fb' + r.id} d={paths.fallbackArc(l, r.id, local.id)} stroke="var(--warn)" strokeDasharray="4 4" />
      )) : null}
      {g.routes.filter((r) => r.empty && (r.id === 'local' || !r.localFallback || !local)).map((r) => (
        <g key={'stub' + r.id} stroke="var(--err)">
          <path d={`M${COL.route.x + COL.route.w} ${l.route.get(r.id)} h28`} strokeDasharray="4 4" />
          <path d={`M${COL.route.x + COL.route.w + 30} ${(l.route.get(r.id) ?? 0) - 5} l8 10 M${COL.route.x + COL.route.w + 38} ${(l.route.get(r.id) ?? 0) - 5} l-8 10`} />
        </g>
      ))}
    </g>
  );
}

// ---------- one request's path ----------

function clientNode(g: Graph, client: string) {
  return g.clients.some((c) => c.id === client) ? client : g.clients.some((c) => c.id === MORE) ? MORE : client;
}
function targetNode(l: Layout, target: string) {
  return l.target.has(target) ? target : MORE;
}

function FlightLeads({ f, g, l, now }: { f: Flight; g: Graph; l: Layout; now: number }) {
  const done = f.phase === 'done';
  const failed = done && f.status !== null && f.status >= 400;
  const fade = done ? Math.max(0, 1 - (now - f.at) / 6000) : 1;
  const flow = !done ? 'lead-line' : undefined;
  const ink = failed ? 'var(--err)' : 'var(--accent)';
  const entry = f.requestedRoute && l.route.has(f.requestedRoute) ? f.requestedRoute : f.route;
  return (
    <g fill="none" strokeWidth="2.5" opacity={fade} strokeLinecap="round">
      <path d={paths.clientToFitting(l, clientNode(g, f.client))} stroke={ink} className={flow} />
      {entry ? <path d={paths.fittingToRoute(l, entry)} stroke={ink} className={flow} /> : null}
      {entry && f.route && entry !== f.route ? <path d={paths.autoArc(l, entry, f.route)} stroke={ink} className={flow} /> : null}
      {f.attempts.map((a, i) => <AttemptLead key={i} f={f} a={a} g={g} l={l} />)}
    </g>
  );
}

function AttemptLead({ f, a, g, l }: { f: Flight; a: FlightAttempt; g: Graph; l: Layout }) {
  const via = viaOf(g, f, a);
  if (!via || !l.route.has(via) || a.state === 'lost') return null;
  const t = targetNode(l, a.target);
  const d = paths.routeToTarget(l, via, t);
  const fallback = f.route && via !== f.route && l.route.has(f.route) ? <path d={paths.fallbackArc(l, f.route, via)} stroke="var(--warn)" className={a.state === 'trying' ? 'lead-line' : undefined} /> : null;
  if (a.state === 'failed' || a.state === 'skipped' || a.state === 'cancelled') {
    // The lead snaps: red, broken in the middle, with an X where it parted.
    const x = (COL.route.x + COL.route.w + COL.target.x) / 2;
    const y = ((l.route.get(via) ?? 0) + (l.target.get(t) ?? 0)) / 2;
    return (
      <g>
        {fallback}
        <path d={d} stroke="var(--err)" strokeDasharray={a.state === 'skipped' ? '1 5' : '14 6'} strokeWidth="2" />
        <path d={`M${x - 5} ${y - 5} l10 10 M${x + 5} ${y - 5} l-10 10`} stroke="var(--err)" strokeWidth="2.5" />
        <title>{`${a.target}: ${a.outcome ?? a.state}${a.error ? ' — ' + a.error : ''}`}</title>
      </g>
    );
  }
  return (
    <g>
      {fallback}
      <path d={d} stroke="var(--accent)" className={a.state === 'trying' ? 'lead-line' : undefined} strokeWidth={a.state === 'ok' ? 3 : 2.5} />
    </g>
  );
}

// ---------- moving packets (off under reduced motion) ----------

function FlightPackets({ f, g, l }: { f: Flight; g: Graph; l: Layout }) {
  if (f.replayed) return null;
  const entry = f.requestedRoute && l.route.has(f.requestedRoute) ? f.requestedRoute : f.route;
  const inbound = [paths.clientToFitting(l, clientNode(g, f.client))];
  if (entry) inbound.push(paths.fittingToRoute(l, entry));
  if (entry && f.route && entry !== f.route) inbound.push(paths.autoArc(l, entry, f.route));
  const last = f.attempts[f.attempts.length - 1];
  const leg = (a: FlightAttempt) => {
    const via = viaOf(g, f, a);
    if (!via || !l.route.has(via)) return [];
    const parts = [];
    if (f.route && via !== f.route && l.route.has(f.route)) parts.push(paths.fallbackArc(l, f.route, via));
    parts.push(paths.routeToTarget(l, via, targetNode(l, a.target)));
    return parts;
  };
  const ok = f.attempts.find((a) => a.state === 'ok');
  return (
    <g>
      <Packet key="in" d={inbound.join(' ')} ms={550} tone="var(--accent)" />
      {last ? <Packet key={'try' + f.attempts.length} d={leg(last).join(' ')} ms={450} delay={f.attempts.length === 1 ? 500 : 0} tone={last.state === 'failed' ? 'var(--err)' : 'var(--accent)'} /> : null}
      {(f.phase === 'answering' || f.phase === 'done') && ok ? <Packet key="back" d={[...inbound, ...leg(ok)].join(' ')} ms={800} reverse tone="var(--ok)" /> : null}
    </g>
  );
}

function Packet({ d, ms, delay = 0, reverse = false, tone }: { d: string; ms: number; delay?: number; reverse?: boolean; tone: string }) {
  const path = useRef<SVGPathElement>(null);
  const dot = useRef<SVGCircleElement>(null);
  useEffect(() => {
    const p = path.current, c = dot.current;
    if (!p || !c || !d) return;
    let total = 0;
    try { total = p.getTotalLength(); } catch { return; }
    let raf = 0;
    const start = performance.now() + delay;
    const step = (t: number) => {
      const k = Math.min(1, Math.max(0, (t - start) / ms));
      const e = k < 0.5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
      const pt = p.getPointAtLength((reverse ? 1 - e : e) * total);
      c.setAttribute('cx', String(pt.x));
      c.setAttribute('cy', String(pt.y));
      c.setAttribute('opacity', t < start ? '0' : k >= 1 ? '0' : '1');
      if (k < 1) raf = requestAnimationFrame(step);
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }, [d, ms, delay, reverse]);
  return (
    <g pointerEvents="none">
      <path ref={path} d={d} fill="none" stroke="none" />
      <circle ref={dot} r="5" fill={tone} stroke="var(--fl-card)" strokeWidth="2" opacity="0" />
    </g>
  );
}

// ---------- nodes ----------

const STATE_LABEL: Record<TargetState, string | null> = { ready: null, unknown: null, kedge: 'Kedge', down: 'down', paused: 'paused', disabled: 'disabled', missing: 'no provider', nokey: 'no key' };

function Nodes({ g, l, flights }: { g: Graph; l: Layout; flights: Flight[] }) {
  const inFlight = flights.filter((f) => f.phase !== 'done');
  const busy = (pred: (f: Flight) => boolean) => inFlight.filter(pred).length;
  const box = { rx: 6, height: NODE_H };
  return (
    <g fontSize="12.5">
      {g.clients.map((c) => {
        const y = l.client.get(c.id) ?? 0;
        const n = busy((f) => clientNode(g, f.client) === c.id);
        return (
          <g key={c.id}>
            <rect x={COL.client.x} y={y - NODE_H / 2} width={COL.client.w} {...box} fill="var(--fl-card)" stroke={n ? 'var(--accent)' : 'var(--line)'} />
            <text x={COL.client.x + 10} y={y + 4} fill={c.id === NO_KEY || c.id === MORE ? 'var(--ink-2)' : 'var(--ink)'}>{trunc(c.label, 22)}</text>
            {n ? <text x={COL.client.x + COL.client.w - 8} y={y + 4} textAnchor="end" fill="var(--accent)" className="mono">{n}</text> : null}
          </g>
        );
      })}

      <g>
        <circle cx={l.fitting.x} cy={l.fitting.y} r={COL.fitting.r} fill="var(--fl-card)" stroke="var(--brass)" strokeWidth="2" />
        <circle cx={l.fitting.x} cy={l.fitting.y} r="5" fill={inFlight.length ? 'var(--accent)' : 'var(--brass)'} />
        <text x={l.fitting.x} y={l.fitting.y + COL.fitting.r + 16} textAnchor="middle" fill="var(--ink-2)" fontSize="12">{inFlight.length ? `${inFlight.length} in flight` : 'idle'}</text>
      </g>

      {g.routes.map((r) => {
        const y = l.route.get(r.id) ?? 0;
        const n = busy((f) => f.route === r.id || f.requestedRoute === r.id);
        const tone = r.empty ? (r.id === 'local' || !r.localFallback ? 'var(--err)' : 'var(--warn)') : n ? 'var(--accent)' : 'var(--line)';
        const sub = r.auto ? 'rules' : r.empty ? 'no targets' : r.dynamic ? 'direct' : `${r.strategy} · ${r.targets.length}`;
        return (
          <g key={r.id}>
            <title>{r.empty ? `Route '${r.id}' has no targets.` : r.auto ? `Route '${r.id}' picks a route per request with the routing rules.` : `Route '${r.id}': ${r.strategy}, ${r.targets.join(', ')}`}</title>
            <rect x={COL.route.x} y={y - NODE_H / 2} width={COL.route.w} {...box} fill={r.empty ? 'var(--warn-bg)' : 'var(--fl-card)'} stroke={tone} strokeDasharray={r.empty ? '4 3' : undefined} strokeWidth={r.empty || n ? 1.5 : 1} />
            <text x={COL.route.x + 10} y={y + 4} fill="var(--ink)" fontWeight="600" className="mono">{trunc(r.id, 14)}</text>
            <text x={COL.route.x + COL.route.w - 8} y={y + 4} textAnchor="end" fill={r.empty ? tone : 'var(--ink-2)'} fontSize="11.5" fontWeight={r.empty ? 600 : undefined}>{r.empty ? '⚠ ' + sub : sub}</text>
          </g>
        );
      })}

      {g.targets.map((t) => {
        const y = l.target.get(t.id) ?? 0;
        const n = busy((f) => f.attempts.some((a) => a.state === 'trying' && targetNode(l, a.target) === t.id));
        const off = parked(t.state);
        const Icon = t.local ? LuHardDrive : t.state === 'kedge' ? LuCloudOff : LuCloud;
        const label = STATE_LABEL[t.state];
        return (
          <g key={t.id} opacity={off ? 0.6 : 1}>
            <title>{t.id === MORE ? 'More targets than fit here; see Routes.' : `${t.id}${t.local ? ' (local)' : ' (cloud)'}${label ? ': ' + label : ''}${t.dynamic ? ' — not in any route' : ''}`}</title>
            <rect x={COL.target.x} y={y - NODE_H / 2} width={COL.target.w} {...box} fill="var(--fl-card)" stroke={n ? 'var(--accent)' : 'var(--line)'} strokeDasharray={off ? '3 3' : t.dynamic ? '6 3' : undefined} strokeWidth={n ? 1.5 : 1} />
            {t.id !== MORE ? <Icon x={COL.target.x + 8} y={y - 7} size={14} color="var(--ink-2)" aria-hidden /> : null}
            <text x={COL.target.x + 28} y={y + 4} fill={off ? 'var(--ink-2)' : 'var(--ink)'} className="mono">{trunc(t.model || t.id, label ? 22 : 28)}</text>
            <text x={COL.target.x + COL.target.w - 8} y={y + 4} textAnchor="end" fill={label ? (t.state === 'kedge' ? 'var(--ink-2)' : 'var(--err)') : 'var(--ink-2)'} fontSize="11.5">{label ?? trunc(t.provider, 12)}</text>
          </g>
        );
      })}
    </g>
  );
}
