import { useEffect, useState } from 'react';
import { LuAnchor } from 'react-icons/lu';
import { Button, Checkbox, Field, Input, Notice, Page, Section, Select, Textarea, cn, useToast } from '../components/ui';
import { describeError } from '../lib/api';
import { saveSettings, setKedge, useConfig } from '../lib/config';
import type { Status } from '../lib/schemas';

const KEDGE = [
  { value: 'auto', label: 'Auto', hint: 'Use the cloud while it is reachable; go local the moment it is not, and come back when it returns.' },
  { value: 'on', label: 'On', hint: 'Local models only, whatever the network says (a flight, a metered link, a demo that must not leak).' },
  { value: 'off', label: 'Off', hint: 'Never switch on its own. Failed cloud calls still fall back per route.' },
] as const;

export function SettingsPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const config = useConfig();
  const toast = useToast();
  const c = config.data;
  const [cors, setCors] = useState('');
  const [hosts, setHosts] = useState('');
  const [interval, setInterval] = useState(15);
  useEffect(() => {
    if (!c) return;
    setCors(c.corsOrigins.join('\n'));
    setHosts(c.network.probeHosts.join('\n'));
    setInterval(c.network.probeIntervalSeconds);
  }, [c]);
  if (!c) return <Page title="Settings"><div className="panel h-40" /></Page>;
  const save = async (body: Record<string, unknown>, done = 'Saved.') => {
    try { await saveSettings(body); await refreshStatus(); toast.push('ok', done); } catch (e) { toast.push('err', describeError(e)); }
  };
  const lines = (v: string) => v.split(/\r?\n|,/).map((x) => x.trim()).filter(Boolean);

  return (
    <Page title="Settings">
      <Section title={<span className="flex items-center gap-2"><LuAnchor size={16} />Kedge mode</span>}>
        <div className="px-4 pt-3 text-ink-2">To kedge is to move a ship under your own power when there is no wind: here, to keep answering from local models when the cloud is out of reach.</div>
        <div className="grid gap-2 px-4 py-3 md:grid-cols-3" role="radiogroup" aria-label="Kedge mode">
          {KEDGE.map((k) => (
            <button key={k.value} type="button" role="radio" aria-checked={c.kedge === k.value} onClick={() => void setKedge(k.value).then(refreshStatus, (e) => toast.push('err', describeError(e)))}
              className={cn('flex flex-col items-start gap-1 rounded-panel border px-3 py-2 text-left', c.kedge === k.value ? 'border-accent' : 'border-line-2 hover:bg-surface-2')}>
              <span className={cn('font-medium', c.kedge === k.value && 'text-accent')}>{k.label}</span>
              <span className="text-[12.5px] text-ink-2">{k.hint}</span>
            </button>
          ))}
        </div>
        {status ? <div className="hairline px-4 py-2 text-[12.5px] text-ink-2">Right now: internet {status.connectivity.internet}, {status.connectivity.kedge ? 'local models only' : 'cloud and local'}.</div> : null}
      </Section>

      <Section title="Privacy and access">
        <div className="flex flex-col gap-2.5 px-4 py-3">
          <Checkbox label="Look for secrets in prompts (API keys, private keys, passwords, card numbers)" checked={c.privacy.detectSecrets} onChange={(e) => void save({ privacy: { ...c.privacy, detectSecrets: e.target.checked } })} />
          <Checkbox label="Keep private requests on local models, whatever route was asked for" checked={c.privacy.forceLocalWhenSensitive} onChange={(e) => void save({ privacy: { ...c.privacy, forceLocalWhenSensitive: e.target.checked } })} />
          <Checkbox label="Require a client key from programs on this machine" checked={c.requireClientKey} onChange={(e) => void save({ requireClientKey: e.target.checked })} />
          <Checkbox label="Keep the latest prompt and answer in the Activity log (memory only, never on disk)" checked={c.logContent} onChange={(e) => void save({ logContent: e.target.checked })} />
          <Field label="Keep keys from Lazarette after it locks or closes" hint="Keys stay in memory only and are always dropped at once when you remove the pairing or stop sharing in Lazarette. Shorter is safer; 'only while open' stops the AI gateway whenever the Lazarette tab is closed." className="max-w-md">
            <Select value={String(c.lazaretteHoldHours)} onChange={(e) => void save({ lazaretteHoldHours: Number(e.target.value) })}>
              <option value="0">Only while Lazarette is open and unlocked</option>
              <option value="1">1 hour</option>
              <option value="4">4 hours</option>
              <option value="8">8 hours (default)</option>
              <option value="24">24 hours</option>
            </Select>
          </Field>
        </div>
        {!c.requireClientKey ? <Notice tone="warn" className="mx-4 mb-3">Without keys, any program on this machine can spend your cloud credits through Fairlead. Web pages are still refused unless their origin is listed below.</Notice> : null}
      </Section>

      <Section title="Browser origins">
        <div className="flex flex-col gap-2 px-4 py-3">
          <p className="text-ink-2">Web apps that call <code>/v1</code> straight from the browser (a hackathon front end on <code>http://localhost:5173</code>) must be listed, one per line. Everything else is refused, so a random web page cannot use the gateway.</p>
          <Textarea rows={3} className="mono" value={cors} onChange={(e) => setCors(e.target.value)} placeholder="http://localhost:5173" />
          <div><Button onClick={() => void save({ corsOrigins: lines(cors) })}>Save origins</Button></div>
        </div>
      </Section>

      <Section title="Network checks">
        <div className="grid gap-3 px-4 py-3 md:grid-cols-[minmax(0,1fr)_200px]">
          <Field label="Probe hosts (host:port, one per line)" hint="A plain TCP connect. The cloud providers' own hosts are always probed too, so networks that block public DNS still count as online.">
            <Textarea rows={3} className="mono" value={hosts} onChange={(e) => setHosts(e.target.value)} />
          </Field>
          <Field label="Every (seconds)" hint="Three times as often while offline."><Input type="number" min={3} max={600} value={interval} onChange={(e) => setInterval(Number(e.target.value))} /></Field>
          <div><Button onClick={() => void save({ network: { ...c.network, probeHosts: lines(hosts), probeIntervalSeconds: interval } })}>Save network checks</Button></div>
        </div>
      </Section>

      <Section title="Where things are">
        <div className="grid gap-x-6 gap-y-1.5 px-4 py-3 text-ink-2 md:grid-cols-2">
          <span>Gateway: <code>{status?.gatewayUrl}</code></span>
          <span>Config: <code className="break-all">{status?.configFile ?? '—'}</code></span>
          <span>Log: <code className="break-all">{status?.logFile ?? '—'}</code></span>
          <span>Port, bind and auto-stop are set in <code>config\fairlead.env</code> (see <code>fairlead.env.example</code>).</span>
          <span className="md:col-span-2">Start with Windows: run <code>scripts\Register-FairleadStartup.ps1</code> (no admin; a shortcut in your Startup folder).</span>
        </div>
        <div className="hairline flex justify-end px-4 py-2.5">
          <Button variant="quiet" onClick={() => void save({ setupComplete: false }, 'Setup will open next time.')}>Run setup again on next launch</Button>
        </div>
      </Section>
    </Page>
  );
}
