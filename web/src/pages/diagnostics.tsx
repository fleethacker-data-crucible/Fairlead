import { useState } from 'react';
import { LuRefreshCw, LuRotateCcw } from 'react-icons/lu';
import { Button, Dot, Empty, Page, Pill, Section, Select, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { ago, clock, ms } from '../lib/format';
import { LogEntries, ProviderHealth } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';

export function DiagnosticsPage() {
  const health = useResource('/api/health/providers', ProviderHealth, { pollMs: 5000 });
  const [level, setLevel] = useState('');
  const logs = useResource(`/api/logs${level ? `?level=${level}` : ''}`, LogEntries, { pollMs: 5000 });
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const probe = async () => {
    setBusy(true);
    try { await post('/api/probe'); invalidate('/api/health/providers'); invalidate('/api/status'); } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(false); }
  };
  const reset = async (provider?: string) => {
    try { await post('/api/breakers/reset', { provider: provider ?? null }); invalidate('/api/health/providers'); invalidate('/api/status'); toast.push('ok', 'Breaker reset; the next request will try it again.'); } catch (e) { toast.push('err', describeError(e)); }
  };
  const h = health.data;
  return (
    <Page title="Diagnostics" lede="What the health monitor sees: the network, each provider and the circuit breakers that keep dead targets out of the way." actions={<Button busy={busy} onClick={() => void probe()}><LuRefreshCw size={14} />Check now</Button>}>
      {h ? (
        <Section title="Network">
          <div className="grid gap-x-6 gap-y-1 px-4 py-3 text-ink-2 md:grid-cols-2">
            <span>Internet: <b className="text-ink">{h.connectivity.internet}</b> {h.connectivity.osReportsNetwork ? '' : '(Windows reports no network)'}</span>
            <span>Kedge mode: <b className="text-ink">{h.connectivity.kedge ? `on (${h.connectivity.kedgeReason})` : 'off'}</b>, setting {h.connectivity.mode}</span>
            <span>Last probe: {ago(h.connectivity.lastProbe)}</span>
            <span>Answered: {h.connectivity.reached.length ? h.connectivity.reached.join(', ') : 'none'}</span>
          </div>
        </Section>
      ) : null}
      <Section title="Providers">
        {!h || h.providers.length === 0 ? <Empty title="No probes yet" /> : (
          <table className="table-dense w-full">
            <thead><tr><th>Provider</th><th>Reachable</th><th>Key</th><th className="num">Probe</th><th>Models</th><th>In memory</th><th>Checked</th><th>Last error</th></tr></thead>
            <tbody>
              {h.providers.map((p) => (
                <tr key={p.id}>
                  <td className="mono">{p.id} <span className="text-ink-3">{p.local ? 'local' : 'cloud'}</span></td>
                  <td>{p.reachable === true ? <Dot tone="ok">yes</Dot> : p.reachable === false ? <Dot tone="err">no</Dot> : <Dot tone="neutral">unknown</Dot>}</td>
                  <td>{p.authOk === true ? <Dot tone="ok">accepted</Dot> : p.authOk === false ? <Dot tone="err">rejected</Dot> : <span className="text-ink-3">—</span>}</td>
                  <td className="num">{ms(p.probeMs)}</td>
                  <td>{p.models.length}</td>
                  <td className="mono text-[12px]">{p.loaded.length ? p.loaded.join(', ') : '—'}</td>
                  <td className="text-ink-2">{ago(p.lastChecked)}</td>
                  <td className="text-err">{p.lastError ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Section>
      <Section title="Circuit breakers" aside={<Button size="sm" variant="quiet" onClick={() => void reset()}><LuRotateCcw size={14} />Reset all</Button>}>
        {!h || h.breakers.length === 0 ? <Empty title="No breakers yet">A breaker appears the first time a target is called.</Empty> : (
          <table className="table-dense w-full">
            <thead><tr><th>Target</th><th>State</th><th>Until</th><th className="num">Failures</th><th>Last error</th><th /></tr></thead>
            <tbody>
              {h.breakers.map((b) => (
                <tr key={b.key}>
                  <td className="mono">{b.key}</td>
                  <td>{b.state === 'closed' ? <Dot tone="ok">closed</Dot> : b.state === 'open' ? <Dot tone="err">open</Dot> : <Dot tone="warn">half-open</Dot>}</td>
                  <td className="text-ink-2">{b.openUntil && b.state !== 'closed' ? clock(b.openUntil) : '—'}</td>
                  <td className="num">{b.failures}</td>
                  <td className="text-ink-2">{b.lastFailure ? <Pill className="mr-1">{b.lastFailure}</Pill> : null}{b.lastError ?? ''}</td>
                  <td>{b.state !== 'closed' ? <Button size="sm" variant="quiet" onClick={() => void reset(b.key.split('/')[0])}>Reset</Button> : null}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Section>
      <Section title="Log" aside={<Select value={level} onChange={(e) => setLevel(e.target.value)} aria-label="Level" className="w-32"><option value="">All levels</option><option value="WARN">Warnings</option><option value="ERROR">Errors</option></Select>}>
        <div className="scroll-y max-h-[420px]">
          <table className="table-dense w-full text-[12.5px]">
            <tbody>
              {(logs.data ?? []).map((l, i) => (
                <tr key={i}><td className="mono text-ink-2">{clock(l.time)}</td><td>{l.level === 'ERROR' ? <Dot tone="err">error</Dot> : l.level === 'WARN' ? <Dot tone="warn">warn</Dot> : <span className="text-ink-3">info</span>}</td><td className="mono">{l.event}</td><td className="text-ink-2">{l.message}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>
    </Page>
  );
}
