// Display helpers. A missing value renders as an em dash, never as a made-up number (UNIFIED-UI 7).

export const DASH = '—';

export function ms(v: number | null | undefined): string {
  if (v === null || v === undefined) return DASH;
  if (v < 1000) return `${Math.round(v)} ms`;
  return `${(v / 1000).toFixed(v < 10000 ? 2 : 1)} s`;
}

export function count(v: number | null | undefined): string {
  if (v === null || v === undefined) return DASH;
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (v >= 10_000) return `${(v / 1000).toFixed(1)}k`;
  return v.toLocaleString();
}

export function usd(v: number | null | undefined): string {
  if (v === null || v === undefined) return DASH;
  if (v === 0) return '$0';
  return v < 0.01 ? `$${v.toFixed(4)}` : `$${v.toFixed(2)}`;
}

export function pct(part: number, whole: number): string {
  if (!whole) return DASH;
  return `${Math.round((part / whole) * 100)}%`;
}

export function ago(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return DASH;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return DASH;
  const s = Math.max(0, Math.round((now - t) / 1000));
  if (s < 5) return 'just now';
  if (s < 60) return `${s} s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

export function clock(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? DASH : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}
