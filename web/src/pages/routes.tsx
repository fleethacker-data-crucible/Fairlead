import { useEffect, useMemo, useState } from 'react';
import { LuArrowDown, LuArrowUp, LuPlus, LuRotateCcw, LuTrash2, LuX } from 'react-icons/lu';
import { TargetList, targetOptions } from '../components/targets';
import { Button, Checkbox, ConfirmDialog, Field, Input, Notice, Page, Pill, Section, Select, Tabs, useToast } from '../components/ui';
import { describeError } from '../lib/api';
import { deleteRoute, resetRouting, saveRoute, saveRouting, useConfig } from '../lib/config';
import type { Config, Route, Rule, Strategy } from '../lib/schemas';

const STRATEGIES: Array<{ value: Strategy; label: string; hint: string }> = [
  { value: 'failover', label: 'Failover', hint: 'In the order listed; the next only when one fails.' },
  { value: 'fastest', label: 'Fastest', hint: 'Lowest measured time to first token first.' },
  { value: 'roundRobin', label: 'Round robin', hint: 'Spread requests across the targets in turn.' },
  { value: 'auto', label: 'Auto (rules)', hint: 'Pick another route per request with the routing rules.' },
];

export function RoutesPage() {
  const config = useConfig();
  const [tab, setTab] = useState<'routes' | 'rules' | 'aliases'>('routes');
  return (
    <Page title="Routes" lede="Routes are the model names your tools use. Rules decide what auto picks; aliases catch model names tools hard-code.">
      {config.error ? <Notice tone="err">{config.error}</Notice> : null}
      <section className="panel">
        <Tabs value={tab} onChange={setTab} items={[
          { value: 'routes', label: 'Routes', count: config.data?.routes.length },
          { value: 'rules', label: 'Auto rules', count: config.data?.rules.length },
          { value: 'aliases', label: 'Aliases', count: config.data ? Object.keys(config.data.aliases).length : undefined },
        ]} />
        {config.data ? (tab === 'routes' ? <RoutesTab config={config.data} /> : tab === 'rules' ? <RulesTab config={config.data} /> : <AliasesTab config={config.data} />) : <div className="h-40" />}
      </section>
    </Page>
  );
}

function RoutesTab({ config }: { config: Config }) {
  const [selected, setSelected] = useState(config.routes[0]?.id ?? '');
  const [creating, setCreating] = useState(false);
  const route = creating ? undefined : config.routes.find((r) => r.id === selected) ?? config.routes[0];
  return (
    <div className="grid gap-0 lg:grid-cols-[240px_minmax(0,1fr)]">
      <div className="border-r border-line-2">
        <ul>
          {config.routes.map((r) => (
            <li key={r.id}>
              <button type="button" onClick={() => { setCreating(false); setSelected(r.id); }} aria-current={!creating && route?.id === r.id ? 'true' : undefined}
                className={'flex w-full items-center justify-between gap-2 px-4 py-2 text-left hover:bg-surface-2 ' + (!creating && route?.id === r.id ? 'bg-surface-2' : '')}>
                <span className="mono font-medium">{r.id}</span>
                <span className="text-[12px] text-ink-3">{r.strategy === 'auto' ? 'rules' : `${r.targets.length} target${r.targets.length === 1 ? '' : 's'}`}</span>
              </button>
            </li>
          ))}
        </ul>
        <div className="hairline p-3"><Button size="sm" onClick={() => setCreating(true)}><LuPlus size={14} />New route</Button></div>
      </div>
      <RouteEditor key={creating ? '__new' : route?.id} route={route} config={config} onSaved={(id) => { setCreating(false); setSelected(id); }} />
    </div>
  );
}

function RouteEditor({ route, config, onSaved }: { route: Route | undefined; config: Config; onSaved: (id: string) => void }) {
  const toast = useToast();
  const blank: Route = { id: '', description: '', strategy: 'failover', targets: [], hedgeAfterMs: 0, localFallback: true };
  const [draft, setDraft] = useState<Route>(route ?? blank);
  const [confirm, setConfirm] = useState(false);
  useEffect(() => setDraft(route ?? blank), [route]); // eslint-disable-line react-hooks/exhaustive-deps
  const options = useMemo(() => targetOptions(config), [config]);
  const dirty = JSON.stringify(draft) !== JSON.stringify(route ?? blank);
  const save = async () => {
    try { await saveRoute(draft, route?.id); toast.push('ok', `Route ${draft.id} saved.`); onSaved(draft.id); } catch (e) { toast.push('err', describeError(e)); }
  };
  const used = route ? [config.defaultRoute === route.id && 'the default route', config.autoFallbackRoute === route.id && 'the auto fallback', config.rules.some((r) => r.route === route.id) && 'auto rules'].filter(Boolean) : [];
  return (
    <div className="flex flex-col gap-4 px-5 py-4">
      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Name (what tools send as the model)" hint="Letters, digits, dot, dash, underscore."><Input className="mono" value={draft.id} onChange={(e) => setDraft({ ...draft, id: e.target.value.trim() })} placeholder="deep-local" /></Field>
        <Field label="Strategy" hint={STRATEGIES.find((s) => s.value === draft.strategy)?.hint}>
          <Select value={draft.strategy} onChange={(e) => setDraft({ ...draft, strategy: e.target.value as Strategy })}>
            {STRATEGIES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
          </Select>
        </Field>
        <Field label="Description" className="md:col-span-2"><Input value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} /></Field>
      </div>
      {draft.strategy === 'auto' ? (
        <Notice tone="info">This route has no targets of its own: each request goes to the route chosen by the <b>Auto rules</b> tab (first match wins), or to <code>{config.autoFallbackRoute}</code> when nothing matches.</Notice>
      ) : (
        <>
          <div className="flex flex-col gap-1.5"><span className="text-ink-2">Targets, in order</span><TargetList value={draft.targets} options={options} localOnly={draft.id === 'local'} onChange={(targets) => setDraft({ ...draft, targets })} /></div>
          <div className="grid gap-3 md:grid-cols-2">
            <Field label="Hedge after (ms)" hint="If the first target has not started answering by then, race the next one and keep whichever answers first. 0 = off.">
              <Input type="number" min={0} max={120000} step={100} value={draft.hedgeAfterMs} onChange={(e) => setDraft({ ...draft, hedgeAfterMs: Number(e.target.value) })} />
            </Field>
            <div className="flex items-end pb-2">
              <Checkbox label="Fall back to the local route when every target is down" checked={draft.localFallback} disabled={draft.id === 'local'} onChange={(e) => setDraft({ ...draft, localFallback: e.target.checked })} />
            </div>
          </div>
        </>
      )}
      {used.length ? <div className="text-[12.5px] text-ink-3">Used as {used.join(', ')}.</div> : null}
      <div className="flex justify-between gap-2">
        {route && route.id !== 'local' ? <Button variant="danger" onClick={() => setConfirm(true)}><LuTrash2 size={14} />Delete route</Button> : <span />}
        <span className="flex gap-2">
          <Button variant="quiet" disabled={!dirty} onClick={() => setDraft(route ?? blank)}>Discard</Button>
          <Button variant="primary" disabled={!dirty || !draft.id} onClick={() => void save()}>{route ? 'Save route' : 'Create route'}</Button>
        </span>
      </div>
      <ConfirmDialog open={confirm} danger title={`Delete route ${route?.id}?`} confirmLabel="Delete" onCancel={() => setConfirm(false)}
        onConfirm={() => { setConfirm(false); void deleteRoute(route!.id).then(() => onSaved(config.routes[0]?.id ?? ''), (e) => toast.push('err', describeError(e))); }}>
        Tools that ask for <code>{route?.id}</code> will land on the default route instead. Rules and aliases pointing at it must be changed first.
      </ConfirmDialog>
    </div>
  );
}

function RulesTab({ config }: { config: Config }) {
  const toast = useToast();
  const [rules, setRules] = useState<Rule[]>(config.rules);
  const [fallback, setFallback] = useState(config.autoFallbackRoute);
  useEffect(() => { setRules(config.rules); setFallback(config.autoFallbackRoute); }, [config]);
  const targets = config.routes.filter((r) => r.strategy !== 'auto').map((r) => r.id);
  const dirty = JSON.stringify(rules) !== JSON.stringify(config.rules) || fallback !== config.autoFallbackRoute;
  const update = (i: number, patch: Partial<Rule>) => setRules(rules.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const cond = (i: number, patch: Partial<Rule['when']>) => update(i, { when: { ...rules[i]!.when, ...patch } });
  const move = (i: number, d: number) => { const next = [...rules]; const [x] = next.splice(i, 1); next.splice(i + d, 0, x!); setRules(next); };
  const tri = (v: boolean | null | undefined) => (v === true ? 'yes' : v === false ? 'no' : '');
  const fromTri = (v: string) => (v === 'yes' ? true : v === 'no' ? false : null);
  const num = (v: string) => (v.trim() === '' ? null : Number(v));
  return (
    <div className="flex flex-col gap-3 px-5 py-4">
      <p className="text-ink-2">The <code>auto</code> route checks these top to bottom; the first rule whose conditions all hold picks the route. Token counts are estimates of the prompt. Empty fields are ignored.</p>
      <ol className="flex flex-col gap-2">
        {rules.map((r, i) => (
          <li key={i} className="rounded-panel border border-line-2 bg-surface p-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="num w-5 text-ink-3">{i + 1}</span>
              <Input className="w-48 font-medium" value={r.name} onChange={(e) => update(i, { name: e.target.value })} aria-label="Rule name" />
              <span className="text-ink-3">→</span>
              <Select className="w-36" value={r.route} onChange={(e) => update(i, { route: e.target.value })} aria-label="Route">{targets.map((t) => <option key={t} value={t}>{t}</option>)}</Select>
              <Checkbox label="On" checked={r.enabled} onChange={(e) => update(i, { enabled: e.target.checked })} />
              <span className="ml-auto flex gap-1">
                <Button size="sm" variant="quiet" aria-label="Move up" disabled={i === 0} onClick={() => move(i, -1)}><LuArrowUp size={14} /></Button>
                <Button size="sm" variant="quiet" aria-label="Move down" disabled={i === rules.length - 1} onClick={() => move(i, 1)}><LuArrowDown size={14} /></Button>
                <Button size="sm" variant="quiet" aria-label="Delete rule" onClick={() => setRules(rules.filter((_, j) => j !== i))}><LuX size={14} /></Button>
              </span>
            </div>
            <div className="mt-2 grid gap-2 md:grid-cols-3 xl:grid-cols-6">
              <Field label="Private"><Select value={tri(r.when.sensitive)} onChange={(e) => cond(i, { sensitive: fromTri(e.target.value) })}><option value="">any</option><option value="yes">yes</option><option value="no">no</option></Select></Field>
              <Field label="Uses tools"><Select value={tri(r.when.hasTools)} onChange={(e) => cond(i, { hasTools: fromTri(e.target.value) })}><option value="">any</option><option value="yes">yes</option><option value="no">no</option></Select></Field>
              <Field label="Has images"><Select value={tri(r.when.hasImages)} onChange={(e) => cond(i, { hasImages: fromTri(e.target.value) })}><option value="">any</option><option value="yes">yes</option><option value="no">no</option></Select></Field>
              <Field label="Tokens at least"><Input type="number" min={0} value={r.when.minTokens ?? ''} onChange={(e) => cond(i, { minTokens: num(e.target.value) })} /></Field>
              <Field label="Tokens at most"><Input type="number" min={0} value={r.when.maxTokens ?? ''} onChange={(e) => cond(i, { maxTokens: num(e.target.value) })} /></Field>
              <Field label="Client key name"><Input value={r.when.client ?? ''} onChange={(e) => cond(i, { client: e.target.value || null })} /></Field>
              <Field label="Any of these words in the latest message (comma separated)" className="md:col-span-3 xl:col-span-6">
                <Input value={(r.when.keywords ?? []).join(', ')} onChange={(e) => cond(i, { keywords: e.target.value ? e.target.value.split(',').map((k) => k.trim()).filter(Boolean) : null })} />
              </Field>
            </div>
          </li>
        ))}
      </ol>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <span className="flex items-end gap-2">
          <Button size="sm" onClick={() => setRules([...rules, { name: 'new-rule', route: targets[0] ?? 'smart', enabled: true, when: {} }])}><LuPlus size={14} />Add rule</Button>
          <Field label="When no rule matches, use" className="w-48"><Select value={fallback} onChange={(e) => setFallback(e.target.value)}>{targets.map((t) => <option key={t} value={t}>{t}</option>)}</Select></Field>
        </span>
        <span className="flex gap-2">
          <Button variant="quiet" onClick={() => void resetRouting().then(() => toast.push('ok', 'Rules and aliases reset to the defaults.'), (e) => toast.push('err', describeError(e)))}><LuRotateCcw size={14} />Defaults</Button>
          <Button variant="quiet" disabled={!dirty} onClick={() => { setRules(config.rules); setFallback(config.autoFallbackRoute); }}>Discard</Button>
          <Button variant="primary" disabled={!dirty} onClick={() => void saveRouting({ rules, autoFallbackRoute: fallback }).then(() => toast.push('ok', 'Rules saved.'), (e) => toast.push('err', describeError(e)))}>Save rules</Button>
        </span>
      </div>
    </div>
  );
}

function AliasesTab({ config }: { config: Config }) {
  const toast = useToast();
  const [rows, setRows] = useState<Array<[string, string]>>(Object.entries(config.aliases));
  const [def, setDef] = useState(config.defaultRoute);
  useEffect(() => { setRows(Object.entries(config.aliases)); setDef(config.defaultRoute); }, [config]);
  const routes = config.routes.map((r) => r.id);
  const dirty = JSON.stringify(rows) !== JSON.stringify(Object.entries(config.aliases)) || def !== config.defaultRoute;
  const save = () => {
    const aliases: Record<string, string> = {};
    for (const [k, v] of rows) if (k.trim()) aliases[k.trim()] = v;
    void saveRouting({ aliases, defaultRoute: def }).then(() => toast.push('ok', 'Aliases saved.'), (e) => toast.push('err', describeError(e)));
  };
  return (
    <div className="flex flex-col gap-3 px-5 py-4">
      <p className="text-ink-2">Tools often hard-code model names (<code>claude-sonnet-…</code>, <code>gpt-…</code>). A pattern here sends them to a route. <code>*</code> matches anything, <code>?</code> one character, <code>[0-9]</code> a digit. An exact name wins; otherwise the first matching pattern from the top.</p>
      <table className="table-dense w-full max-w-[720px]">
        <thead><tr><th>Model name pattern</th><th>Route</th><th /></tr></thead>
        <tbody>
          {rows.map(([k, v], i) => (
            <tr key={i}>
              <td><Input className="mono" value={k} onChange={(e) => setRows(rows.map((r, j) => (j === i ? [e.target.value, r[1]] : r)))} aria-label="Pattern" /></td>
              <td><Select value={v} onChange={(e) => setRows(rows.map((r, j) => (j === i ? [r[0], e.target.value] : r)))} aria-label="Route">{routes.map((t) => <option key={t} value={t}>{t}</option>)}</Select></td>
              <td><Button size="sm" variant="quiet" aria-label="Remove alias" onClick={() => setRows(rows.filter((_, j) => j !== i))}><LuX size={14} /></Button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <span className="flex items-end gap-2">
          <Button size="sm" onClick={() => setRows([...rows, ['', routes[0] ?? 'auto']])}><LuPlus size={14} />Add alias</Button>
          <Field label="Any other model name goes to" className="w-48"><Select value={def} onChange={(e) => setDef(e.target.value)}>{routes.map((t) => <option key={t} value={t}>{t}</option>)}</Select></Field>
          <Pill>provider/model names always go straight to that model</Pill>
        </span>
        <span className="flex gap-2">
          <Button variant="quiet" disabled={!dirty} onClick={() => { setRows(Object.entries(config.aliases)); setDef(config.defaultRoute); }}>Discard</Button>
          <Button variant="primary" disabled={!dirty} onClick={save}>Save aliases</Button>
        </span>
      </div>
    </div>
  );
}
