import { useMemo, useState, type Dispatch, type SetStateAction } from 'react';
import { LuArrowLeft, LuArrowRight, LuCheck, LuCloud, LuExternalLink, LuHardDrive, LuRefreshCw } from 'react-icons/lu';
import { navigate } from '../app';
import { TargetList, targetOptions } from '../components/targets';
import { Button, CopyButton, Dot, Emblem, Field, Input, Notice, Page, Pill, Section, cn, useToast } from '../components/ui';
import { describeError } from '../lib/api';
import { createClient, discoverProvider, saveProvider, saveRoute, saveSettings, testProvider, useConfig } from '../lib/config';
import { Detected, Presets, type Config, type Status } from '../lib/schemas';
import { snippets } from '../lib/snippets';
import { invalidate, useResource } from '../lib/useResource';

const STEPS = ['Local models', 'Cloud providers', 'Routes', 'Connect tools'] as const;
type RouteDrafts = Record<string, string[]>;

/**
 * The one-time browser setup: find local servers, add cloud keys, fill the routes, hand out the first key.
 * Everything here can be changed later on the other pages; this is just the guided path.
 */
export function SetupPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const [step, setStep] = useState(0);
  // The wizard owns edits so leaving the Routes step cannot discard them.
  const [routeDrafts, setRouteDrafts] = useState<RouteDrafts>({});
  const [savingRoutes, setSavingRoutes] = useState(false);
  const routesDirty = Object.keys(routeDrafts).length > 0;
  const config = useConfig();
  const toast = useToast();
  const cloudSkipped = config.data && !config.data.providers.some((p) => !p.local && p.enabled && p.keySource !== 'none');
  const finish = async () => {
    if (routesDirty || savingRoutes) { setStep(2); return; }
    try {
      await saveSettings({ setupComplete: true });
      await refreshStatus();
      toast.push('ok', 'Setup saved. Fairlead keeps running in the tray; this tab can be closed.');
      navigate('overview');
    } catch (e) { toast.push('err', describeError(e)); }
  };
  return (
    <Page title="Set up Fairlead" lede="A one-time setup. After this, Fairlead runs from the tray and your tools only need its address and a key.">
      <ol className="flex flex-wrap gap-2" aria-label="Setup steps">
        {STEPS.map((s, i) => (
          <li key={s}>
            <button type="button" disabled={savingRoutes} onClick={() => setStep(i)} aria-current={i === step ? 'step' : undefined}
              className={cn('flex h-8 items-center gap-2 rounded-full border px-3 text-[13px]', i === step ? 'border-accent text-accent' : i < step ? 'border-line-2 text-ink' : 'border-line-2 text-ink-3')}>
              <span className={cn('num inline-flex h-5 w-5 items-center justify-center rounded-full text-[11px]', i < step ? 'bg-accent text-accent-ink' : 'bg-surface-2')}>{i < step && !(i === 1 && cloudSkipped) && !(i === 2 && routesDirty) ? <LuCheck size={12} /> : i + 1}</span>{s}{i === 1 && i < step && cloudSkipped ? ' · skipped (optional)' : ''}
            </button>
          </li>
        ))}
      </ol>
      {config.error ? <Notice tone="err">{config.error}</Notice> : null}
      {routesDirty ? <Notice tone="warn">Route changes are not saved yet. They stay available when you change setup steps. Save them in Routes before finishing setup.{step !== 2 ? <Button size="sm" disabled={savingRoutes} onClick={() => setStep(2)}>Review route changes</Button> : null}</Notice> : null}
      {step === 0 ? <LocalStep config={config.data} /> : null}
      {step === 1 ? <CloudStep config={config.data} /> : null}
      {step === 2 ? <RoutesStep config={config.data} drafts={routeDrafts} setDrafts={setRouteDrafts} saving={savingRoutes} setSaving={setSavingRoutes} /> : null}
      {step === 3 ? <ConnectStep status={status} config={config.data} /> : null}
      <div className="flex items-center justify-between">
        <Button variant="quiet" disabled={step === 0 || savingRoutes} onClick={() => setStep(step - 1)}><LuArrowLeft size={14} />Back</Button>
        {step < STEPS.length - 1
          ? <Button variant="primary" disabled={savingRoutes} onClick={() => setStep(step + 1)}>Next: {STEPS[step + 1]}<LuArrowRight size={14} /></Button>
          : <Button variant="primary" disabled={routesDirty || savingRoutes} onClick={finish}><LuCheck size={14} />Finish setup</Button>}
      </div>
    </Page>
  );
}

function LocalStep({ config }: { config: Config | undefined }) {
  const detected = useResource('/api/detect', Detected);
  const toast = useToast();
  const [busy, setBusy] = useState<string | null>(null);
  const use = async (preset: string, baseUrl: string, configured: string | null | undefined) => {
    setBusy(preset);
    try {
      let id = configured ?? undefined;
      if (!id) {
        const saved = await saveProvider({ preset, baseUrl, local: true });
        id = saved.providers.find((p) => p.baseUrl.replace(/\/$/, '') === baseUrl.replace(/\/$/, ''))?.id;
      }
      if (id) await discoverProvider(id);
      invalidate('/api/detect');
      toast.push('ok', 'Saved the models this server has.');
    } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(null); }
  };
  return (
    <Section title={<span className="flex items-center gap-2"><LuHardDrive size={16} />Local model servers</span>} aside={<Button size="sm" variant="quiet" onClick={() => void detected.refetch()} busy={detected.isValidating}><LuRefreshCw size={14} />Scan again</Button>}>
      <div className="px-4 py-3 text-ink-2">These answer when the internet is down (Kedge mode) and for private requests. Fairlead looked on the usual ports of this machine.</div>
      {detected.isLoading ? <div className="px-4 pb-4 text-ink-3">Scanning…</div> : null}
      <ul className="flex flex-col">
        {(detected.data ?? []).map((d) => {
          const provider = d.configured ? config?.providers.find((p) => p.id === d.configured) : undefined;
          const saved = provider?.models.length ?? 0;
          return (
            <li key={d.preset} className="hairline flex flex-wrap items-center gap-3 px-4 py-2.5">
              <span className="w-40 font-medium">{d.name}</span>
              <span className="mono text-[12px] text-ink-3">{d.baseUrl}</span>
              {d.running ? <Dot tone="ok">Running · {d.models.length} model{d.models.length === 1 ? '' : 's'}</Dot> : <Dot tone="neutral">Not running</Dot>}
              {saved > 0 ? <Pill tone="ok">{saved} saved</Pill> : null}
              <span className="ml-auto">
                {d.running ? <Button size="sm" variant={saved ? 'default' : 'primary'} busy={busy === d.preset} onClick={() => void use(d.preset, d.baseUrl, d.configured)}>{saved ? 'Refresh models' : 'Use these models'}</Button> : null}
              </span>
              {d.running && d.models.length > 0 ? <div className="basis-full text-[12px] text-ink-3">{d.models.slice(0, 8).map((m) => m.id).join(', ')}{d.models.length > 8 ? ` and ${d.models.length - 8} more` : ''}</div> : null}
            </li>
          );
        })}
      </ul>
      {detected.data && !detected.data.some((d) => d.running) ? (
        <Notice tone="warn" className="m-4">No local server answered. Install Ollama and pull a model (<code>ollama pull qwen2.5:14b</code>), then scan again. Without one, Fairlead has nothing to fall back to offline.</Notice>
      ) : null}
    </Section>
  );
}

function CloudStep({ config }: { config: Config | undefined }) {
  const presets = useResource('/api/presets', Presets);
  const cloud = (presets.data ?? []).filter((p) => p.needsKey);
  return (
    <Section title={<span className="flex items-center gap-2"><LuCloud size={16} />Cloud providers</span>}>
      <div className="px-4 py-3 text-ink-2">Add the keys you have; skip the rest. Keys are encrypted for your Windows account (DPAPI) and never shown again. A key in an environment variable (for example <code>ANTHROPIC_API_KEY</code>) works too.</div>
      <ul>{cloud.map((p) => <CloudRow key={p.id} preset={p} config={config} />)}</ul>
    </Section>
  );
}

function CloudRow({ preset, config }: { preset: { id: string; name: string; baseUrl: string; keyUrl?: string | null; hint: string; apiKeyEnv?: string | null }; config: Config | undefined }) {
  const provider = config?.providers.find((p) => p.preset === preset.id);
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null);
  const save = async () => {
    setBusy(true);
    setResult(null);
    try {
      const saved = await saveProvider(provider ? { id: provider.id } : { preset: preset.id }, key);
      const id = provider?.id ?? saved.providers.find((p) => p.preset === preset.id)?.id;
      setKey('');
      if (!id) return;
      const t = await testProvider(id);
      if (t.ok) {
        await discoverProvider(id);
        setResult({ ok: true, text: `Key works: ${t.models?.length ?? 0} models available.` });
      } else setResult({ ok: false, text: t.error ?? 'The key did not work.' });
    } catch (e) { setResult({ ok: false, text: describeError(e) }); } finally { setBusy(false); }
  };
  return (
    <li className="hairline flex flex-wrap items-center gap-3 px-4 py-2.5">
      <span className="w-40 font-medium">{preset.name}</span>
      <span className="min-w-[12rem] flex-1 text-[12.5px] text-ink-3">{preset.hint}</span>
      {provider ? (provider.keySource === 'none' ? <Pill tone="warn">No key</Pill> : <Pill tone="ok">Key {provider.keySource === 'env' ? `from ${provider.apiKeyEnv}` : provider.keyHint ?? 'saved'}</Pill>) : null}
      <span className="flex items-center gap-2">
        <Input type="password" autoComplete="off" placeholder={provider && provider.keySource !== 'none' ? 'Replace key' : 'Paste API key'} value={key} onChange={(e) => setKey(e.target.value)} className="w-56" aria-label={`${preset.name} API key`} />
        <Button busy={busy} disabled={!key.trim()} onClick={() => void save()}>Save and test</Button>
        {preset.keyUrl ? <a href={preset.keyUrl} target="_blank" rel="noreferrer noopener" className="inline-flex items-center gap-1 text-[12.5px]">Get a key<LuExternalLink size={12} /></a> : null}
      </span>
      {result ? <div className={cn('basis-full text-[12.5px]', result.ok ? 'text-ok' : 'text-err')}>{result.text}</div> : null}
    </li>
  );
}

function RoutesStep({ config, drafts, setDrafts, saving, setSaving }: { config: Config | undefined; drafts: RouteDrafts; setDrafts: Dispatch<SetStateAction<RouteDrafts>>; saving: boolean; setSaving: Dispatch<SetStateAction<boolean>> }) {
  const options = useMemo(() => targetOptions(config), [config]);
  const toast = useToast();
  if (!config) return <div className="panel h-40" />;
  const routes = config.routes.filter((r) => r.strategy !== 'auto');
  const suggest = () => {
    const local = options.filter((o) => o.local);
    const cloud = options.filter((o) => !o.local);
    const big = (o: typeof options[number]) => o.info?.contextWindow ?? 0;
    const smartCloud = [...cloud].sort((a, b) => big(b) - big(a)).slice(0, 2).map((o) => o.ref);
    const fastCloud = cloud.filter((o) => /mini|flash|haiku|fast|instant|8b|small|llama-3\.1-8b/i.test(o.model)).slice(0, 2).map((o) => o.ref);
    const localPick = local.filter((o) => o.info?.tools !== false).slice(0, 2).map((o) => o.ref);
    setDrafts({
      smart: [...smartCloud, ...localPick.slice(0, 1)],
      fast: [...(fastCloud.length ? fastCloud : cloud.slice(0, 1).map((o) => o.ref)), ...localPick.slice(0, 1)],
      local: localPick,
    });
    toast.push('info', 'Suggested targets filled in. Review the order, then save.');
  };
  const save = async () => {
    if (saving) return;
    setSaving(true);
    try {
      for (const r of routes) {
        const targets = drafts[r.id];
        if (targets) await saveRoute({ ...r, targets });
      }
      setDrafts({});
      toast.push('ok', 'Routes saved.');
    } catch (e) { toast.push('err', describeError(e)); } finally { setSaving(false); }
  };
  const dirty = Object.keys(drafts).length > 0;
  return (
    <Section title="Routes" aside={<><Button size="sm" onClick={suggest} disabled={options.length === 0 || saving}>Suggest targets</Button><Button size="sm" variant="primary" busy={saving} disabled={!dirty} onClick={() => void save()}>Save routes</Button></>}>
      <div className="px-4 py-3 text-ink-2">Tools ask for a route by name (<code>auto</code>, <code>smart</code>, <code>fast</code>, <code>local</code>). Each route tries its models in order. <code>auto</code> picks one of the others per request with the routing rules.</div>
      {options.length === 0 ? <Notice tone="warn" className="mx-4 mb-4">No models yet. Go back and add a local server or a cloud key first.</Notice> : null}
      <fieldset disabled={saving} className="grid gap-4 px-4 pb-4 2xl:grid-cols-3 xl:grid-cols-2">
        {routes.map((r) => (
          <div key={r.id} className="flex flex-col gap-2">
            <div><span className="mono font-semibold text-ink">{r.id}</span> <span className="text-[12.5px] text-ink-3">{r.description}</span></div>
            <TargetList value={drafts[r.id] ?? r.targets} options={options} localOnly={r.id === 'local'} onChange={(next) => setDrafts({ ...drafts, [r.id]: next })} />
          </div>
        ))}
      </fieldset>
    </Section>
  );
}

function ConnectStep({ status, config }: { status: Status | undefined; config: Config | undefined }) {
  const [key, setKey] = useState<string | null>(null);
  const [name, setName] = useState('hackathon');
  const toast = useToast();
  const base = status?.gatewayUrl ?? 'http://127.0.0.1:8776/v1';
  const make = async () => {
    try {
      const r = await createClient(name.trim(), []);
      setKey(r.key);
    } catch (e) { toast.push('err', describeError(e)); }
  };
  const first = snippets(base, key ?? '').filter((s) => s.status === 'ready').slice(0, 3);
  return (
    <Section title="Connect your tools">
      <div className="grid gap-5 px-4 py-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]">
        <div className="flex flex-col gap-3">
          <div className="flex items-center gap-3">
            <Emblem size={72} />
            <p className="text-ink-2">Every tool gets the same address. A client key says which tool is calling, so the Activity page can tell them apart.</p>
          </div>
          <Field label="Gateway address"><div className="flex items-center gap-2"><Input readOnly value={base} className="mono" /><CopyButton text={base} /></div></Field>
          {key ? (
            <Notice tone="ok"><div className="flex flex-col gap-2"><span>Key for <b>{name}</b>. It is shown once: copy it now.</span><div className="flex items-center gap-2"><code className="break-all text-ink">{key}</code><CopyButton text={key} /></div></div></Notice>
          ) : (
            <div className="flex items-end gap-2">
              <Field label="Name this key (the tool or team using it)" className="flex-1"><Input value={name} onChange={(e) => setName(e.target.value)} /></Field>
              <Button variant="primary" disabled={!name.trim()} onClick={() => void make()}>Create key</Button>
            </div>
          )}
          {config && config.clients.length > 0 && !key ? <div className="text-[12.5px] text-ink-3">{config.clients.length} key{config.clients.length === 1 ? '' : 's'} already exist: {config.clients.map((c) => c.name).join(', ')}.</div> : null}
          <div className="text-[12.5px] text-ink-3">More tools (Open WebUI, Continue, Aider, Halyard) are on the Clients page.</div>
        </div>
        <div className="flex flex-col gap-3">
          {first.map((s) => (
            <div key={s.id} className="panel">
              <div className="flex items-center justify-between px-3 py-1.5"><span className="font-medium">{s.tool}</span><CopyButton text={s.code} /></div>
              <pre className="snippet hairline m-0 bg-surface-2 px-3 py-2">{s.code}</pre>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}
