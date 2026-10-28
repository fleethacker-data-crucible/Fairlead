import { LuArrowRight, LuCloud, LuHardDrive } from 'react-icons/lu';
import { navigate } from '../app';
import { RoutingMap } from '../components/routing-map';
import { Button, CopyButton, Dot, Empty, Page, Pill, Section, Stat } from '../components/ui';
import { ago, count, ms, pct, usd } from '../lib/format';
import { Config, RequestList, type Status } from '../lib/schemas';
import { useResource } from '../lib/useResource';

export function OverviewPage({ status }: { status: Status | undefined }) {
  const recent = useResource('/api/requests?limit=8', RequestList, { pollMs: 4000 });
  const config = useResource('/api/config', Config, { pollMs: 15000 });
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
      {config.data ? <RoutingMap config={config.data} status={status} /> : <div className="panel h-64" aria-busy="true" />}

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
