// Keyboard and id helpers for the tab pattern (WAI-ARIA tabs). Pure so the movement rules
// can be tested without a browser. The Tabs component applies them.

export type TabMove = 'next' | 'prev' | 'first' | 'last';

/** Horizontal tab set: Left/Right and Up/Down move, Home/End jump. Any other key, including Tab, leaves the set. */
export function tabMove(key: string): TabMove | null {
  switch (key) {
    case 'ArrowRight':
    case 'ArrowDown':
      return 'next';
    case 'ArrowLeft':
    case 'ArrowUp':
      return 'prev';
    case 'Home':
      return 'first';
    case 'End':
      return 'last';
    default:
      return null;
  }
}

export function tabIndexAfter(move: TabMove, index: number, count: number): number {
  if (count <= 0) return 0;
  if (move === 'first') return 0;
  if (move === 'last') return count - 1;
  if (move === 'next') return (index + 1) % count;
  return (index - 1 + count) % count;
}

/** Stable ids shared by a tab and the panel it controls. */
export function tabDomIds(setId: string, value: string): { tab: string; panel: string } {
  const safe = value.replace(/[^A-Za-z0-9_-]/g, '_');
  return { tab: `${setId}-tab-${safe}`, panel: `${setId}-panel-${safe}` };
}
