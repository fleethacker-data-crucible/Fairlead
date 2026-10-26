// Night watch / Chart room (docs/UNIFIED-UI.md 3.1): follow the system unless the viewer picks one.
// The choice is kept under `fairlead-theme`; storage can be blocked, so every access is guarded and
// the page still renders with the system theme.
import { useCallback, useState } from 'react';

export type ThemePref = 'system' | 'dark' | 'light';

const KEY = 'fairlead-theme';

function readPref(): ThemePref {
  try {
    const v = window.localStorage.getItem(KEY);
    return v === 'dark' || v === 'light' ? v : 'system';
  } catch {
    return 'system';
  }
}

export function applyTheme(pref: ThemePref = readPref()): void {
  const root = document.documentElement;
  if (pref === 'system') delete root.dataset.theme;
  else root.dataset.theme = pref;
}

export function useTheme(): [ThemePref, (next: ThemePref) => void] {
  const [pref, setPref] = useState<ThemePref>(readPref);
  const set = useCallback((next: ThemePref) => {
    try {
      if (next === 'system') window.localStorage.removeItem(KEY);
      else window.localStorage.setItem(KEY, next);
    } catch {
      // storage unavailable: the choice lasts for this page only
    }
    applyTheme(next);
    setPref(next);
  }, []);
  return [pref, set];
}
