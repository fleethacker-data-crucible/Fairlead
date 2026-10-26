import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { LuActivity, LuAnchor, LuCompass, LuFlaskConical, LuKeyRound, LuLayoutDashboard, LuMonitor, LuMoon, LuPanelLeftClose, LuPanelLeftOpen, LuPower, LuServer, LuSettings2, LuSignpost, LuSun } from 'react-icons/lu';
import { post, sessionToken, startHeartbeat } from './lib/api';
import { Status } from './lib/schemas';
import { useLocalState } from './lib/storage';
import { useTheme, type ThemePref } from './lib/theme';
import { useResource } from './lib/useResource';
import { Button, ConfirmDialog, Dot, Emblem, Notice, Pill, ToastProvider, cn, useToast } from './components/ui';
import { ActivityPage } from './pages/activity';
import { ClientsPage } from './pages/clients';
import { DiagnosticsPage } from './pages/diagnostics';
import { OverviewPage } from './pages/overview';
import { PlaygroundPage } from './pages/playground';
import { ProvidersPage } from './pages/providers';
import { RoutesPage } from './pages/routes';
import { SettingsPage } from './pages/settings';
import { SetupPage } from './pages/setup';

export type Route = 'setup' | 'overview' | 'providers' | 'routes' | 'clients' | 'playground' | 'activity' | 'diagnostics' | 'settings';

const NAV: Array<{ route: Route; label: string; icon: ReactNode; group: 'gateway' | 'operate' }> = [
  { route: 'overview', label: 'Overview', icon: <LuLayoutDashboard size={20} />, group: 'gateway' },
  { route: 'providers', label: 'Providers', icon: <LuServer size={20} />, group: 'gateway' },
  { route: 'routes', label: 'Routes', icon: <LuSignpost size={20} />, group: 'gateway' },
  { route: 'clients', label: 'Clients', icon: <LuKeyRound size={20} />, group: 'gateway' },
  { route: 'playground', label: 'Playground', icon: <LuFlaskConical size={20} />, group: 'gateway' },
  { route: 'activity', label: 'Activity', icon: <LuActivity size={20} />, group: 'operate' },
  { route: 'setup', label: 'Setup', icon: <LuCompass size={20} />, group: 'operate' },
  { route: 'diagnostics', label: 'Diagnostics', icon: <LuAnchor size={20} />, group: 'operate' },
  { route: 'settings', label: 'Settings', icon: <LuSettings2 size={20} />, group: 'operate' },
];

export function parseHash(hash: string): Route | null {
  const raw = hash.replace(/^#\/?/, '').split('?')[0] ?? '';
  return NAV.find((n) => n.route === raw)?.route ?? null;
}

export function navigate(route: Route) {
  window.location.hash = `/${route}`;
}

function useHashRoute(): Route | null {
  const [state, setState] = useState(() => parseHash(window.location.hash));
  useEffect(() => {
    const onChange = () => setState(parseHash(window.location.hash));
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return state;
}

export function App() {
  return (
    <ToastProvider>
      <Shell />
    </ToastProvider>
  );
}

function Shell() {
  const hashRoute = useHashRoute();
  const status = useResource('/api/status', Status, { pollMs: 4000 });
  const toast = useToast();
  const [confirmStop, setConfirmStop] = useState(false);
  const [stopped, setStopped] = useState(false);
  const [collapsed, setCollapsed] = useLocalState('sidebar-collapsed', false);

  useEffect(() => startHeartbeat(), []);
  // First run: pin the setup page in the address so a refresh or a status reload keeps the operator there.
  const needsSetup = status.data ? !status.data.setupComplete : false;
  useEffect(() => { if (needsSetup && hashRoute === null) navigate('setup'); }, [needsSetup, hashRoute]);

  const endSession = useCallback(async () => {
    try {
      await post('/api/session/stop', {}, { 'X-Fairlead-Intent': 'end-session' });
      setStopped(true);
    } catch (e) {
      toast.push('err', e instanceof Error ? e.message : String(e));
    } finally {
      setConfirmStop(false);
    }
  }, [toast]);

  if (!sessionToken()) {
    return (
      <main className="mx-auto flex max-w-[60ch] flex-col items-center px-6 py-12 text-center">
        <Emblem size={280} />
        <h1 className="mt-4 text-[28px]">Open Fairlead from the tray icon</h1>
        <p className="mt-3 text-ink-2">This page has no session token, so Fairlead refuses its requests. Use <b>Open Fairlead</b> in the tray menu, or run <code>Launch-Fairlead.bat --open</code>. Your tools keep working either way; this page is only for setup.</p>
      </main>
    );
  }

  if (stopped) {
    return (
      <main className="mx-auto flex max-w-[60ch] flex-col items-center px-6 py-12 text-center">
        <Emblem size={280} />
        <h1 className="mt-4 text-[28px]">Fairlead stopped</h1>
        <p className="mt-3 text-ink-2">The gateway is no longer running, so tools pointed at it will fail until it starts again. Run <code>Launch-Fairlead.bat</code> to start it.</p>
      </main>
    );
  }

  const s = status.data;
  // First run: setup until it is finished, unless the operator navigated somewhere on purpose.
  const route: Route = hashRoute ?? (s && !s.setupComplete ? 'setup' : 'overview');
  const page = (() => {
    switch (route) {
      case 'setup': return <SetupPage status={s} refreshStatus={status.refetch} />;
      case 'providers': return <ProvidersPage status={s} />;
      case 'routes': return <RoutesPage />;
      case 'clients': return <ClientsPage status={s} />;
      case 'playground': return <PlaygroundPage />;
      case 'activity': return <ActivityPage />;
      case 'diagnostics': return <DiagnosticsPage />;
      case 'settings': return <SettingsPage status={s} refreshStatus={status.refetch} />;
      default: return <OverviewPage status={s} />;
    }
  })();

  const label = collapsed ? 'sr-only' : 'max-lg:sr-only';
  const centred = collapsed ? 'justify-center px-0' : 'max-lg:justify-center max-lg:px-0';
  return (
    <div className={cn('grid h-screen grid-rows-[minmax(0,1fr)_28px]', collapsed ? 'grid-cols-[64px_minmax(0,1fr)]' : 'grid-cols-[240px_minmax(0,1fr)] max-lg:grid-cols-[64px_minmax(0,1fr)]')}>
      <nav className="flex min-h-0 flex-col border-r border-line-2 bg-sidebar" aria-label="Fairlead">
        <div className={cn('flex-col items-center px-3 pt-3 pb-2', collapsed ? 'hidden' : 'flex max-lg:hidden')}>
          <Emblem size={168} />
          <div className="text-[12px] text-ink-3">Model router</div>
        </div>
        <div className={cn('justify-center py-4', collapsed ? 'flex' : 'hidden max-lg:flex')}>
          <img src="brand/icon-256.png" alt="Fairlead" width={40} height={40} />
        </div>
        <div className="scroll-y min-h-0 flex-1">
          <NavGroup label="Gateway" items={NAV.filter((n) => n.group === 'gateway')} route={route} collapsed={collapsed} />
          <NavGroup label="Operate" items={NAV.filter((n) => n.group === 'operate')} route={route} collapsed={collapsed} />
        </div>
        <div className="flex flex-col gap-1 border-t border-line-2 px-2 py-2">
          <ThemeSwitch className={centred} labelClass={label} />
          <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', centred)} onClick={() => setCollapsed(!collapsed)} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
            {collapsed ? <LuPanelLeftOpen size={16} /> : <LuPanelLeftClose size={16} />}<span className={label}>Collapse sidebar</span>
          </Button>
          <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', centred)} onClick={() => setConfirmStop(true)} title="End session: stop Fairlead">
            <LuPower size={16} /><span className={label}>End session</span>
          </Button>
        </div>
      </nav>
      <div className="flex min-h-0 min-w-0 flex-col">
        <ModeStrip status={s} error={status.error} />
        <main className="scroll-y min-h-0 min-w-0 flex-1">{page}</main>
      </div>
      <StatusBar status={s} error={status.error} />
      <ConfirmDialog open={confirmStop} title="Stop Fairlead?" confirmLabel="Stop Fairlead" danger onConfirm={endSession} onCancel={() => setConfirmStop(false)}>
        This stops the gateway, not just this page. Tools pointed at <code>{s?.gatewayUrl ?? '127.0.0.1:8776'}</code> fail until it starts again. To close only this page, close the tab: Fairlead keeps running in the tray.
      </ConfirmDialog>
    </div>
  );
}

function NavGroup({ label, items, route, collapsed }: { label: string; items: typeof NAV; route: Route; collapsed: boolean }) {
  const hide = collapsed ? 'sr-only' : 'max-lg:sr-only';
  return (
    <div className="pb-3">
      <div className={cn('px-4 pb-1 text-[12px] font-medium text-ink-3', hide)}>{label}</div>
      <ul className="fl-nav flex flex-col gap-px">
        {items.map((n) => (
          <li key={n.route}>
            <a
              href={`#/${n.route}`}
              aria-current={route === n.route ? 'page' : undefined}
              title={n.label}
              className={cn('flex min-h-9 items-center gap-3 px-4 text-ink-2 hover:bg-surface-2 hover:text-ink', route === n.route && 'bg-surface-2 font-medium', collapsed ? 'justify-center px-0' : 'max-lg:justify-center max-lg:px-0')}
            >
              {n.icon}
              <span className={hide}>{n.label}</span>
            </a>
          </li>
        ))}
      </ul>
    </div>
  );
}

const THEMES: Array<{ value: ThemePref; label: string; icon: ReactNode }> = [
  { value: 'system', label: 'System theme', icon: <LuMonitor size={16} /> },
  { value: 'dark', label: 'Night watch', icon: <LuMoon size={16} /> },
  { value: 'light', label: 'Chart room', icon: <LuSun size={16} /> },
];

function ThemeSwitch({ className, labelClass }: { className: string; labelClass: string }) {
  const [pref, setPref] = useTheme();
  const i = Math.max(0, THEMES.findIndex((t) => t.value === pref));
  const current = THEMES[i]!;
  const next = THEMES[(i + 1) % THEMES.length]!;
  return (
    <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', className)} onClick={() => setPref(next.value)} title={`Theme: ${current.label}. Switch to ${next.label}`}>
      {current.icon}<span className={labelClass}>{current.label}</span>
    </Button>
  );
}

function StatusBar({ status, error }: { status: Status | undefined; error: string | null }) {
  // UNIFIED-UI 5: engine state as a dot and a word, the bound address, session state, version.
  const state = status && !error ? 'Running' : status ? 'Reconnecting' : error ? 'Disconnected' : 'Connecting';
  return (
    <footer className="col-span-2 flex items-center gap-4 border-t border-line-2 bg-sidebar px-4 text-[12px] text-ink-2">
      <Dot tone={state === 'Running' ? 'ok' : state === 'Disconnected' ? 'err' : 'warn'}>{state}</Dot>
      <span className="mono">{window.location.host}</span>
      <span>{status ? (status.connectivity.kedge ? 'Kedge mode: local only' : status.connectivity.internet === 'up' ? 'Cloud + local' : status.connectivity.internet === 'down' ? 'Offline' : 'Checking network') : '—'}</span>
      <span className="ml-auto">Fairlead {status ? status.version : '—'}</span>
    </footer>
  );
}

function ModeStrip({ status, error }: { status: Status | undefined; error: string | null }) {
  if (error && !status) {
    return <Notice tone="err" className="m-3">Cannot reach Fairlead: {error}. If it was stopped, run Launch-Fairlead.bat.</Notice>;
  }
  if (!status) return <div className="h-9 border-b border-line-2 bg-surface" />;
  const c = status.connectivity;
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 border-b border-line-2 bg-surface px-6 py-1.5 text-[12px] text-ink-2">
      <span>Gateway <span className="mono text-ink">{status.gatewayUrl}</span></span>
      {c.kedge ? (
        <Pill tone="warn"><LuAnchor size={12} />Kedge mode {c.kedgeReason === 'forced' ? '(forced)' : '(internet down)'}: local models only</Pill>
      ) : c.internet === 'up' ? (
        <Pill tone="ok">Internet up</Pill>
      ) : c.internet === 'down' ? (
        <Pill tone="err">Internet down</Pill>
      ) : (
        <Pill>Checking network</Pill>
      )}
      {status.port !== status.preferredPort ? <Pill tone="warn">Port {status.preferredPort} was busy: running on {status.port}</Pill> : null}
      <span className="ml-auto">{status.metrics.lastHour.requests} requests in the last hour</span>
    </div>
  );
}
