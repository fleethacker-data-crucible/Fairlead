import { LuArrowRight, LuCloud, LuHardDrive } from 'react-icons/lu';
import { navigate } from '../app';
import { Button, CopyButton, Dot, Empty, Page, Pill, Section, Stat, cn } from '../components/ui';
import { ago, count, ms, pct, usd } from '../lib/format';
import { RequestList, type Status } from '../lib/schemas';
import { useResource } from '../lib/useResource';

export function OverviewPage({ status }: { status: Status | undefined }) {
  const recent = useResource('/api/requests?limit=8', RequestList, { pollMs: 4000 });
  if (!status) return <Page title="Overview"><div className="panel h-40" /></Page>;
  const m = status.metrics;
  const local = status.providers.filter((p) => p.local);
  const cloud = status.providers.filter((p) => !p.local);
  return (
    <Page
      title="Overview"
      lede="One address for every tool. Fairlead picks a model per request and keeps answering from local models when the cloud is out of reach."
      actions={<><CopyButton text={status.gatewayUrl} label="Copy gateway URL" /><Button variant="primary" onClick={() => navigate('clients')}>Connect a tool<LuArrowRight size={14} /></Button></>}
    >
      <LeadDiagram status={status} />

      <Section title="Last hour">
        <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-6">
          <Stat label="Requests" value={count(m.lastHour.requests)} hint={`${count(m.total)} since start`} />
          <Stat label="Failed" value={count(m.lastHour.failed)} hint={m.lastHour.requests ? pct(m.lastHour.failed, m.lastHour.requests) + ' of requests' : 'no traffic yet'} />
          <Stat label="Median time" value={ms(m.lastHour.p50Ms)} hint={`p95 ${ms(m.lastHour.p95Ms)}`} />
          <Stat label="First token" value={ms(m.lastHour.p50FirstByteMs)} hint="median, streaming" />
          <Stat label="Tokens" value={count(m.lastHour.inputTokens + m.lastHour.outputTokens)} hint={`${count(m.lastHour.inputTokens)} in · ${count(m.lastHour.outputTokens)} out`} />
          <Stat label="Cost" value={usd(m.lastHour.cost)} hint={m.lastHour.cost === null || m.lastHour.cost === undefined ? 'enter model prices on Providers' : 'from your model prices'} />
        </div>
        <div className="hairline grid grid-cols-2 md:grid-cols-4">
          <Stat label="Failovers" value={count(m.failovers)} hint="answered by a later target" />
          <Stat label="Served locally" value={count(m.servedLocally)} hint={pct(m.servedLocally, m.total - m.failed)} />
          <Stat label="In Kedge mode" value={count(m.kedge)} hint="while the cloud was out of reach" />
          <Stat label="Kept private" value={count(m.sensitive)} hint="secrets detected or private header" />
        </div>
      </Section>

      <div className="grid gap-5 xl:grid-cols-2">
        <Section title="Providers" aside={<Button size="sm" variant="quiet" onClick={() => navigate('providers')}>Manage</Button>}>
          {status.providers.length === 0 ? (
            <Empty title="No providers yet" action={<Button variant="primary" onClick={() => navigate('setup')}>Run setup</Button>} />
          ) : (
            <table className="table-dense w-full">
              <thead><tr><th>Provider</th><th>State</th><th className="num">Models</th><th className="num">Probe</th></tr></thead>
              <tbody>
                {[...local, ...cloud].map((p) => (
                  <tr key={p.id}>
                    <td><span className="inline-flex items-center gap-2">{p.local ? <LuHardDrive size={14} className="text-ink-3" /> : <LuCloud size={14} className="text-ink-3" />}{p.name}<span className="mono text-ink-3">{p.id}</span></span></td>
                    <td><ProviderState p={p} /></td>
                    <td className="num">{p.models}</td>
                    <td className="num">{ms(p.probeMs)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Section>

        <Section title="Latest requests" aside={<Button size="sm" variant="quiet" onClick={() => navigate('activity')}>All activity</Button>}>
          {!recent.data || recent.data.length === 0 ? (
            <Empty title="No traffic yet">Point a tool at <code>{status.gatewayUrl}</code> or try the Playground.</Empty>
          ) : (
            <table className="table-dense w-full">
              <thead><tr><th>When</th><th>Asked for</th><th>Served by</th><th className="num">Time</th></tr></thead>
              <tbody>
                {recent.data.map((r) => (
                  <tr key={r.id}>
                    <td className="text-ink-2">{ago(r.time)}</td>
                    <td><span className="mono">{r.requestedModel || '—'}</span>{r.route ? <span className="text-ink-3"> → {r.route}</span> : null}</td>
                    <td>{r.status >= 400 ? <Dot tone="err">{r.status} {r.error?.slice(0, 40)}</Dot> : <span className="mono">{r.target}</span>}{r.failover ? <Pill tone="warn" className="ml-1">failover</Pill> : null}{r.kedge ? <Pill tone="warn" className="ml-1">kedge</Pill> : null}</td>
                    <td className="num">{ms(r.totalMs)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Section>
      </div>
    </Page>
  );
}

export function ProviderState({ p }: { p: Status['providers'][number] }) {
  if (!p.enabled) return <Dot tone="neutral">Disabled</Dot>;
  if (p.breaker === 'open') return <Dot tone="err">Paused after failures</Dot>;
  if (p.authOk === false) return <Dot tone="err">Key rejected</Dot>;
  if (!p.local && p.keySource === 'none') return <Dot tone="warn">No key</Dot>;
  if (p.reachable === false) return <Dot tone="err">{p.local ? 'Not running' : 'Unreachable'}</Dot>;
  if (p.reachable === true) return <Dot tone="ok">Ready</Dot>;
  return <Dot tone="neutral">Checking</Dot>;
}

/** Tools → Fairlead → cloud and local. The cloud lines go dark in Kedge mode; that is the whole idea in one picture. */
function LeadDiagram({ status }: { status: Status }) {
  const c = status.connectivity;
  const cloudLive = !c.kedge && c.internet !== 'down';
  const localReady = status.providers.some((p) => p.local && p.reachable);
  return (
    <section className="panel overflow-hidden" aria-label="Where traffic goes">
      <div className="grid items-center gap-4 px-6 py-5 md:grid-cols-[1fr_auto_1fr]">
        <div className="flex flex-col gap-1">
          <span className="text-[12px] text-ink-3">Your tools</span>
          <span className="text-ink">Claude Code, SDKs, agents, Open WebUI…</span>
          <span className="mono text-[12px] text-ink-2">{status.gatewayUrl}</span>
        </div>
        <svg width="220" height="96" viewBox="0 0 220 96" role="img" aria-label={cloudLive ? 'Traffic goes to cloud and local models' : 'Traffic goes to local models only'}>
          <line x1="0" y1="48" x2="94" y2="48" stroke="var(--accent)" strokeWidth="2" className="lead-line" />
          <circle cx="110" cy="48" r="16" fill="none" stroke="var(--brass)" strokeWidth="2" />
          <circle cx="110" cy="48" r="5" fill="var(--accent)" />
          <path d="M126 44 C160 44 170 18 220 18" fill="none" stroke={cloudLive ? 'var(--accent)' : 'var(--line-2)'} strokeWidth="2" className={cn(cloudLive && 'lead-line')} strokeDasharray={cloudLive ? undefined : '2 6'} />
          <path d="M126 52 C160 52 170 78 220 78" fill="none" stroke={localReady ? 'var(--accent)' : 'var(--line-2)'} strokeWidth="2" className={cn(localReady && 'lead-line')} />
        </svg>
        <div className="flex flex-col gap-3">
          <div className={cn('flex items-center gap-2', !cloudLive && 'text-ink-3')}><LuCloud size={16} />Cloud {cloudLive ? <Pill tone="ok">in use</Pill> : <Pill tone="warn">{c.kedge ? 'Kedge mode' : 'unreachable'}</Pill>}</div>
          <div className={cn('flex items-center gap-2', !localReady && 'text-ink-3')}><LuHardDrive size={16} />Local {localReady ? <Pill tone="ok">ready</Pill> : <Pill tone="err">no local server</Pill>}</div>
        </div>
      </div>
      {!localReady ? <div className="hairline px-6 py-2 text-[12.5px] text-warn">No local model server is reachable, so there is nothing to fall back to when the cloud drops. Start Ollama (or add one on Providers).</div> : null}
    </section>
  );
}
