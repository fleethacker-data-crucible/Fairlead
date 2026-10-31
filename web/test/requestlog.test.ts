import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DEFAULT_FILTERS, failRate, logQuery, statusTone, who } from '../src/lib/requestlog';
import { RequestLogEntries, RequestLogSummary } from '../src/lib/schemas';

test('the default filters ask for the last day, newest 500', () => {
  assert.equal(logQuery(DEFAULT_FILTERS), '?since=24h&limit=500');
  assert.equal(logQuery(DEFAULT_FILTERS, false), '?since=24h');
});

test('filters become query parameters, trimmed and encoded, and empty ones are left out', () => {
  const q = logQuery({ since: '7d', client: ' claude code ', label: 'nightly/report', model: 'ollama/phi4', status: '5xx', limit: 100 });
  assert.equal(q, '?since=7d&client=claude%20code&label=nightly%2Freport&model=ollama%2Fphi4&status=5xx&limit=100');
  assert.equal(logQuery({ since: 'all', client: '', label: '  ', model: '', status: 'all', limit: 1000 }), '?limit=1000');
  assert.equal(logQuery({ since: 'all', client: '', label: '', model: '', status: 'all', limit: 1000 }, false), '');
});

test('a status says what it means in words as well as colour', () => {
  assert.deepEqual(statusTone(200), { tone: 'ok', label: '200' });
  assert.deepEqual(statusTone(401), { tone: 'err', label: '401 rejected' });
  assert.deepEqual(statusTone(429), { tone: 'warn', label: '429 limited' });
  assert.deepEqual(statusTone(503), { tone: 'err', label: '503 server error' });
  assert.equal(statusTone(0).tone, 'neutral');
});

test('who names the key, the label and the key prefix', () => {
  assert.deepEqual(who({ client: 'report-bot', label: 'nightly', keyPrefix: 'fl-ab12' }), { name: 'report-bot', detail: 'label nightly · fl-ab12…' });
  assert.deepEqual(who({ client: null, label: null, keyPrefix: null }), { name: '(no key)', detail: '' });
  assert.deepEqual(who({ client: '', label: 'x' }), { name: '(no key)', detail: 'label x' });
});

test('failure rate reads as a percentage, never a fake number for no requests', () => {
  assert.equal(failRate(0, 10), '0%');
  assert.equal(failRate(1, 1000), '<1%');
  assert.equal(failRate(1, 4), '25%');
  assert.equal(failRate(0, 0), '—');
});

const entry = {
  id: 'r1', time: '2026-11-01T10:00:00+00:00', client: 'report-bot', clientId: 'k1', keyPrefix: 'fl-ab12', label: 'nightly',
  endpoint: '/v1/chat/completions', dialect: 'openai', requestedModel: 'fast', route: 'fast', servedBy: 'groq/llama-fast', fallbackVia: null,
  local: false, stream: false, status: 200, error: null, firstByteMs: 120, totalMs: 480, inputTokens: 12, outputTokens: 30, cost: null, failover: false, attempts: 1,
};

test('the API shape of the request log validates, and a newer engine adding a field does not break it', () => {
  const parsed = RequestLogEntries.parse([entry, { ...entry, id: 'r2', client: null, label: null, status: 401, servedBy: null, route: null, error: 'no key', future: 1 }]);
  assert.equal(parsed.length, 2);
  assert.equal(parsed[1]!.status, 401);
  assert.throws(() => RequestLogEntries.parse([{ ...entry, status: 'ok' }]));
});

test('the summary shape validates with its four groupings', () => {
  const group = { name: 'report-bot', requests: 3, failed: 1, inputTokens: 30, outputTokens: 60, cost: null, p50Ms: 400, p95Ms: 900 };
  const parsed = RequestLogSummary.parse({ requests: 3, failed: 1, inputTokens: 30, outputTokens: 60, cost: null, from: '2026-11-01T10:00:00+00:00', to: null, byClient: [group], byLabel: [], byModel: [group], byRoute: [group] });
  assert.equal(parsed.byClient[0]!.name, 'report-bot');
  assert.equal(parsed.byLabel.length, 0);
});
