// Per-viewer conveniences only (collapsed sidebar, last tool tab). Browser storage can be empty, blocked or
// throw (private windows, cleared site data), so every access is guarded and the UI never depends on it.
// Keys carry the `fairlead-` prefix (docs/UNIFIED-UI.md section 9).
import { useCallback, useState } from 'react';

export const PREFIX = 'fairlead-';

export function readLocal<T>(key: string, fallback: T): T {
  try {
    const raw = window.localStorage.getItem(PREFIX + key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

export function writeLocal<T>(key: string, value: T): void {
  try {
    window.localStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    // ignore: storage unavailable
  }
}

export function useLocalState<T>(key: string, fallback: T): [T, (next: T) => void] {
  const [value, setValue] = useState<T>(() => readLocal(key, fallback));
  const set = useCallback((next: T) => {
    writeLocal(key, next);
    setValue(next);
  }, [key]);
  return [value, set];
}
