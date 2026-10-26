// Thin client for the Fairlead host. Every /api call carries the per-session token the launcher
// put in the URL once; nothing else on the machine can talk to the host without it.

const TOKEN_KEY = 'fairlead.session';

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public requestId?: string) {
    super(message);
    this.name = 'ApiError';
  }
}

export function captureSessionToken(): string | null {
  try {
    const url = new URL(window.location.href);
    const fromUrl = url.searchParams.get('session');
    if (fromUrl) {
      sessionStorage.setItem(TOKEN_KEY, fromUrl);
      url.searchParams.delete('session');
      window.history.replaceState({}, '', url.pathname + (url.search || '') + url.hash);
      return fromUrl;
    }
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function sessionToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
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
