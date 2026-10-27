// Side panel for one provider, opened by clicking its lead on the routing map: what it is, how healthy it is, which
// routes use it, and the quick fixes (test, reset breaker, enable/disable) without leaving the Overview.
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { LuCloud, LuExternalLink, LuHardDrive, LuX } from 'react-icons/lu';
import { describeError, post } from '../lib/api';
import { saveProvider, testProvider, type TestResult } from '../lib/config';
import { ms } from '../lib/format';
import { providerOf, type ProviderNode } from '../lib/routing-map';
import type { Config, Status } from '../lib/schemas';
import { invalidate } from '../lib/useResource';
import { Button, Dot, Notice, Pill, useToast } from './ui';

export function ProviderPanel({ node, config, status, onClose }: { node: ProviderNode; config: Config; status: Status; onClose: () => void }) {
  const toast = useToast();
  const p = config.providers.find((x) => x.id === node.id);
  const h = status.providers.find((x) => x.id === node.id);
  const [busy, setBusy] = useState<string | null>(null);
  const [test, setTest] = useState<TestResult | null>(null);
  const close = useRef<HTMLButtonElement>(null);

  useEffect(() => { setTest(null); close.current?.focus(); }, [node.id]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const run = async (what: string, f: () => Promise<unknown>) => {
    setBusy(what);
    try { await f(); } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(null); }
  };
  const uses = config.routes.flatMap((r) => r.targets.filter((t) => providerOf(t).provider === node.id).map((t) => ({ route: r.id, model: providerOf(t).model })));
  const models = p ? [...new Set([...p.models.map((m) => m.id), ...p.discovered.map((m) => m.id)])] : [];

  return (
    <aside className="absolute inset-y-0 right-0 z-10 flex w-[380px] max-w-full flex-col border-l border-line-2 bg-[var(--fl-popover)] shadow-lg" aria-label={`${node.name} details`}>
      <header className="flex items-center justify-between gap-2 border-b border-line-2 px-4 py-3">
        <span className="flex min-w-0 items-center gap-2">
          {node.local ? <LuHardDrive size={16} className="text-ink-2" aria-hidden /> : <LuCloud size={16} className="text-ink-2" aria-hidden />}
          <span className="truncate font-semibold">{node.name}</span>
          <span className="mono truncate text-[12px] text-ink-2">{node.id}</span>
        </span>
        <Button ref={close} size="sm" variant="quiet" aria-label="Close details" onClick={onClose}><LuX size={16} /></Button>
      </header>

      <div className="scroll-y flex flex-1 flex-col gap-4 px-4 py-3 text-[13px]">
        <div><Health node={node} /></div>

        {!p ? (
          <Notice tone="err">No provider '{node.id}' is configured. A route or a request names it; add it on Providers or fix the route.</Notice>
        ) : (
          <dl className="grid grid-cols-[110px_1fr] gap-x-3 gap-y-1.5">
            <Row k="Kind">{p.kind}{p.managed ? <Pill className="ml-1">from AI gateway file</Pill> : null}</Row>
            <Row k="Where">{p.local ? 'This machine' : 'Cloud'}</Row>
            <Row k="Base URL"><span className="mono break-all">{p.baseUrl}</span></Row>
            <Row k="Enabled">{p.enabled ? 'Yes' : 'No'}</Row>
            <Row k="Timeout">{p.timeoutSeconds} s</Row>
            <Row k="Key">{p.local && p.keySource === 'none' ? 'Not needed' : p.keySource === 'none' ? <span className="text-err">None</span> : <>{p.keySource}{p.keyHint ? <span className="mono text-ink-2"> {p.keyHint}</span> : null}</>}</Row>
            <Row k="Probe">{h?.reachable === false ? <span className="text-err">unreachable</span> : ms(h?.probeMs)}</Row>
            <Row k="Breaker">{h?.breaker === 'open' ? <span className="text-err">open (paused after failures)</span> : h?.breaker ?? '—'}</Row>
            {h?.lastError ? <Row k="Last error"><span className="text-err break-words">{h.lastError}</span></Row> : null}
          </dl>
        )}

        <section className="flex flex-col gap-1">
          <h3 className="text-[13px] font-semibold">Used by</h3>
          {uses.length === 0 ? (
            <span className="text-warn">No route lists a model from this provider, so it only gets requests that name it directly{node.local ? ' or fall back to a model found on this machine' : ''}.</span>
          ) : (
            <ul className="flex flex-col gap-0.5">
              {uses.map((u) => <li key={u.route + u.model}><span className="mono">{u.route}</span> <span className="text-ink-2">→</span> <span className="mono">{u.model}</span></li>)}
            </ul>
          )}
        </section>

        {models.length ? (
          <section className="flex flex-col gap-1">
            <h3 className="text-[13px] font-semibold">Models ({models.length})</h3>
            <div className="flex flex-wrap gap-1">
              {models.slice(0, 12).map((m) => <Pill key={m} className="mono">{m}</Pill>)}
              {models.length > 12 ? <Pill>+{models.length - 12} more</Pill> : null}
            </div>
          </section>
        ) : null}

        {test ? <Notice tone={test.ok ? 'ok' : 'err'}>{test.ok ? `Connected in ${ms(test.ms)}: ${test.models?.length ?? 0} models.` : `${test.error} (${test.kind})`}</Notice> : null}
      </div>

      <footer className="flex flex-wrap gap-2 border-t border-line-2 px-4 py-3">
        {p ? <Button size="sm" busy={busy === 'test'} onClick={() => run('test', async () => { setTest(await testProvider(p.id)); invalidate('/api/status'); })}>Test connection</Button> : null}
        {p && h?.breaker && h.breaker !== 'closed' ? (
          <Button size="sm" busy={busy === 'reset'} onClick={() => run('reset', async () => { await post('/api/breakers/reset', { provider: p.id }); invalidate('/api/status'); toast.push('ok', `${p.name} will be tried again.`); })}>Reset breaker</Button>
        ) : null}
        {p && !p.managed ? (
          <Button size="sm" busy={busy === 'enable'} onClick={() => run('enable', async () => {
            await saveProvider({ id: p.id, name: p.name, kind: p.kind, baseUrl: p.baseUrl, local: p.local, enabled: !p.enabled, apiKeyEnv: p.apiKeyEnv ?? '', timeoutSeconds: p.timeoutSeconds, streamUsage: p.streamUsage, models: p.models });
            toast.push('ok', `${p.name} ${p.enabled ? 'disabled' : 'enabled'}.`);
          })}>{p.enabled ? 'Disable' : 'Enable'}</Button>
        ) : null}
        <Button size="sm" variant="quiet" onClick={() => { window.location.hash = `/providers?id=${encodeURIComponent(node.id)}`; }}>Open in Providers<LuExternalLink size={14} /></Button>
      </footer>
    </aside>
  );
}

function Row({ k, children }: { k: string; children: ReactNode }) {
  return <><dt className="text-ink-2">{k}</dt><dd className="min-w-0">{children}</dd></>;
}

function Health({ node }: { node: ProviderNode }) {
  switch (node.health) {
    case 'broken': return <Dot tone="err">Broken: {node.why}</Dot>;
    case 'kedge': return <Dot tone="warn">Parked: Kedge mode, cloud is out of reach</Dot>;
    case 'disabled': return <Dot tone="neutral">Disabled</Dot>;
    case 'ok': return <Dot tone="ok">Ready</Dot>;
    default: return <Dot tone="neutral">Not checked yet</Dot>;
  }
}
