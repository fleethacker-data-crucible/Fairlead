import { useEffect, useState } from 'react';
import { LuCloud, LuHardDrive, LuPlus, LuRefreshCw, LuTrash2 } from 'react-icons/lu';
import { GatewayCard } from '../components/gateway';
import { describeModel } from '../components/targets';
import { Button, Checkbox, ConfirmDialog, Empty, Field, Input, Notice, Page, Pill, Section, Select, useToast } from '../components/ui';
import { describeError } from '../lib/api';
import { deleteProvider, discoverProvider, saveProvider, testProvider, useConfig, type TestResult } from '../lib/config';
import { ms } from '../lib/format';
import { Presets, type ModelInfo, type Provider, type Status } from '../lib/schemas';
import { useResource } from '../lib/useResource';
import { ProviderState } from './overview';

export function ProvidersPage({ status }: { status: Status | undefined }) {
  const config = useConfig();
  const presets = useResource('/api/presets', Presets);
  const toast = useToast();
  const [adding, setAdding] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const providers = config.data?.providers ?? [];
  const current = providers.find((p) => p.id === selected) ?? providers[0];

  const add = async () => {
    if (!adding) return;
    try {
      const before = new Set(providers.map((p) => p.id));
      const saved = await saveProvider({ preset: adding });
      const created = saved.providers.find((p) => !before.has(p.id));
      setSelected(created?.id ?? null);
      setAdding('');
    } catch (e) { toast.push('err', describeError(e)); }
  };

  return (
    <Page
      title="Providers"
      lede="Where models live: servers on this machine or network (local) and cloud APIs. Fairlead lists each provider's models itself."
      actions={
        <span className="flex items-center gap-2">
          <Select value={adding} onChange={(e) => setAdding(e.target.value)} aria-label="Provider to add" className="w-64">
            <option value="">Add a provider…</option>
            {(presets.data ?? []).map((p) => <option key={p.id} value={p.id}>{p.local ? 'Local · ' : 'Cloud · '}{p.name}</option>)}
          </Select>
          <Button variant="primary" disabled={!adding} onClick={() => void add()}><LuPlus size={14} />Add</Button>
        </span>
      }
    >
      <GatewayCard gateway={status?.gatewayModels} />
      {config.error ? <Notice tone="err">{config.error}</Notice> : null}
      {providers.length === 0 ? <Section><Empty title="No providers" emblem>Add a local server or a cloud API with the menu above.</Empty></Section> : (
        <div className="grid gap-5 lg:grid-cols-[280px_minmax(0,1fr)]">
          <Section title="All providers">
            <ul>
              {providers.map((p) => {
                const st = status?.providers.find((x) => x.id === p.id);
                return (
                  <li key={p.id}>
                    <button type="button" onClick={() => setSelected(p.id)} aria-current={current?.id === p.id ? 'true' : undefined}
                      className={'flex w-full flex-col items-start gap-0.5 px-4 py-2 text-left hover:bg-surface-2 ' + (current?.id === p.id ? 'bg-surface-2' : '')}>
                      <span className="flex items-center gap-2 font-medium">{p.local ? <LuHardDrive size={14} /> : <LuCloud size={14} />}{p.name}</span>
                      <span className="text-[12px]">{st ? <ProviderState p={st} /> : <span className="text-ink-3">—</span>}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </Section>
          {current ? <ProviderEditor key={current.id} provider={current} probeMs={status?.providers.find((x) => x.id === current.id)?.probeMs} onDeleted={() => setSelected(null)} /> : null}
        </div>
      )}
    </Page>
  );
}

/** A provider from the AI gateway model file: read-only here, changed in that file. */
function ManagedProvider({ provider, probeMs }: { provider: Provider; probeMs: number | null | undefined }) {
  const toast = useToast();
  const [test, setTest] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState(false);
  const check = async () => {
    setBusy(true);
    try { setTest(await testProvider(provider.id)); } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(false); }
  };
  return (
    <Section title={<span className="flex items-center gap-2">{provider.name}<span className="mono text-ink-3">{provider.id}</span></span>}
      aside={<Button size="sm" busy={busy} onClick={() => void check()}>Check reachability</Button>}>
      <Notice tone="info" className="mx-4 mt-4">From the AI gateway model file. To change endpoints or models, import a new version above; it takes effect at once. The address is set above; the token comes from Lazarette or the environment, never from this page.</Notice>
      <dl className="grid gap-x-6 gap-y-2 px-4 py-4 text-[13px] md:grid-cols-2">
        <div><dt className="text-ink-3">API style</dt><dd>OpenAI Responses (called without streaming)</dd></div>
        <div><dt className="text-ink-3">Endpoint path</dt><dd className="mono">{provider.path ?? ''}</dd></div>
        <div><dt className="text-ink-3">Token</dt><dd>{provider.keySource === 'env' ? <>from <span className="mono">{provider.apiKeyEnv}</span></> : provider.keySource === 'stored' ? <>from Lazarette (<span className="mono">{provider.apiKeyEnv}</span>)</> : <span className="text-err">not set ({provider.apiKeyEnv})</span>}</dd></div>
        <div><dt className="text-ink-3">Retries on transient errors</dt><dd>{provider.retries ?? 0}, then the route's next target</dd></div>
        <div><dt className="text-ink-3">Timeout</dt><dd>{provider.timeoutSeconds} s</dd></div>
        <div><dt className="text-ink-3">Last probe</dt><dd>{ms(probeMs)}</dd></div>
        <div className="md:col-span-2"><dt className="text-ink-3">Models ({provider.models.length})</dt><dd className="mono">{provider.models.map((m) => m.id).join(', ')}</dd></div>
      </dl>
      {test ? <Notice tone={test.ok ? 'ok' : 'err'} className="mx-4 mb-3">{test.ok ? `Reachable in ${ms(test.ms)}. The token is checked by the first real request.` : `${test.error} (${test.kind})`}</Notice> : null}
    </Section>
  );
}

function ProviderEditor({ provider, probeMs, onDeleted }: { provider: Provider; probeMs: number | null | undefined; onDeleted: () => void }) {
  if (provider.managed) return <ManagedProvider provider={provider} probeMs={probeMs} />;
  return <EditableProvider provider={provider} probeMs={probeMs} onDeleted={onDeleted} />;
}

function EditableProvider({ provider, probeMs, onDeleted }: { provider: Provider; probeMs: number | null | undefined; onDeleted: () => void }) {
  const toast = useToast();
  const [draft, setDraft] = useState(provider);
  const [key, setKey] = useState('');
  const [test, setTest] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  useEffect(() => setDraft(provider), [provider]);
  const dirty = JSON.stringify(draft) !== JSON.stringify(provider) || key.length > 0;

  const run = async (what: string, f: () => Promise<unknown>) => {
    setBusy(what);
    try { await f(); } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(null); }
  };
  const save = () => run('save', async () => {
    await saveProvider({
      id: draft.id, name: draft.name, kind: draft.kind, baseUrl: draft.baseUrl, local: draft.local, enabled: draft.enabled,
      apiKeyEnv: draft.apiKeyEnv ?? '', timeoutSeconds: draft.timeoutSeconds, streamUsage: draft.streamUsage, models: draft.models,
    }, key);
    setKey('');
    toast.push('ok', 'Saved.');
  });
  const setModel = (id: string, patch: Partial<ModelInfo>) => setDraft({ ...draft, models: draft.models.map((m) => (m.id === id ? { ...m, ...patch } : m)) });
  const price = (v: string) => (v.trim() === '' ? null : Number(v));
  const knownIds = new Set(draft.models.map((m) => m.id.toLowerCase()));
  const unsaved = draft.discovered.filter((m) => !knownIds.has(m.id.toLowerCase()));

  return (
    <div className="flex flex-col gap-5">
      <Section title={<span className="flex items-center gap-2">{draft.name}<span className="mono text-ink-3">{draft.id}</span></span>}
        aside={<>
          <Button size="sm" busy={busy === 'test'} onClick={() => run('test', async () => setTest(await testProvider(provider.id)))}>Test connection</Button>
          <Button size="sm" variant="danger" onClick={() => setConfirmDelete(true)}><LuTrash2 size={14} />Remove</Button>
        </>}>
        <div className="grid gap-3 px-4 py-4 md:grid-cols-2">
          <Field label="Name"><Input value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></Field>
          <Field label="API style" hint="OpenAI-compatible covers Ollama's /v1, LM Studio, vLLM, llama.cpp, Groq, Gemini, OpenRouter…">
            <Select value={draft.kind} onChange={(e) => setDraft({ ...draft, kind: e.target.value as Provider['kind'] })}>
              <option value="openAi">OpenAI-compatible</option>
              <option value="anthropic">Anthropic Messages</option>
              <option value="ollama">Ollama (OpenAI-compatible plus model discovery)</option>
            </Select>
          </Field>
          <Field label="Base URL" hint={draft.kind === 'openAi' ? 'Include /v1, e.g. https://api.openai.com/v1' : 'The server root, e.g. http://127.0.0.1:11434'} className="md:col-span-2">
            <Input value={draft.baseUrl} onChange={(e) => setDraft({ ...draft, baseUrl: e.target.value })} className="mono" />
          </Field>
          <Field label={`API key${provider.keySource === 'stored' ? ` (saved ${provider.keyHint ?? ''})` : provider.keySource === 'env' ? ` (from ${provider.apiKeyEnv})` : ''}`} hint="Encrypted for your Windows account. Changing the base URL forgets a saved key unless you enter it again.">
            <Input type="password" autoComplete="off" value={key} placeholder={provider.keySource === 'none' ? (draft.local ? 'Usually none for local servers' : 'Paste API key') : 'Leave empty to keep'} onChange={(e) => setKey(e.target.value)} />
          </Field>
          <Field label="Or read the key from this environment variable"><Input value={draft.apiKeyEnv ?? ''} onChange={(e) => setDraft({ ...draft, apiKeyEnv: e.target.value })} className="mono" placeholder="ANTHROPIC_API_KEY" /></Field>
          <Field label="Timeout (seconds)" hint="Until the first response headers, and the longest a stream may go quiet."><Input type="number" min={5} max={3600} value={draft.timeoutSeconds} onChange={(e) => setDraft({ ...draft, timeoutSeconds: Number(e.target.value) })} /></Field>
          <div className="flex flex-col justify-end gap-2">
            <Checkbox label="Local: runs on this machine or network (used in Kedge mode and for private requests)" checked={draft.local} onChange={(e) => setDraft({ ...draft, local: e.target.checked })} />
            <Checkbox label="Enabled" checked={draft.enabled} onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })} />
            {draft.kind !== 'anthropic' ? <Checkbox label="Ask for token usage when streaming" checked={draft.streamUsage} onChange={(e) => setDraft({ ...draft, streamUsage: e.target.checked })} /> : null}
          </div>
        </div>
        {test ? <Notice tone={test.ok ? 'ok' : 'err'} className="mx-4 mb-3">{test.ok ? `Connected in ${ms(test.ms)}: ${test.models?.length ?? 0} models.` : `${test.error} (${test.kind})`}</Notice> : null}
        <div className="hairline flex justify-end gap-2 px-4 py-2.5">
          <Button variant="quiet" disabled={!dirty} onClick={() => { setDraft(provider); setKey(''); }}>Discard</Button>
          <Button variant="primary" disabled={!dirty} busy={busy === 'save'} onClick={() => void save()}>Save</Button>
        </div>
      </Section>

      <Section title={`Models (${draft.models.length})`} aside={<><span className="text-[12px]">last probe {ms(probeMs)}</span><Button size="sm" busy={busy === 'discover'} onClick={() => run('discover', async () => { await discoverProvider(provider.id); toast.push('ok', 'Model list refreshed.'); })}><LuRefreshCw size={14} />Fetch model list</Button></>}>
        {draft.models.length === 0 ? (
          <Empty title="No saved models">{unsaved.length ? `${unsaved.length} found on the server; fetch the list to save them.` : 'Fetch the model list, or check the base URL and key.'}</Empty>
        ) : (
          <div className="scroll-y max-h-[480px]">
            <table className="table-dense w-full">
              <thead><tr><th>Model</th><th>Known</th><th className="num">Context</th><th>Tools</th><th>Images</th><th className="num">$ / M in</th><th className="num">$ / M out</th><th /></tr></thead>
              <tbody>
                {draft.models.map((m) => (
                  <tr key={m.id}>
                    <td className="mono">{m.id}{m.embedding ? <Pill className="ml-1">embedding</Pill> : null}</td>
                    <td className="text-[12px] text-ink-3">{describeModel(m) || '—'}</td>
                    <td className="num"><input aria-label={`${m.id} context window`} className="h-7 w-20 rounded-ctl border border-line px-1 text-right" value={m.contextWindow ?? ''} placeholder="—" onChange={(e) => setModel(m.id, { contextWindow: e.target.value ? Number(e.target.value) : null })} /></td>
                    <td><TriState value={m.tools} onChange={(v) => setModel(m.id, { tools: v })} label={`${m.id} tools`} /></td>
                    <td><TriState value={m.vision} onChange={(v) => setModel(m.id, { vision: v })} label={`${m.id} images`} /></td>
                    <td className="num"><input aria-label={`${m.id} input price`} className="h-7 w-16 rounded-ctl border border-line px-1 text-right" value={m.inputPricePerMTok ?? ''} placeholder="—" onChange={(e) => setModel(m.id, { inputPricePerMTok: price(e.target.value) })} /></td>
                    <td className="num"><input aria-label={`${m.id} output price`} className="h-7 w-16 rounded-ctl border border-line px-1 text-right" value={m.outputPricePerMTok ?? ''} placeholder="—" onChange={(e) => setModel(m.id, { outputPricePerMTok: price(e.target.value) })} /></td>
                    <td><Button size="sm" variant="quiet" aria-label={`Forget ${m.id}`} onClick={() => setDraft({ ...draft, models: draft.models.filter((x) => x.id !== m.id) })}>Forget</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="hairline px-4 py-2 text-[12px] text-ink-3">Prices stay empty until you enter them; Fairlead never guesses. Unknown capabilities never exclude a model. Edits here are saved with the Save button above.</div>
      </Section>

      <ConfirmDialog open={confirmDelete} danger title={`Remove ${provider.name}?`} confirmLabel="Remove provider" onCancel={() => setConfirmDelete(false)}
        onConfirm={() => { setConfirmDelete(false); void run('delete', async () => { await deleteProvider(provider.id); onDeleted(); }); }}>
        Its saved key is deleted and it is taken out of every route.
      </ConfirmDialog>
    </div>
  );
}

function TriState({ value, onChange, label }: { value: boolean | null | undefined; onChange: (v: boolean | null) => void; label: string }) {
  return (
    <select aria-label={label} className="h-7 rounded-ctl border border-line px-1 text-[12.5px]" value={value === true ? 'yes' : value === false ? 'no' : ''} onChange={(e) => onChange(e.target.value === 'yes' ? true : e.target.value === 'no' ? false : null)}>
      <option value="">unknown</option>
      <option value="yes">yes</option>
      <option value="no">no</option>
    </select>
  );
}
