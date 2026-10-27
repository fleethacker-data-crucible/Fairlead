// The live routing map on the Overview: calling clients → the fairlead → one lead per provider, all drawn from the
// real config. The local lead is always made fast (the Kedge anchor); a cloud lead pays out when it starts carrying
// traffic and fades to an outline when idle, but never disappears. A broken lead is red and frayed, short of its
// provider; in Kedge mode cloud leads go slack. Each request is a bead in its route's colour travelling the lead it
// actually took; a failed attempt bounces back red and the bead goes out on the next lead. Click a lead for its config.
import { useEffect, useMemo, useRef, useState } from 'react';
import { LuCloud, LuCloudOff, LuHardDrive } from 'react-icons/lu';
import { navigate } from '../app';
import type { Config, Status } from '../lib/schemas';
import { useLive, type LiveState } from '../lib/live';
import {
  COL, MORE, NODE_H, NO_KEY, W, WARN_LINE, buildGraph, describe, layout, paths, providerOf, prune, reduce, routeTone, seenFrom, viaOf,
  type Flight, type Graph, type Layout, type ProviderNode, type Seen,
} from '../lib/routing-map';
import { ProviderPanel } from './provider-panel';
import { Button, Pill, cn } from './ui';

const HEAD = 22;
/** A cloud lead stays paid out this long after its last request, then fades to an outline. */
const ACTIVE_MS = 20000;

export function RoutingMap({ config, status }: { config: Config; status: Status }) {
  const [flights, setFlights] = useState<Flight[]>([]);
  const [recent, setRecent] = useState<Flight[]>([]);
  const [seen, setSeen] = useState<Seen>({ clients: [], routes: [], targets: [] });
  const [lastUsed, setLastUsed] = useState<Record<string, number>>({});
  const [selected, setSelected] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const live = useLive((type, data) => {
    const t = Date.now();
    if (type === 'attempt' && data.phase === 'begin' && typeof data.target === 'string') {
      const p = providerOf(data.target).provider;
      setLastUsed((u) => ({ ...u, [p]: t }));
    }
    setFlights((fs) => {
      const next = prune(reduce(fs, type, data, t), t);
      setSeen((s) => seenFrom(next, s));
      if (type === 'done') {
        const f = next.find((x) => x.id === data.id);
        if (f) {
          setRecent((r) => [f, ...r.filter((x) => x.id !== f.id)].slice(0, 5));
          // A replayed record (polling) still marks the lead it took as in use.
          const ok = f.attempts.find((a) => a.state === 'ok');
          if (ok) setLastUsed((u) => ({ ...u, [providerOf(ok.target).provider]: t }));
        }
      }
      return next;
    });
  });

  // Tick while anything moves or a lead is still paid out, so finished requests fade and idle leads slacken.
  const busy = flights.length > 0 || Object.values(lastUsed).some((t) => now - t < ACTIVE_MS + 1000);
  useEffect(() => {
    if (!busy) return;
    const id = setInterval(() => { const t = Date.now(); setNow(t); setFlights((fs) => prune(fs, t)); }, 250);
    return () => clearInterval(id);
  }, [busy]);

  const graph = useMemo(() => buildGraph(config, status, seen), [config, status, seen]);
  const l = useMemo(() => layout(graph), [graph]);
  const reduced = useReducedMotion();
  const c = status.connectivity;
  const routes = graph.routes.filter((r) => !r.auto && !r.dynamic);
  const chosen = selected ? graph.providers.find((p) => p.id === selected) ?? null : null;

  return (
    <section className="panel relative overflow-hidden" aria-label="Live routing">
      <header className="flex flex-wrap items-center justify-between gap-3 px-5 pt-4">
        <div className="flex flex-col">
          <h2 className="text-[16px] font-semibold">Where traffic goes</h2>
          <span className="text-[12.5px] text-ink-2">Each request is a bead on the lead it actually took. Click a lead for its provider. <span className="mono">{status.gatewayUrl}</span></span>
        </div>
        <div className="flex flex-wrap items-center gap-3 text-[12px]">
          <span className="flex flex-wrap items-center gap-2" aria-label="Bead colours">
            {routes.map((r) => (
              <span key={r.id} className={cn('inline-flex items-center gap-1', r.empty && 'text-ink-2')}>
                <svg width="10" height="10" aria-hidden><circle cx="5" cy="5" r="4.5" fill={routeTone(r.id)} /></svg>
                <span className="mono">{r.id}</span>{r.empty ? <span className="text-warn">(empty)</span> : null}
              </span>
            ))}
          </span>
          {c.kedge ? <Pill tone="warn"><LuCloudOff size={13} className="mr-1 inline" />Kedge mode: cloud leads slack</Pill> : c.internet === 'down' ? <Pill tone="warn">Cloud unreachable</Pill> : null}
          <LiveBadge state={live} />
        </div>
      </header>

      <svg viewBox={`0 0 ${W} ${l.height + HEAD}`} className="block w-full" role="img" aria-label={summary(graph)}>
        <g fontSize="12" fill="var(--ink-2)">
          <text x={COL.client.x} y={14}>Clients</text>
          <text x={COL.fitting.cx} y={14} textAnchor="middle">Fairlead</text>
          <text x={COL.provider.x} y={14}>Providers</text>
        </g>
        <g transform={`translate(0 ${HEAD})`}>
          <ClientLeads g={graph} l={l} flights={flights} now={now} />
          <ProviderLeads g={graph} l={l} flights={flights} lastUsed={lastUsed} now={now} selected={selected} onSelect={setSelected} />
          <Nodes g={graph} l={l} flights={flights} lastUsed={lastUsed} now={now} selected={selected} onSelect={setSelected} />
          {!reduced ? flights.map((f) => <Beads key={f.id} f={f} g={graph} l={l} />) : null}
        </g>
      </svg>

      <div className="hairline flex flex-col gap-1 px-5 py-2 text-[12.5px]">
        {graph.problems.length > 0 ? (
          <div className="flex flex-wrap items-center gap-2 text-warn" role="status">
            <span>{graph.problems.length === 1 ? graph.problems[0]!.text : `${graph.problems.length} routes have no targets, so their requests fall back without telling the caller.`}</span>
            <Button size="sm" variant="quiet" onClick={() => navigate('routes')}>Add targets on Routes</Button>
          </div>
        ) : null}
        <div aria-live="polite" className="flex flex-col gap-0.5">
          {recent.length === 0 && flights.length === 0 ? (
            <span className="text-ink-2">{live === 'live' ? 'Waiting for the next request.' : live === 'polling' ? 'Watching the activity log; finished requests appear here.' : 'Connecting to the live feed…'}</span>
          ) : (
            [...flights.filter((f) => f.phase !== 'done'), ...recent].slice(0, 5).map((f) => (
              <span key={f.id} className={cn('mono truncate', f.phase === 'done' && f.status !== null && f.status >= 400 ? 'text-err' : f.phase === 'done' ? 'text-ink-2' : 'text-ink')}>{describe(f)}</span>
            ))
          )}
        </div>
      </div>

      {chosen && chosen.id !== MORE ? <ProviderPanel node={chosen} config={config} status={status} onClose={() => setSelected(null)} /> : null}
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
  const broken = g.providers.filter((p) => p.health === 'broken').map((p) => `${p.name} (${p.why})`);
  return `${g.clients.length} clients, ${g.providers.length} providers` + (broken.length ? `; broken: ${broken.join(', ')}` : '') + (g.problems.length ? '; ' + g.problems.map((p) => p.short).join('; ') : '') + (g.kedge ? '; Kedge mode, cloud leads slack' : '');
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
const fadeOf = (f: Flight, now: number) => (f.phase === 'done' ? Math.max(0, 1 - (now - f.at) / 6000) : 1);

function clientNode(g: Graph, client: string) {
  return g.clients.some((c) => c.id === client) ? client : g.clients.some((c) => c.id === MORE) ? MORE : client;
}
function providerNode(l: Layout, target: string) {
  const p = providerOf(target).provider;
  return l.provider.has(p) ? p : MORE;
}
function inFlightOn(flights: Flight[], provider: string, l: Layout) {
  return flights.filter((f) => f.phase !== 'done' && f.attempts.some((a) => (a.state === 'trying' || a.state === 'ok') && providerNode(l, a.target) === provider)).length;
}
function leadMode(p: ProviderNode): 'taut' | 'slack' | 'short' {
  return p.health === 'broken' ? 'short' : p.health === 'kedge' ? 'slack' : 'taut';
}

// ---------- client side ----------

function ClientLeads({ g, l, flights, now }: { g: Graph; l: Layout; flights: Flight[]; now: number }) {
  return (
    <g fill="none" strokeWidth="1.5">
      {g.clients.map((c) => <path key={c.id} d={paths.clientToFitting(l, c.id)} stroke="var(--line-2)" />)}
      {flights.map((f) => {
        const failed = f.phase === 'done' && f.status !== null && f.status >= 400;
        return <path key={f.id} d={paths.clientToFitting(l, clientNode(g, f.client))} stroke={failed ? 'var(--err)' : routeTone(f.route)} strokeWidth="2.5" opacity={fadeOf(f, now)} className={f.phase !== 'done' ? 'lead-line' : undefined} />;
      })}
    </g>
  );
}

// ---------- provider leads ----------

function ProviderLeads({ g, l, flights, lastUsed, now, selected, onSelect }: { g: Graph; l: Layout; flights: Flight[]; lastUsed: Record<string, number>; now: number; selected: string | null; onSelect: (id: string) => void }) {
  return (
    <g fill="none" strokeLinecap="round">
      {g.providers.filter((p) => p.id !== MORE).map((p) => {
        const carrying = inFlightOn(flights, p.id, l) > 0 || now - (lastUsed[p.id] ?? -Infinity) < ACTIVE_MS;
        const failures = flights.filter((f) => f.attempts.some((a) => (a.state === 'failed' || a.state === 'cancelled' || a.state === 'skipped') && providerNode(l, a.target) === p.id));
        return (
          <g key={p.id} onClick={() => onSelect(p.id)} style={{ cursor: 'pointer' }}>
            <Lead p={p} l={l} carrying={carrying} kedge={g.kedge} since={lastUsed[p.id] ?? 0} selected={selected === p.id} />
            {/* A failed attempt snaps the lead: red over it while that request is on screen. */}
            {failures.map((f) => <path key={f.id} d={paths.lead(l, p.id, leadMode(p))} stroke="var(--err)" strokeWidth="2.5" strokeDasharray="12 5" opacity={fadeOf(f, now)} />)}
            <path d={paths.lead(l, p.id, leadMode(p))} stroke="transparent" strokeWidth="16" pointerEvents="stroke" />
          </g>
        );
      })}
    </g>
  );
}

function Lead({ p, l, carrying, kedge, since, selected }: { p: ProviderNode; l: Layout; carrying: boolean; kedge: boolean; since: number; selected: boolean }) {
  const ring = selected ? 1 : 0;
  if (p.health === 'broken') {
    // Broken is not idle: red, frayed, ending short of the provider.
    return (
      <g stroke="var(--err)">
        <path d={paths.lead(l, p.id, 'short')} strokeWidth={2 + ring} strokeDasharray="10 4 3 4" />
        <path d={paths.fray(l, p.id)} strokeWidth="1.5" />
        <title>{`${p.name}: ${p.why}`}</title>
      </g>
    );
  }
  if (p.health === 'kedge') return <path d={paths.lead(l, p.id, 'slack')} stroke="var(--ink-2)" strokeWidth={1.5 + ring} strokeDasharray="3 5" opacity="0.6" className="lead-fade" />;
  if (p.health === 'disabled') return <path d={paths.lead(l, p.id)} stroke="var(--line)" strokeWidth={1 + ring} strokeDasharray="2 6" opacity="0.5" />;
  if (p.local) {
    // The local lead is always made fast; in Kedge mode it is the only way out, so it brightens.
    return <path d={paths.lead(l, p.id)} stroke="var(--accent)" strokeWidth={(kedge ? 3.5 : carrying ? 3 : 2) + ring} opacity={kedge || carrying ? 1 : 0.75} className="lead-fade" />;
  }
  if (carrying) {
    // Pays out from the fairlead each time it goes from idle to carrying (the key restarts the draw).
    return <path key={'out' + since} d={paths.lead(l, p.id)} pathLength={1} stroke="var(--accent)" strokeWidth={2.5 + ring} className="lead-payout" />;
  }
  return <path d={paths.lead(l, p.id)} stroke="var(--line)" strokeWidth={1.25 + ring} opacity="0.4" className="lead-fade" />;
}

// ---------- beads (off under reduced motion) ----------

/** Each request's beads run one after another, even when its events arrive faster than a bead can travel. */
const nextFree = new Map<string, number>();

function Beads({ f, g, l }: { f: Flight; g: Graph; l: Layout }) {
  if (f.replayed) return null;
  const legs = f.attempts.filter((a) => a.state !== 'skipped' && a.state !== 'lost');
  const modeOf = (target: string) => {
    const p = g.providers.find((x) => x.id === providerNode(l, target));
    return p ? leadMode(p) : 'taut';
  };
  return (
    <g>
      <Bead slot={f.id} d={paths.clientToFitting(l, clientNode(g, f.client))} ms={500} tone={routeTone(f.route)} />
      {legs.map((a, i) => {
        const d = paths.lead(l, providerNode(l, a.target), modeOf(a.target));
        const tone = routeTone(viaOf(g, f, a));
        const failed = a.state === 'failed' || a.state === 'cancelled';
        return (
          <g key={i}>
            <Bead slot={f.id} d={d} ms={450} tone={tone} hold={a.state !== 'trying' && !failed && f.phase !== 'done'} />
            {failed ? <Bead slot={f.id} d={d} ms={400} reverse tone="var(--err)" /> : null}
          </g>
        );
      })}
    </g>
  );
}

function Bead({ slot, d, ms, reverse = false, hold = false, tone }: { slot: string; d: string; ms: number; reverse?: boolean; hold?: boolean; tone: string }) {
  const path = useRef<SVGPathElement>(null);
  const dot = useRef<SVGCircleElement>(null);
  const [start] = useState(() => {
    const t = Math.max(performance.now(), nextFree.get(slot) ?? 0);
    nextFree.set(slot, t + ms);
    return t;
  });
  useEffect(() => () => { if ((nextFree.get(slot) ?? 0) < performance.now()) nextFree.delete(slot); }, [slot]);
  useEffect(() => {
    const p = path.current, c = dot.current;
    if (!p || !c || !d) return;
    let total = 0;
    try { total = p.getTotalLength(); } catch { return; }
    let raf = 0;
    const step = (t: number) => {
      const k = Math.min(1, Math.max(0, (t - start) / ms));
      const e = k < 0.5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
      const pt = p.getPointAtLength((reverse ? 1 - e : e) * total);
      c.setAttribute('cx', String(pt.x));
      c.setAttribute('cy', String(pt.y));
      c.setAttribute('opacity', t < start || (k >= 1 && !hold) ? '0' : '1');
      if (k < 1) raf = requestAnimationFrame(step);
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }, [d, ms, reverse, hold, start]);
  return (
    <g pointerEvents="none">
      <path ref={path} d={d} fill="none" stroke="none" />
      <circle ref={dot} r="6" fill={tone} stroke="var(--fl-card)" strokeWidth="2" opacity="0" />
    </g>
  );
}

// ---------- nodes ----------

function Nodes({ g, l, flights, lastUsed, now, selected, onSelect }: { g: Graph; l: Layout; flights: Flight[]; lastUsed: Record<string, number>; now: number; selected: string | null; onSelect: (id: string) => void }) {
  const inFlight = flights.filter((f) => f.phase !== 'done');
  const box = { rx: 6, height: NODE_H };
  return (
    <g fontSize="12.5">
      {g.clients.map((c) => {
        const y = l.client.get(c.id) ?? 0;
        const n = inFlight.filter((f) => clientNode(g, f.client) === c.id).length;
        return (
          <g key={c.id}>
            <rect x={COL.client.x} y={y - NODE_H / 2} width={COL.client.w} {...box} fill="var(--fl-card)" stroke={n ? 'var(--accent)' : 'var(--line)'} />
            <text x={COL.client.x + 10} y={y + 4} fill={c.id === NO_KEY || c.id === MORE ? 'var(--ink-2)' : 'var(--ink)'}>{trunc(c.label, 22)}</text>
            {n ? <text x={COL.client.x + COL.client.w - 8} y={y + 4} textAnchor="end" fill="var(--accent)" className="mono">{n}</text> : null}
          </g>
        );
      })}

      <Fitting g={g} l={l} inFlight={inFlight.length} />

      {g.providers.map((p) => {
        const y = l.provider.get(p.id) ?? 0;
        const n = inFlightOn(flights, p.id, l);
        const carrying = n > 0 || now - (lastUsed[p.id] ?? -Infinity) < ACTIVE_MS;
        const Icon = p.local ? LuHardDrive : p.health === 'kedge' ? LuCloudOff : LuCloud;
        const more = p.id === MORE;
        const right = p.why ?? (n ? `${n} in flight` : p.local ? 'ready' : carrying ? 'in use' : 'idle');
        const tone = p.health === 'broken' ? 'var(--err)' : p.health === 'kedge' || p.health === 'disabled' ? 'var(--ink-2)' : n ? 'var(--accent)' : 'var(--ink-2)';
        const open = () => { if (!more) onSelect(p.id); else navigate('providers'); };
        return (
          <g key={p.id} role="button" tabIndex={0} aria-label={`${p.name}${p.local ? ', local' : ', cloud'}: ${right}. Show details.`} style={{ cursor: 'pointer' }} className="map-node"
            onClick={open} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } }}>
            <rect x={COL.provider.x} y={y - NODE_H / 2} width={COL.provider.w} {...box} fill={p.health === 'broken' ? 'var(--err-bg)' : 'var(--fl-card)'}
              stroke={selected === p.id ? 'var(--brass)' : p.health === 'broken' ? 'var(--err)' : n ? 'var(--accent)' : 'var(--line)'} strokeWidth={selected === p.id || n ? 2 : 1}
              strokeDasharray={p.health === 'disabled' || p.health === 'kedge' ? '3 3' : undefined} opacity={p.health === 'disabled' || p.health === 'kedge' ? 0.75 : 1} />
            {!more ? <Icon x={COL.provider.x + 10} y={y - 7} size={14} color="var(--ink-2)" aria-hidden /> : null}
            <text x={COL.provider.x + (more ? 10 : 32)} y={y + 4} fill={p.health === 'kedge' || p.health === 'disabled' || more ? 'var(--ink-2)' : 'var(--ink)'}>
              {trunc(p.name, 20)}{!more && p.name.toLowerCase() !== p.id.toLowerCase() ? <tspan fill="var(--ink-2)" className="mono" fontSize="11.5">{' ' + trunc(p.id, 12)}</tspan> : null}
            </text>
            {!more ? <text x={COL.provider.x + COL.provider.w - 10} y={y + 4} textAnchor="end" fill={tone} fontSize="11.5" fontWeight={p.health === 'broken' ? 600 : undefined}>{right}</text> : null}
          </g>
        );
      })}
    </g>
  );
}

/** The fairlead, with a warning hanging off it for every route that has nowhere to go. */
function Fitting({ g, l, inFlight }: { g: Graph; l: Layout; inFlight: number }) {
  const { x, y } = l.fitting;
  const r = COL.fitting.r;
  return (
    <g>
      <circle cx={x} cy={y} r={r} fill="var(--fl-card)" stroke={g.problems.length ? 'var(--warn)' : 'var(--brass)'} strokeWidth="2" />
      <circle cx={x} cy={y} r="6" fill={inFlight ? 'var(--accent)' : 'var(--brass)'} />
      <text x={x} y={y + r + 17} textAnchor="middle" fill={g.kedge ? 'var(--warn)' : 'var(--ink-2)'} fontSize="12">{g.kedge ? 'Kedge mode · local only' : inFlight ? `${inFlight} in flight` : 'idle'}</text>
      {g.problems.map((p, i) => (
        <g key={p.route ?? i} role="link" tabIndex={0} style={{ cursor: 'pointer' }} className="map-node" onClick={() => navigate('routes')} onKeyDown={(e) => { if (e.key === 'Enter') navigate('routes'); }}>
          <title>{p.text + ' Click to add targets on Routes.'}</title>
          <text x={x} y={y + r + 17 + (i + 1) * WARN_LINE} textAnchor="middle" fill={p.tone === 'err' ? 'var(--err)' : 'var(--warn)'} fontSize="12" fontWeight="600" stroke="var(--fl-card)" strokeWidth="4" paintOrder="stroke">{'⚠ ' + p.short}</text>
        </g>
      ))}
    </g>
  );
}
