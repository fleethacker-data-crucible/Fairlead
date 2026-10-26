// Small data hook: fetch, validate with a zod schema, cache by key, optional polling, manual refresh.
// A deliberate stand-in for TanStack Query on an offline bundle; the surface is the same shape
// (data / error / isLoading / refetch) so swapping it later is a one-file change.
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ZodType, ZodTypeDef } from 'zod';
import { api, describeError } from './api';

type Entry = { data: unknown; at: number };
const cache = new Map<string, Entry>();
const listeners = new Map<string, Set<() => void>>();

const stale = new Set<string>();

/** Marks matching keys stale and refetches them. The cached data stays on screen until the new data arrives. */
export function invalidate(prefix: string) {
  for (const key of [...cache.keys()]) {
    if (key === prefix || key.startsWith(prefix)) {
      stale.add(key);
      listeners.get(key)?.forEach((l) => l());
    }
  }
}

export type Resource<T> = {
  data: T | undefined;
  error: string | null;
  isLoading: boolean;
  isValidating: boolean;
  refetch: () => Promise<void>;
};

export function useResource<T>(path: string | null, schema: ZodType<T, ZodTypeDef, unknown>, options: { pollMs?: number; enabled?: boolean } = {}): Resource<T> {
  const enabled = options.enabled !== false && path !== null;
  const key = path ?? '';
  const [, bump] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [isValidating, setValidating] = useState(false);
  const alive = useRef(true);

  const load = useCallback(async () => {
    if (!enabled) return;
    setValidating(true);
    try {
      const raw = await api<unknown>(key);
      const parsed = schema.safeParse(raw);
      if (!parsed.success) {
        const issue = parsed.error.issues[0];
        throw new Error(`Unexpected response shape from ${key}: ${issue ? issue.path.join('.') + ' ' + issue.message : 'unknown'}`);
      }
      cache.set(key, { data: parsed.data, at: Date.now() });
      if (alive.current) setError(null);
      listeners.get(key)?.forEach((l) => l());
    } catch (e) {
      if (alive.current) setError(describeError(e));
    } finally {
      if (alive.current) setValidating(false);
    }
  }, [key, enabled, schema]);

  useEffect(() => {
    alive.current = true;
    // When another caller invalidates this key, reload instead of showing a skeleton forever.
    const listener = () => {
      bump((n) => n + 1);
      if (enabled && (stale.has(key) || !cache.has(key))) { stale.delete(key); void load(); }
    };
    if (!listeners.has(key)) listeners.set(key, new Set());
    listeners.get(key)!.add(listener);
    if (enabled && !cache.has(key)) void load();
    let timer: ReturnType<typeof setInterval> | undefined;
    if (enabled && options.pollMs) timer = setInterval(() => void load(), options.pollMs);
    return () => {
      alive.current = false;
      listeners.get(key)?.delete(listener);
      if (timer) clearInterval(timer);
    };
  }, [key, enabled, load, options.pollMs]);

  const entry = cache.get(key);
  return {
    data: enabled ? (entry?.data as T | undefined) : undefined,
    error,
    isLoading: enabled && !entry && !error,
    isValidating,
    refetch: load,
  };
}
