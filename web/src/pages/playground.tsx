import { useMemo, useState } from 'react';
import { LuPlay, LuRoute } from 'react-icons/lu';
import { targetOptions } from '../components/targets';
import { Button, Checkbox, Field, Input, Notice, Page, Pill, Section, Textarea } from '../components/ui';
import { describeError, post } from '../lib/api';
import { useConfig } from '../lib/config';
import { ms } from '../lib/format';
import type { RequestRecord } from '../lib/schemas';
import { Trace } from './activity';

type Explain = { route: string; decision: string[]; error?: string | null; kedge: boolean; localOnly: boolean; profile: { estimatedTokens: number; hasTools: boolean; sensitive: boolean; sensitiveReasons: string[] }; candidates: Array<{ target: string; local: boolean; parked: boolean; parkedBecause?: string | null; dialect: string }> };
type Played = { status: number; text?: string | null; error?: string | null; route?: string | null; target?: string | null; record?: RequestRecord | null };

/** Ask Fairlead where a prompt would go (dry run) or send it for real and see the whole trace. */
export function PlaygroundPage() {
  const config = useConfig();
  const [model, setModel] = useState('auto');
  const [prompt, setPrompt] = useState('In one sentence, what does a fairlead do on a sailing boat?');
  const [route, setRoute] = useState('');
  const [priv, setPriv] = useState(false);
  const [tools, setTools] = useState(false);
  const [busy, setBusy] = useState<'explain' | 'send' | null>(null);
  const [explain, setExplain] = useState<Explain | null>(null);
  const [played, setPlayed] = useState<Played | null>(null);
  const [error, setError] = useState<string | null>(null);
  const names = useMemo(() => [...(config.data?.routes.map((r) => r.id) ?? []), ...targetOptions(config.data).map((o) => o.ref)], [config.data]);
  const body = { model, prompt, route: route || null, private: priv, withTools: tools, maxTokens: 400 };

  const run = async (what: 'explain' | 'send') => {
    setBusy(what);
    setError(null);
    try {
      if (what === 'explain') { setExplain(await post<Explain>('/api/explain', body)); }
      else { setPlayed(await post<Played>('/api/playground', body)); setExplain(null); }
    } catch (e) { setError(describeError(e)); } finally { setBusy(null); }
  };

  return (
    <Page title="Playground" lede="Check a route before a tool depends on it. Explain shows the plan without calling any model; Send runs it for real.">
      <Section title="Request">
        <div className="grid gap-3 px-4 py-4 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
          <Field label="Model (a route or provider/model)"><Input className="mono" list="fl-models" value={model} onChange={(e) => setModel(e.target.value)} /><datalist id="fl-models">{names.map((n) => <option key={n} value={n} />)}</datalist></Field>
          <Field label="X-Fairlead-Route header (optional)" hint="Overrides the model name, like a tool sending the header."><Input className="mono" value={route} onChange={(e) => setRoute(e.target.value)} placeholder="local" /></Field>
          <Field label="Prompt" className="md:col-span-2"><Textarea rows={5} value={prompt} onChange={(e) => setPrompt(e.target.value)} /></Field>
          <div className="flex flex-wrap gap-4 md:col-span-2">
            <Checkbox label="Private (X-Fairlead-Privacy: local)" checked={priv} onChange={(e) => setPriv(e.target.checked)} />
            <Checkbox label="Include a tool definition (an agent-style request)" checked={tools} onChange={(e) => setTools(e.target.checked)} />
          </div>
        </div>
        <div className="hairline flex justify-end gap-2 px-4 py-2.5">
          <Button busy={busy === 'explain'} onClick={() => void run('explain')}><LuRoute size={14} />Explain</Button>
          <Button variant="primary" busy={busy === 'send'} onClick={() => void run('send')}><LuPlay size={14} />Send</Button>
        </div>
      </Section>
      {error ? <Notice tone="err">{error}</Notice> : null}
      {explain ? (
        <Section title={<span>Plan: route <span className="mono">{explain.route || '—'}</span></span>} aside={<>{explain.kedge ? <Pill tone="warn">Kedge</Pill> : null}{explain.profile.sensitive ? <Pill tone="warn">private: {explain.profile.sensitiveReasons.join(', ')}</Pill> : null}<Pill>~{explain.profile.estimatedTokens} tokens</Pill></>}>
          <ol className="list-decimal px-8 py-3 text-ink-2">{explain.decision.map((d, i) => <li key={i}>{d}</li>)}</ol>
          {explain.error ? <Notice tone="err" className="mx-4 mb-3">{explain.error}</Notice> : null}
          {explain.candidates.length ? (
            <table className="table-dense hairline w-full">
              <thead><tr><th>#</th><th>Target</th><th>Where</th><th>Speaks</th><th>Note</th></tr></thead>
              <tbody>{explain.candidates.map((c, i) => <tr key={c.target}><td className="num">{i + 1}</td><td className="mono">{c.target}</td><td>{c.local ? 'local' : 'cloud'}</td><td>{c.dialect}</td><td className="text-ink-3">{c.parked ? `last resort: ${c.parkedBecause}` : ''}</td></tr>)}</tbody>
            </table>
          ) : null}
        </Section>
      ) : null}
      {played ? (
        <Section title={played.status < 300 ? <span>Answer from <span className="mono">{played.target}</span></span> : 'Failed'} aside={played.record ? <span>{ms(played.record.totalMs)} · {played.record.inputTokens ?? '—'} in / {played.record.outputTokens ?? '—'} out</span> : null}>
          {played.status < 300 ? <div className="whitespace-pre-wrap px-4 py-3 text-ink">{played.text || <span className="text-ink-3">(empty answer: a thinking model may have spent its tokens reasoning; raise the limit or pick another model)</span>}</div> : <Notice tone="err" className="m-4">{played.error}</Notice>}
          {played.record ? <div className="hairline"><Trace record={played.record} /></div> : null}
        </Section>
      ) : null}
    </Page>
  );
}
