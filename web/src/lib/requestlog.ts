// The durable request log (GET /api/requestlog and /summary): filters to query string, and small display helpers.
// Pure functions, so they are tested without a browser.
import { DASH } from './format';

export type LogFilters = {
  since: '1h' | '24h' | '7d' | '30d' | 'all';
  client: string;
  label: string;
  model: string;
  status: 'all' | 'ok' | 'error' | '4xx' | '5xx';
  limit: 100 | 500 | 1000;
};

export const DEFAULT_FILTERS: LogFilters = { since: '24h', client: '', label: '', model: '', status: 'all', limit: 500 };

/** The query string for /api/requestlog and /api/requestlog/summary; empty filters are left out. */
export function logQuery(f: LogFilters, withLimit = true): string {
  const q: string[] = [];
  const add = (k: string, v: string) => {
    if (v.trim()) q.push(k + '=' + encodeURIComponent(v.trim()));
  };
  if (f.since !== 'all') add('since', f.since);
  add('client', f.client);
  add('label', f.label);
  add('model', f.model);
  if (f.status !== 'all') add('status', f.status);
  if (withLimit) add('limit', String(f.limit));
  return q.length ? '?' + q.join('&') : '';
}

export type Tone = 'ok' | 'warn' | 'err' | 'neutral';

/** Colour and word for a status code, so the meaning never rests on colour alone. */
export function statusTone(status: number): { tone: Tone; label: string } {
  if (status >= 500) return { tone: 'err', label: String(status) + ' server error' };
  if (status === 429) return { tone: 'warn', label: '429 limited' };
  if (status >= 400) return { tone: 'err', label: String(status) + ' rejected' };
  if (status >= 200 && status < 300) return { tone: 'ok', label: String(status) };
  return { tone: 'neutral', label: status ? String(status) : DASH };
}

/** Who made the call: the key's name, its label if one was sent, and the key's visible prefix. */
export function who(e: { client?: string | null; label?: string | null; keyPrefix?: string | null }): { name: string; detail: string } {
  const name = e.client && e.client.length ? e.client : '(no key)';
  const detail = [e.label ? 'label ' + e.label : '', e.keyPrefix ? e.keyPrefix + '…' : ''].filter(Boolean).join(' · ');
  return { name, detail };
}

export type GroupBy = 'byClient' | 'byLabel' | 'byModel' | 'byRoute';
export const GROUP_TITLES: Record<GroupBy, string> = { byClient: 'By client', byLabel: 'By label', byModel: 'By model', byRoute: 'By route' };

/** Share of failed requests as text, "0%" when none failed, a dash when there were no requests. */
export function failRate(failed: number, requests: number): string {
  if (!requests) return DASH;
  const p = (failed / requests) * 100;
  return (p === 0 ? '0' : p < 1 ? '<1' : String(Math.round(p))) + '%';
}
