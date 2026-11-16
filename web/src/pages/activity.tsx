import { useState } from 'react';
import { RequestLogView } from '../components/request-log';
import { Dot, Empty, Page, Pill, Section, Tabs, cn } from '../components/ui';
import { DASH, clock, count, ms, usd } from '../lib/format';
import { RequestList, type RequestRecord } from '../lib/schemas';
import { useResource } from '../lib/useResource';

type Filter = 'all' | 'failed' | 'failover' | 'kedge' | 'private';
type View = 'run' | 'log';

export function ActivityPage() {
  const requests = useResource('/api/requests?limit=500', RequestList, { pollMs: 3000 });
  const [filter, setFilter] = useState<Filter>('all');
  const [open, setOpen] = useState<string | null>(null);
  const [view, setView] = useState<View>('run');
  const all = requests.data ?? [];
  const pick = (f: Filter) => (r: RequestRecord) => f === 'all' || (f === 'failed' && r.status >= 400) || (f === 'failover' && r.failover) || (f === 'kedge' && r.kedge) || (f === 'private' && r.sensitive);
  const rows = all.filter(pick(filter));
  const selected = rows.find((r) => r.id === open) ?? null;
  return (
    <Page title="Activity" lede="Every gateway request: what was asked for, how the route was chosen, which targets were tried. Prompts are not kept unless content logging is on (Settings).">
      <section className="panel flex min-h-0 flex-col">
        <Tabs id="activity-source" label="Activity source" value={view} onChange={setView} items={[{ value: 'run', label: 'This run' }, { value: 'log', label: 'Log on disk' }]} panelClassName="flex min-h-0 min-w-0 flex-col">
        {view === 'log' ? <RequestLogView /> : <>
        <Tabs id="activity-filter" label="Request filter" value={filter} onChange={setFilter} items={(['all', 'failed', 'failover', 'kedge', 'private'] as Filter[]).map((f) => ({ value: f, label: f === 'all' ? 'All' : f === 'kedge' ? 'Kedge mode' : f[0]!.toUpperCase() + f.slice(1), count: all.filter(pick(f)).length }))} panelClassName="min-w-0">
        {rows.length === 0 ? <Empty title={all.length ? 'Nothing matches this filter' : 'No requests yet'} /> : (
          <div className="grid min-h-0 lg:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)]">
            <div className="scroll-y max-h-[70vh]">
              <table className="table-dense w-full">
                <thead><tr><th>Time</th><th>Client</th><th>Asked for</th><th>Route</th><th>Served by</th><th className="num">Total</th><th className="num">Tokens</th></tr></thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.id} onClick={() => setOpen(r.id)} className={cn('cursor-pointer', open === r.id && '[&>td]:bg-surface-2')} tabIndex={0} onKeyDown={(e) => { if (e.key === 'Enter') setOpen(r.id); }}>
                      <td className="mono text-ink-2">{clock(r.time)}</td>
                      <td>{r.client ?? <span className="text-ink-3">{DASH}</span>}</td>
                      <td className="mono">{r.requestedModel || DASH}</td>
                      <td className="mono">{r.route ?? DASH}</td>
                      <td>{r.status >= 400 ? <Dot tone="err">{r.status}</Dot> : <span className="mono">{r.target}</span>} {r.failover ? <Pill tone="warn">failover</Pill> : null} {r.kedge ? <Pill tone="warn">kedge</Pill> : null} {r.sensitive ? <Pill tone="accent">private</Pill> : null}</td>
                      <td className="num">{ms(r.totalMs)}</td>
                      <td className="num">{r.inputTokens === null || r.inputTokens === undefined ? DASH : count((r.inputTokens ?? 0) + (r.outputTokens ?? 0))}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="border-l border-line-2">
              {selected ? <Detail r={selected} /> : <Empty title="Pick a request">Its routing decision and every attempt show here.</Empty>}
            </div>
          </div>
        )}
        </Tabs>
        </>}
        </Tabs>
      </section>
    </Page>
  );
}

function Detail({ r }: { r: RequestRecord }) {
  return (
    <div className="flex flex-col">
      <div className="flex flex-wrap gap-x-4 gap-y-1 px-4 py-3 text-[12.5px] text-ink-2">
        <span>{r.dialect === 'anthropic' ? 'Anthropic' : 'OpenAI'} <span className="mono">{r.endpoint}</span></span>
        <span>{r.stream ? 'streaming' : 'single response'}</span>
        <span>first token {ms(r.firstByteMs)}</span>
        <span>~{r.estimatedTokens} prompt tokens estimated</span>
        <span>{r.inputTokens ?? DASH} in / {r.outputTokens ?? DASH} out</span>
        <span>cost {usd(r.cost)}</span>
        <span className="mono">id {r.id}</span>
      </div>
      {r.error ? <div className="hairline px-4 py-2 text-err">{r.error}</div> : null}
      <div className="hairline"><Trace record={r} /></div>
      {r.promptPreview ? <Section title="Prompt (latest user message)" className="m-3"><div className="whitespace-pre-wrap px-3 py-2 text-[12.5px]">{r.promptPreview}</div></Section> : null}
      {r.responsePreview ? <Section title="Answer" className="m-3"><div className="whitespace-pre-wrap px-3 py-2 text-[12.5px]">{r.responsePreview}</div></Section> : null}
    </div>
  );
}

/** The routing decision and the attempts, the same in Activity and the Playground. */
export function Trace({ record }: { record: RequestRecord }) {
  return (
    <div className="flex flex-col gap-2 px-4 py-3">
      <div className="text-[12px] font-medium text-ink-3">Decision</div>
      <ol className="list-decimal pl-5 text-[12.5px] text-ink-2">{record.decision.map((d, i) => <li key={i}>{d}</li>)}</ol>
      {record.attempts.length ? (
        <>
          <div className="text-[12px] font-medium text-ink-3">Attempts</div>
          <table className="table-dense w-full text-[12.5px]">
            <tbody>
              {record.attempts.map((a, i) => (
                <tr key={i}>
                  <td className="mono">{a.target}</td>
                  <td>{a.outcome === 'ok' ? <Dot tone="ok">answered</Dot> : a.outcome === 'lost-race' ? <Dot tone="neutral">lost the hedge race</Dot> : a.outcome === 'skipped' ? <Dot tone="neutral">skipped ({a.error})</Dot> : <Dot tone="err">{a.outcome}{a.status ? ` ${a.status}` : ''}</Dot>}</td>
                  <td className="num">{ms(a.ms)}</td>
                  <td className="text-ink-3">{a.outcome !== 'skipped' ? a.error ?? '' : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
    </div>
  );
}
