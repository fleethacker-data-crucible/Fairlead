import { useState } from 'react';
import { LuKeyRound, LuTrash2 } from 'react-icons/lu';
import { Button, ConfirmDialog, CopyButton, Empty, Field, Input, Notice, Page, Pill, Section, Select, Tabs, useToast } from '../components/ui';
import { describeError } from '../lib/api';
import { credentialTarget, FLEET_APPS, isValidAppName } from '../lib/apps';
import { createClient, deleteClient, installClient, useConfig } from '../lib/config';
import { ago } from '../lib/format';
import type { Status } from '../lib/schemas';
import { snippets } from '../lib/snippets';
import { useLocalState } from '../lib/storage';

export function ClientsPage({ status }: { status: Status | undefined }) {
  const config = useConfig();
  const toast = useToast();
  const [name, setName] = useState('');
  const [routes, setRoutes] = useState('');
  const [fresh, setFresh] = useState<{ name: string; key: string } | null>(null);
  const [remove, setRemove] = useState<{ id: string; name: string } | null>(null);
  const [app, setApp] = useState<string>(FLEET_APPS[0]);
  const [custom, setCustom] = useState('');
  const [installed, setInstalled] = useState<string | null>(null);
  const appName = app === '' ? custom.trim() : app;
  const [tool, setTool] = useLocalState('clients-tool', 'claude-code');
  const base = status?.gatewayUrl ?? 'http://127.0.0.1:8776/v1';
  const list = snippets(base, fresh?.key ?? '');
  const current = list.find((s) => s.id === tool) ?? list[0]!;

  const create = async () => {
    try {
      const r = await createClient(name.trim(), routes.split(',').map((x) => x.trim()).filter(Boolean));
      setFresh({ name: name.trim(), key: r.key });
      setName('');
      setRoutes('');
    } catch (e) { toast.push('err', describeError(e)); }
  };

  const install = async () => {
    try {
      const r = await installClient(appName, []);
      setInstalled(r.installed);
      if (app === '') setCustom('');
    } catch (e) { setInstalled(null); toast.push('err', describeError(e)); }
  };

  return (
    <Page title="Clients" lede="One key per tool or team. The key tells Fairlead who is calling; the Activity page and the rules can tell them apart.">
      {config.data && !config.data.requireClientKey ? <Notice tone="warn">Keys are optional right now (Settings): any program on this machine can use the gateway. Callers from other machines always need a key.</Notice> : null}
      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)]">
        <div className="flex flex-col gap-5">
          <Section title="Install for an app">
            <div className="flex flex-col gap-3 px-4 py-4">
              <Field label="App" hint="Fairlead makes a key for the app and saves it to Windows Credential Manager. The key is not shown.">
                <Select value={app} onChange={(e) => { setApp(e.target.value); setInstalled(null); }}>
                  {FLEET_APPS.map((a) => <option key={a} value={a}>{a}</option>)}
                  <option value="">Another app…</option>
                </Select>
              </Field>
              {app === '' ? <Field label="App name" hint="A letter first, then letters, digits and hyphens (up to 40)."><Input value={custom} onChange={(e) => { setCustom(e.target.value); setInstalled(null); }} /></Field> : null}
              <div><Button variant="primary" disabled={!isValidAppName(appName)} onClick={() => void install()}><LuKeyRound size={14} />Install for this app</Button></div>
              {installed ? <Notice tone="ok">Saved to Windows Credential Manager as <b>{installed || credentialTarget(appName)}</b>. The app reads it from there.</Notice> : null}
            </div>
          </Section>
          <Section title="New key">
            <div className="flex flex-col gap-3 px-4 py-4">
              <Field label="Name" hint="The tool or team: claude-code, open-webui, team-blue…"><Input value={name} onChange={(e) => setName(e.target.value)} /></Field>
              <Field label="Limit to routes (optional, comma separated)" hint="Empty = every route. A team on a budget might get local,fast."><Input className="mono" value={routes} onChange={(e) => setRoutes(e.target.value)} placeholder="local, fast" /></Field>
              <div><Button variant="primary" disabled={!name.trim()} onClick={() => void create()}><LuKeyRound size={14} />Create key</Button></div>
              {fresh ? (
                <Notice tone="ok"><div className="flex flex-col gap-2"><span>Key for <b>{fresh.name}</b>, shown once. The snippets on the right now include it.</span><div className="flex items-center gap-2"><code className="break-all text-ink">{fresh.key}</code><CopyButton text={fresh.key} /></div></div></Notice>
              ) : null}
            </div>
          </Section>
          <Section title={`Keys (${config.data?.clients.length ?? 0})`}>
            {!config.data || config.data.clients.length === 0 ? <Empty title="No keys yet">Create one above.</Empty> : (
              <table className="table-dense w-full">
                <thead><tr><th>Name</th><th>Starts with</th><th>Routes</th><th>Created</th><th /></tr></thead>
                <tbody>
                  {config.data.clients.map((c) => (
                    <tr key={c.id}>
                      <td className="font-medium">{c.name}</td>
                      <td className="mono">{c.prefix}…</td>
                      <td>{c.routes.length ? c.routes.map((r) => <Pill key={r} className="mr-1">{r}</Pill>) : <span className="text-ink-3">all</span>}</td>
                      <td className="text-ink-2">{ago(c.created)}</td>
                      <td><Button size="sm" variant="quiet" aria-label={`Revoke ${c.name}`} onClick={() => setRemove({ id: c.id, name: c.name })}><LuTrash2 size={14} />Revoke</Button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Section>
        </div>
        <section className="panel flex min-w-0 flex-col">
          <Tabs id="client-tools" label="Tool setup" value={current.id} onChange={setTool} items={list.map((s) => ({ value: s.id, label: s.tool }))} panelClassName="min-w-0">
          <div className="flex flex-col gap-2 px-4 py-3">
            <div className="flex items-start justify-between gap-3">
              <p className="text-ink-2">{current.note}</p>
              {current.status === 'hackathon' ? <Pill tone="warn">not yet verified against the live tool</Pill> : <Pill tone="ok">tested</Pill>}
            </div>
            <div className="panel"><div className="flex justify-end px-2 py-1"><CopyButton text={current.code} /></div><pre className="snippet hairline m-0 bg-surface-2 px-3 py-2">{current.code}</pre></div>
            {!fresh ? <p className="text-[12.5px] text-ink-3">Shown with a placeholder key. Create a key to fill it in; existing keys cannot be shown again.</p> : null}
          </div>
          </Tabs>
        </section>
      </div>
      <ConfirmDialog open={remove !== null} danger title={`Revoke ${remove?.name}?`} confirmLabel="Revoke key" onCancel={() => setRemove(null)}
        onConfirm={() => { const r = remove!; setRemove(null); void deleteClient(r.id).then(() => toast.push('ok', `${r.name} revoked.`), (e) => toast.push('err', describeError(e))); }}>
        Tools using this key get 401 from the next request on.
      </ConfirmDialog>
    </Page>
  );
}
