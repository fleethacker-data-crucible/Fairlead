// Live request events from /api/live (server-sent events). Read with fetch rather than EventSource so the session
// token travels in the X-Fairlead-Session header like every other /api call, never in the URL. An engine without
// /api/live (older build) falls back to polling /api/requests and replays finished requests.
import { useEffect, useRef, useState } from 'react';
import { api, sessionToken } from './api';

export type LiveState = 'connecting' | 'live' | 'polling' | 'offline';
export type LiveHandler = (type: string, data: Record<string, unknown>) => void;

/** Splits an SSE byte stream into events. Returns the unparsed remainder. */
export function parseSse(buffer: string, emit: (type: string, data: string) => void): string {
  const blocks = buffer.replace(/\r\n/g, '\n').split('\n\n');
  const rest = blocks.pop() ?? '';
  for (const block of blocks) {
    let type = 'message';
    const data: string[] = [];
    for (const line of block.split('\n')) {
      if (line.startsWith(':')) continue;
      const colon = line.indexOf(':');
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
      if (field === 'event') type = value;
      else if (field === 'data') data.push(value);
    }
    if (data.length) emit(type, data.join('\n'));
  }
  return rest;
}

export function connectLive(onEvent: LiveHandler, onState: (s: LiveState) => void): () => void {
  let stopped = false;
  let controller: AbortController | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let failures = 0;

  const later = (fn: () => void, ms: number) => { if (!stopped) timer = setTimeout(fn, ms); };

  const poll = (seen: Set<string> | null) => {
    if (stopped) return;
    onState('polling');
    api<Array<Record<string, unknown>>>('/api/requests?limit=20')
      .then((list) => {
        const known = seen ?? new Set(list.map((r) => String(r.id)));
        if (seen) for (const r of [...list].reverse()) if (!known.has(String(r.id))) { known.add(String(r.id)); onEvent('done', r); }
        later(() => poll(known), 2000);
      })
      .catch(() => later(() => poll(seen), 5000));
  };

  const open = async (quiet = false) => {
    if (stopped) return;
    if (!quiet) onState('connecting');
    controller = new AbortController();
    const headers: Record<string, string> = { Accept: 'text/event-stream' };
    const token = sessionToken();
    if (token) headers['X-Fairlead-Session'] = token;
    try {
      const response = await fetch('/api/live', { headers, cache: 'no-store', signal: controller.signal });
      if (response.status === 404) { poll(null); return; }
      if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      let hello = false;
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer = parseSse(buffer + decoder.decode(value, { stream: true }), (type, data) => {
          if (type === 'hello') { hello = true; failures = 0; onState('live'); return; }
          try { onEvent(type, JSON.parse(data) as Record<string, unknown>); } catch { /* not JSON: ignore */ }
        });
      }
      // A clean close after a good start: reconnect quietly; the next attempt reports if the engine is gone.
      if (hello) { later(() => void open(true), 500); return; }
      throw new Error('closed');
    } catch {
      if (stopped) return;
      failures++;
      onState('offline');
      later(() => void open(), Math.min(15000, 1500 * failures));
    }
  };

  void open();
  return () => {
    stopped = true;
    if (timer) clearTimeout(timer);
    controller?.abort();
  };
}

/** Subscribes for the component's lifetime. The handler may change between renders without reconnecting. */
export function useLive(handler: LiveHandler, enabled = true): LiveState {
  const [state, setState] = useState<LiveState>('connecting');
  const ref = useRef(handler);
  ref.current = handler;
  useEffect(() => {
    if (!enabled) return;
    return connectLive((t, d) => ref.current(t, d), setState);
  }, [enabled]);
  return state;
}
