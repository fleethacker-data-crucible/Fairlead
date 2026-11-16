// Thin client for the Fairlead host. Every /api call carries the per-session token.
// The launcher puts a single-use ticket in the URL. The page claims it once, keeps the
// session token in sessionStorage, and never writes that token to disk.

const TOKEN_KEY = 'fairlead.session';
let heldToken: string | null = null;

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public requestId?: string) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * The launch ticket in `/s/<ticket>/`, and the address to show after it is claimed.
 * A `?session=` or `#session=` value is removed and is not a session token: that would put the
 * long-lived token in history. A route hash such as `#/setup` is left alone.
 */
export function locateLaunchTicket(href: string): { ticket: string | null; next: string } {
  const url = new URL(href);
  url.searchParams.delete('session');
  const segments = url.pathname.split('/');
  const rawPath = segments[2] ?? '';
  const ticket = segments[1] === 's' && /^[0-9a-f]{48}$/i.test(rawPath) ? rawPath : null;
  let hash = url.hash;
  if (/^#session=/i.test(hash)) hash = '';
  const pathname = ticket ? '/' : url.pathname || '/';
  return { ticket, next: pathname + url.search + hash };
}

function remember(token: string): string {
  heldToken = token;
  try { sessionStorage.setItem(TOKEN_KEY, token); } catch { /* this load still uses the token */ }
  return token;
}

/** POSTs the launch ticket once. The host returns the session token and forgets the ticket. */
export async function claimLaunchTicket(): Promise<string | null> {
  try {
    const here = window.location.pathname + window.location.search + window.location.hash;
    const found = locateLaunchTicket(window.location.href);
    if (found.ticket) {
      let token: string | null = null;
      try {
        const response = await fetch('/api/session/claim', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
          body: JSON.stringify({ ticket: found.ticket }),
          cache: 'no-store',
        });
        if (response.ok) {
          const data = (await response.json()) as { token?: unknown };
          if (typeof data.token === 'string' && data.token.trim()) token = data.token.trim();
        }
      } catch {
        token = null;
      }
      try { window.history.replaceState({}, '', found.next); } catch { /* leave the ticket in the address */ }
      if (token) return remember(token);
      return sessionToken();
    }
    if (found.next !== here) {
      try { window.history.replaceState({}, '', found.next); } catch { /* leave the address */ }
    }
    return sessionToken();
  } catch {
    return null;
  }
}

export function sessionToken(): string | null {
  if (heldToken) return heldToken;
  try {
    const stored = sessionStorage.getItem(TOKEN_KEY);
    if (stored) heldToken = stored;
    return stored;
  } catch {
    return null;
  }
}

export async function api<T = unknown>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers = new Headers(init.headers ?? {});
  headers.set('Accept', 'application/json');
  const token = sessionToken();
  if (token) headers.set('X-Fairlead-Session', token);
  let body = init.body;
  if (init.json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(init.json);
  }
  const response = await fetch(path, { ...init, headers, body, cache: 'no-store' });
  const text = await response.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { error: text };
    }
  }
  if (!response.ok) {
    const err = (data ?? {}) as { error?: string; code?: string; requestId?: string };
    throw new ApiError(response.status, err.code ?? `HTTP_${response.status}`, err.error ?? `Request failed with HTTP ${response.status}`, err.requestId);
  }
  return data as T;
}

export const get = <T,>(path: string) => api<T>(path);

/** Fetches a non-JSON resource  with the session header. */
export async function getText(path: string): Promise<string> {
  const headers = new Headers();
  const token = sessionToken();
  if (token) headers.set('X-Fairlead-Session', token);
  const response = await fetch(path, { headers, cache: 'no-store' });
  const text = await response.text();
  if (!response.ok) {
    let message = `Request failed with HTTP ${response.status}`;
    try { message = (JSON.parse(text) as { error?: string }).error ?? message; } catch { /* not json */ }
    throw new ApiError(response.status, `HTTP_${response.status}`, message);
  }
  return text;
}
export const post = <T,>(path: string, json?: unknown, headers?: Record<string, string>) => api<T>(path, { method: 'POST', json: json ?? {}, headers });

export function describeError(e: unknown): string {
  if (e instanceof ApiError) return e.message;
  if (e instanceof Error) return e.message;
  return String(e);
}

/**
 * Presence: the engine stops itself when the tab that drives it is gone. A heartbeat every few seconds
 * says "still here"; a keepalive "leaving" beacon on unload lets it stop within seconds instead of waiting
 * for the timeout. A reload sends a fresh heartbeat inside the grace period, so nothing stops.
 */
export function startHeartbeat(intervalMs = 5000): () => void {
  const token = sessionToken();
  if (!token) return () => {};
  const send = (leaving: boolean) => {
    try {
      void fetch('/api/heartbeat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Fairlead-Session': token },
        body: JSON.stringify({ leaving }),
        keepalive: true,
      }).catch(() => {});
    } catch {
      /* the engine is gone; nothing to do */
    }
  };
  send(false);
  const timer = window.setInterval(() => send(false), intervalMs);
  const onHide = () => {
    // sendBeacon is what browsers actually deliver during unload (a keepalive fetch is dropped by some);
    // it cannot set headers, so the token travels in the body, which the engine accepts for this route only.
    let sent = false;
    try {
      if (navigator.sendBeacon) sent = navigator.sendBeacon('/api/heartbeat', new Blob([JSON.stringify({ leaving: true, session: token })], { type: 'application/json' }));
    } catch {
      sent = false;
    }
    if (!sent) send(true);
  };
  const onShow = () => send(false);
  window.addEventListener('pagehide', onHide);
  window.addEventListener('pageshow', onShow);
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') send(false); });
  return () => {
    window.clearInterval(timer);
    window.removeEventListener('pagehide', onHide);
    window.removeEventListener('pageshow', onShow);
  };
}
export const del = <T,>(path: string) => api<T>(path, { method: 'DELETE' });
