import { useDeferredValue, useState } from 'react';
import { Dot, Empty, Field, Input, Notice, Pill, Select, Stat, Tabs } from './ui';
import { DASH, clock, count, ms, usd } from '../lib/format';
import { DEFAULT_FILTERS, GROUP_TITLES, failRate, logQuery, statusTone, who, type GroupBy, type LogFilters } from '../lib/requestlog';
import { RequestLogEntries, RequestLogSummary } from '../lib/schemas';
import { useResource } from '../lib/useResource';

/** The request log on disk: who called, what for, which model answered. It survives restarts; prompts are never in it. */
export function RequestLogView() {
  const [filters, setFilters] = useState<LogFilters>(DEFAULT_FILTERS);
  const [group, setGroup] = useState<GroupBy>('byClient');
  const set = <K extends keyof LogFilters>(k: K, v: LogFilters[K]) => setFilters((f) => ({ ...f, [k]: v }));
  // Typing in a filter box does not fetch on every key.
  const slow = useDeferredValue(filters);
  const entries = useResource('/api/requestlog' + logQuery(slow), RequestLogEntries, { pollMs: 5000 });
  const summary = useResource('/api/requestlog/summary' + logQuery(slow, false), RequestLogSummary, { pollMs: 5000 });
  const rows = entries.data ?? [];
  const sum = summary.data;
  const error = entries.error ?? summary.error;
  const groups = sum ? sum[group] : [];
  return (
    <div className="flex flex-col">
      <div className="grid gap-3 border-b border-line-2 px-4 py-3 sm:grid-cols-2 lg:grid-cols-6">
        <Field label="Period">
          <Select value={filters.since} onChange={(e) => set('since', e.target.value as LogFilters['since'])}>
            <option value="1h">Last hour</option>
            <option value="24h">Last 24 hours</option>
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
            <option value="all">Everything on disk</option>
          </Select>
        </Field>
        <Field label="Client key"><Input value={filters.client} onChange={(e) => set('client', e.target.value)} placeholder="claude-code" /></Field>
        <Field label="Label"><Input value={filters.label} onChange={(e) => set('label', e.target.value)} placeholder="nightly-report" /></Field>
        <Field label="Model served"><Input className="mono" value={filters.model} onChange={(e) => set('model', e.target.value)} placeholder="ollama/" /></Field>
        <Field label="Outcome">
          <Select value={filters.status} onChange={(e) => set('status', e.target.value as LogFilters['status'])}>
            <option value="all">All</option>
            <option value="ok">Answered</option>
            <option value="error">Failed</option>
            <option value="4xx">Rejected (4xx)</option>
            <option value="5xx">Server errors (5xx)</option>
          </Select>
        </Field>
        <Field label="Show">
          <Select value={String(filters.limit)} onChange={(e) => set('limit', Number(e.target.value) as LogFilters['limit'])}>
            <option value="100">Latest 100</option>
            <option value="500">Latest 500</option>
            <option value="1000">Latest 1000</option>
          </Select>
        </Field>
      </div>
      {error ? <Notice tone="err" className="m-3">{error}</Notice> : null}
      {sum ? (
        <>
          <div className="grid grid-cols-2 border-b border-line-2 lg:grid-cols-5">
            <Stat label="Requests" value={count(sum.requests)} />
            <Stat label="Failed" value={count(sum.failed)} hint={failRate(sum.failed, sum.requests)} />
            <Stat label="Tokens in" value={count(sum.inputTokens)} />
            <Stat label="Tokens out" value={count(sum.outputTokens)} />
            <Stat label="Cost" value={usd(sum.cost)} hint={sum.cost === null || sum.cost === undefined ? 'no prices entered' : undefined} />
          </div>
          <Tabs value={group} onChange={setGroup} items={(Object.keys(GROUP_TITLES) as GroupBy[]).map((g) => ({ value: g, label: GROUP_TITLES[g], count: sum[g].length }))} />
          {groups.length === 0 ? (
            <Empty title={group === 'byLabel' ? 'No labels in this period' : 'Nothing to total'}>{group === 'byLabel' ? 'A caller adds one with the X-Fairlead-Label header.' : undefined}</Empty>
          ) : (
            <div className="scroll-y max-h-[30vh]">
              <table className="table-dense w-full">
                <thead>
                  <tr><th>Name</th><th className="num">Requests</th><th className="num">Failed</th><th className="num">Tokens in</th><th className="num">Tokens out</th><th className="num">Median</th><th className="num">p95</th></tr>
                </thead>
                <tbody>
                  {groups.map((g) => (
                    <tr key={g.name}>
                      <td className={group === 'byModel' || group === 'byRoute' ? 'mono' : undefined}>{g.name}</td>
                      <td className="num">{count(g.requests)}</td>
                      <td className="num">{g.failed ? <Dot tone="err">{count(g.failed)}</Dot> : '0'}</td>
                      <td className="num">{count(g.inputTokens)}</td>
                      <td className="num">{count(g.outputTokens)}</td>
                      <td className="num">{ms(g.p50Ms)}</td>
                      <td className="num">{ms(g.p95Ms)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      ) : null}
      {rows.length === 0 ? (
        <Empty title={entries.isLoading ? 'Reading the log…' : 'No requests match'}>{entries.isLoading ? undefined : 'Widen the period or clear a filter. The log is written to the logs folder, one file a day.'}</Empty>
      ) : (
        <div className="scroll-y max-h-[50vh] border-t border-line-2">
          <table className="table-dense w-full">
            <thead>
              <tr><th>Time</th><th>Who</th><th>What</th><th>Route</th><th>Model served</th><th>Outcome</th><th className="num">Total</th><th className="num">Tokens</th></tr>
            </thead>
            <tbody>
              {rows.map((e) => {
                const w = who(e);
                const t = statusTone(e.status);
                return (
                  <tr key={e.id + e.time}>
                    <td className="mono text-ink-2">{clock(e.time)}</td>
                    <td>{w.name}{w.detail ? <span className="block text-[12px] text-ink-3">{w.detail}</span> : null}</td>
                    <td><span className="mono">{e.endpoint}</span><span className="block text-[12px] text-ink-3">asked for <span className="mono">{e.requestedModel || DASH}</span>{e.stream ? ' · streaming' : ''}</span></td>
                    <td className="mono">{e.route ?? DASH}</td>
                    <td><span className="mono">{e.servedBy || DASH}</span> {e.failover ? <Pill tone="warn">failover</Pill> : null} {e.fallbackVia ? <Pill tone="warn">fallback</Pill> : null}</td>
                    <td><Dot tone={t.tone}>{t.label}</Dot>{e.error ? <span className="block max-w-[28ch] truncate text-[12px] text-ink-3" title={e.error}>{e.error}</span> : null}</td>
                    <td className="num">{ms(e.totalMs)}</td>
                    <td className="num">{e.inputTokens === null || e.inputTokens === undefined ? DASH : count((e.inputTokens ?? 0) + (e.outputTokens ?? 0))}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
