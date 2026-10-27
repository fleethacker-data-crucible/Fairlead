import { useRef, useState } from 'react';
import { LuFileUp, LuTerminal, LuTrash2 } from 'react-icons/lu';
import { describeError } from '../lib/api';
import { importGatewayCurl, importGatewayFile, removeGatewayFile, saveGatewayValues } from '../lib/config';
import type { GatewayVariable, GatewayView } from '../lib/schemas';
import { Button, ConfirmDialog, Field, Input, Notice, Pill, Section, Textarea, useToast } from './ui';
import { GatewayTestButton } from './gateway-test';

const sourceText: Record<string, string> = {
  imported: 'Imported here; kept encrypted for your Windows account in Fairlead\'s data folder, never in the app folder.',
  environment: 'Read from the path in FAIRLEAD_GATEWAY_CONFIG.',
  file: 'Read from the config folder.',
};

/**
 * The AI gateway model files (one per gateway): choose them here, enter a gateway address once if a file asks for
 * one, and Fairlead adds one provider per endpoint at once. Tokens are never entered here: they come from Lazarette or the environment.
 */
export function GatewayCard({ gateway }: { gateway: GatewayView | null | undefined }) {
  const toast = useToast();
  const fileInput = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [refused, setRefused] = useState<string[] | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState<string | null>(null);
  const [curlOpen, setCurlOpen] = useState(false);
  const [curl, setCurl] = useState('');
  const [tokenVar, setTokenVar] = useState('');
  const [curlNotes, setCurlNotes] = useState<string[]>([]);
  const [curlTarget, setCurlTarget] = useState<string | null>(null);
  const g = gateway ?? null;
  const canImport = g?.canImport ?? true;

  const run = async (what: string, f: () => Promise<void>) => {
    setBusy(what);
    try { await f(); } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(null); }
  };

  const onFile = (file: File | undefined) => {
    if (!file) return;
    void run('import', async () => {
      const text = await file.text();
      const r = await importGatewayFile(text, file.name);
      if (!r.saved) { setRefused(r.errors ?? ['The file was not accepted.']); return; }
      setRefused(null);
      toast.push('ok', `Imported ${file.name}.`);
    });
    if (fileInput.current) fileInput.current.value = '';
  };

  const fromCurl = () => run('curl', async () => {
    const r = await importGatewayCurl(curl, tokenVar.trim());
    if (!r.saved) { setRefused(r.errors ?? ['The command was not accepted.']); setCurlNotes([]); setCurlTarget(null); return; }
    setRefused(null);
    setCurlNotes(r.warnings ?? []);
    setCurlTarget(r.target ?? null);
    setCurl('');
    toast.push('ok', `Added ${r.target ?? 'the gateway'} from the curl command.`);
  });

  const editable = (g?.variables ?? []).filter((v) => v.kind !== 'token');
  const dirty = Object.values(values).some((v) => v.trim() !== '');
  const save = () => run('save', async () => {
    const send: Record<string, string> = {};
    for (const [k, v] of Object.entries(values)) if (v.trim() !== '') send[k] = v.trim();
    await saveGatewayValues(send);
    setValues({});
    toast.push('ok', 'Saved.');
  });

  return (
    <Section title="AI gateway"
      aside={canImport ? <>
        <input ref={fileInput} type="file" accept=".yaml,.yml,.txt" className="sr-only" id="gateway-file" aria-label="AI gateway model file" onChange={(e) => onFile(e.target.files?.[0])} />
        <Button size="sm" variant={g?.present ? 'default' : 'primary'} busy={busy === 'import'} onClick={() => fileInput.current?.click()}><LuFileUp size={14} />Import model file</Button>
        <Button size="sm" aria-expanded={curlOpen} onClick={() => setCurlOpen(!curlOpen)}><LuTerminal size={14} />Add from curl</Button>
        {g?.imported ? <Button size="sm" variant="danger" onClick={() => setConfirmRemove('*')}><LuTrash2 size={14} />Remove all</Button> : null}
      </> : undefined}>
      <div className="flex flex-col gap-3 px-4 py-4 text-[13px]">
        {!g?.present ? (
          <p className="text-ink-2">Have a gateway model file (YAML listing endpoints and models)? Import it and Fairlead adds each endpoint as a provider. Import one file per gateway; each is kept encrypted on this PC, not in the app folder or a repository. Importing a file with the same name replaces it.</p>
        ) : (
          <>
            <p className="text-ink-2">{sourceText[g.source ?? 'file']}</p>
            <p>
              {g.endpoints.length} endpoint{g.endpoints.length === 1 ? '' : 's'} ({g.endpoints.join(', ')}), {g.enabledModels} of {g.models} models enabled.{' '}
              {g.ok ? <Pill>{g.providers.length} provider{g.providers.length === 1 ? '' : 's'} added</Pill> : <span className="text-warn">Not all in use yet: see below.</span>}
            </p>
            {g.files.length > 1 || (g.imported && g.files.length > 0) ? (
              <ul className="flex flex-col divide-y divide-line rounded-ctl border border-line" aria-label="Imported model files">
                {g.files.map((f) => (
                  <li key={f.name ?? ''} className="flex flex-wrap items-center gap-2 px-3 py-2">
                    <span className="mono font-medium">{f.name}</span>
                    <span className="text-[12px] text-ink-3">{f.endpoints.join(', ')} · {f.enabledModels} of {f.models} models</span>
                    {f.ok ? <Pill>in use</Pill> : <span className="text-[12px] text-warn">{f.errors.length} problem{f.errors.length === 1 ? '' : 's'}</span>}
                    {g.imported && f.name ? <Button size="sm" variant="quiet" className="ml-auto" aria-label={`Remove ${f.name}`} onClick={() => setConfirmRemove(f.name)}><LuTrash2 size={14} />Remove</Button> : null}
                  </li>
                ))}
              </ul>
            ) : null}
          </>
        )}
        {curlOpen && canImport ? (
          <div className="flex flex-col gap-2 rounded-ctl border border-line p-3">
            <Field label="Curl command" hint="Paste the gateway's own example. Fairlead takes the URL, the model and the format (Responses or Chat Completions) from it; a key in the text is ignored and never stored.">
              <Textarea rows={6} className="mono text-[12px]" spellCheck={false} value={curl} onChange={(e) => setCurl(e.target.value)} placeholder={'curl https://gateway.example/.../responses -H "Authorization: Bearer $DATABRICKS_TOKEN" -d \'{"model": "...", "input": [...]}\''} />
            </Field>
            <div className="flex flex-wrap items-end gap-2">
              <Field label="Key variable (if the command does not name one)" className="w-72">
                <Input className="mono" value={tokenVar} placeholder="AI_ENDPOINT_TOKEN" onChange={(e) => setTokenVar(e.target.value)} />
              </Field>
              <Button variant="primary" disabled={!curl.trim()} busy={busy === 'curl'} onClick={() => void fromCurl()}>Add gateway</Button>
            </div>
            {curlNotes.length ? <Notice tone="warn"><ul className="list-disc pl-5">{curlNotes.map((n) => <li key={n}>{n}</li>)}</ul></Notice> : null}
            {curlTarget ? <GatewayTestButton target={curlTarget} /> : null}
          </div>
        ) : null}
        {!canImport ? <Notice tone="info">FAIRLEAD_GATEWAY_CONFIG is set, so the file comes from that path and cannot be imported here.</Notice> : null}
        {refused ? (
          <Notice tone="err">That file was not imported, and nothing was saved:
            <ul className="mt-1 list-disc pl-5">{refused.map((e) => <li key={e}>{e}</li>)}</ul>
          </Notice>
        ) : null}

        {g?.present && g.variables.length > 0 ? (
          <div className="grid gap-3 md:grid-cols-2">
            {g.variables.map((v) => v.kind === 'token' ? <TokenState key={v.name} v={v} /> : (
              <Field key={v.name} label={`${v.kind === 'baseUrl' ? 'Gateway address' : 'Value'} (${v.name})`}
                hint={v.source === 'stored' ? 'Saved, encrypted. Enter a new one to replace it.' : v.source === 'env' ? 'From the environment variable. Enter one here to use instead.' : 'Needed.'}>
                <Input value={values[v.name] ?? ''} autoComplete="off" spellCheck={false} className="mono"
                  placeholder={v.kind === 'baseUrl' ? 'https://gateway.example' : ''} onChange={(e) => setValues({ ...values, [v.name]: e.target.value })} />
              </Field>
            ))}
          </div>
        ) : null}
        {editable.length > 0 ? (
          <div className="flex justify-end"><Button variant="primary" disabled={!dirty} busy={busy === 'save'} onClick={() => void save()}>Save</Button></div>
        ) : null}

        {g?.present && !g.ok && g.errors.length > 0 ? (
          <Notice tone="warn">Problems:
            <ul className="mt-1 list-disc pl-5">{g.errors.map((e) => <li key={e}>{e}</li>)}</ul>
          </Notice>
        ) : null}
        {g?.present && g.warnings.length > 0 ? (
          <ul className="list-disc pl-5 text-[12px] text-ink-3">{g.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
        ) : null}
      </div>

      <ConfirmDialog open={confirmRemove !== null} danger title={confirmRemove === '*' ? 'Remove every imported model file?' : `Remove ${confirmRemove ?? ''}?`} confirmLabel="Remove" onCancel={() => setConfirmRemove(null)}
        onConfirm={() => { const name = confirmRemove; setConfirmRemove(null); void run('remove', () => removeGatewayFile(name === '*' ? undefined : name ?? undefined)); }}>
        {confirmRemove === '*'
          ? 'Every imported file and the saved gateway address are deleted from this PC, and their providers are taken out of use.'
          : 'This file is deleted from this PC and its providers are taken out of use; the other files stay.'}{' '}
        Routes that name them fall through to their next target.
      </ConfirmDialog>
    </Section>
  );
}

function TokenState({ v }: { v: GatewayVariable }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-ink-2">Token ({v.name})</span>
      {v.source === 'lazarette' ? <span className="text-ok">From Lazarette.</span> : v.source === 'env' ? <span>From the environment variable.</span> : (
        <span className="text-warn">Not available. Tokens are never typed into Fairlead: share {v.name} with Fairlead in Lazarette (see below), or set the environment variable.</span>
      )}
      <span className="text-[12px] text-ink-3">Fairlead keeps tokens out of its own files on purpose.</span>
    </div>
  );
}
