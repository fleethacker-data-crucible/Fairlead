import { useState } from 'react';
import { LuKeyRound, LuRefreshCw, LuUnlink } from 'react-icons/lu';
import { describeError } from '../lib/api';
import { pairLazarette, refreshLazarette, unpairLazarette, useLazarette } from '../lib/config';
import { Button, ConfirmDialog, Field, Input, Notice, Pill, Section, useToast } from './ui';

const stateText: Record<string, { tone: 'ok' | 'warn' | 'err' | 'info'; text: string }> = {
  ok: { tone: 'ok', text: 'Connected' },
  unknown: { tone: 'info', text: 'Checking' },
  idle: { tone: 'info', text: 'Paired' },
  locked: { tone: 'warn', text: 'Lazarette is locked' },
  'not-running': { tone: 'warn', text: 'Lazarette is not open' },
  'not-paired': { tone: 'info', text: 'Not paired' },
  'peer-unverified': { tone: 'err', text: 'Lazarette could not verify Fairlead' },
  'rate-limited': { tone: 'err', text: 'Lazarette is pausing requests' },
  error: { tone: 'err', text: 'Problem' },
};

const nameText: Record<string, string> = {
  ok: 'shared',
  'not-stowed': 'not shared with Fairlead',
  empty: 'shared, but empty',
  unknown: 'not asked yet',
};

/**
 * Fairlead's link to Lazarette, the suite's vault. Tokens and API keys live there; Fairlead asks for them by
 * variable name and keeps them in memory only. The pairing code is entered once and never shown again.
 */
export function LazaretteCard() {
  const toast = useToast();
  const lz = useLazarette();
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmUnpair, setConfirmUnpair] = useState(false);
  const v = lz.data;

  const run = async (what: string, f: () => Promise<unknown>) => {
    setBusy(what);
    try { await f(); } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(null); }
  };
  const st = v ? stateText[v.state] ?? stateText.error : null;
  const offPort = v && v.port !== v.registryPort;

  return (
    <Section title={<span className="flex items-center gap-2"><LuKeyRound size={14} aria-hidden />Lazarette (keys and tokens)</span>}
      aside={v?.paired ? <>
        <Button size="sm" busy={busy === 'refresh'} onClick={() => void run('refresh', refreshLazarette)}><LuRefreshCw size={14} />Refresh</Button>
        <Button size="sm" variant="danger" onClick={() => setConfirmUnpair(true)}><LuUnlink size={14} />Remove pairing</Button>
      </> : undefined}>
      <div className="flex flex-col gap-3 px-4 py-4 text-[13px]">
        {lz.error ? <Notice tone="err">{lz.error}</Notice> : null}
        {!v ? null : !v.paired ? (
          <>
            <p className="text-ink-2">Tokens and API keys stay in Lazarette, your encrypted vault. In Lazarette open <b>Settings, Fairlead</b>, press <b>Pair</b>, and paste the code here. Then share each secret with Fairlead under the name Fairlead asks for (for example <span className="mono">DATABRICKS_TOKEN</span>).</p>
            <div className="flex flex-wrap items-end gap-2">
              <Field label="Pairing code from Lazarette" className="min-w-[280px] flex-1">
                <Input type="password" autoComplete="off" spellCheck={false} className="mono" value={code} placeholder="lzfl1." onChange={(e) => setCode(e.target.value)} />
              </Field>
              <Button variant="primary" disabled={!code.trim()} busy={busy === 'pair'} onClick={() => void run('pair', async () => { await pairLazarette(code.trim()); setCode(''); toast.push('ok', 'Paired with Lazarette.'); })}>Pair</Button>
            </div>
            {v.message ? <Notice tone="warn">{v.message}</Notice> : null}
          </>
        ) : (
          <>
            <p className="flex flex-wrap items-center gap-2">
              <span>Paired <span className="mono text-ink-3">{v.pairing}</span>.</span>
              {st ? <Pill className={st.tone === 'ok' ? 'text-ok' : st.tone === 'warn' ? 'text-warn' : st.tone === 'err' ? 'text-err' : ''}>{st.text}</Pill> : null}
              {v.lastChecked ? <span className="text-[12px] text-ink-3">checked {new Date(v.lastChecked).toLocaleTimeString()}</span> : null}
            </p>
            {v.message ? <Notice tone={st?.tone === 'err' ? 'err' : 'warn'}>{v.message}</Notice> : null}
            {offPort ? <Notice tone="warn">Fairlead is on port {v.port}, not {v.registryPort}. Lazarette only answers Fairlead on {v.registryPort}; free that port and restart Fairlead.</Notice> : null}
            {v.names.length > 0 ? (
              <table className="table-dense w-full">
                <thead><tr><th>Name Fairlead asks for</th><th>In Lazarette</th></tr></thead>
                <tbody>{v.names.map((n) => <tr key={n.name}><td className="mono">{n.name}</td><td className={n.state === 'ok' ? 'text-ok' : n.state === 'unknown' ? 'text-ink-3' : 'text-warn'}>{nameText[n.state] ?? n.state}</td></tr>)}</tbody>
              </table>
            ) : null}
            <p className="text-[12px] text-ink-3">
              Values are held in memory only, never on disk or shown here.{' '}
              {v.holdHours > 0 ? `If Lazarette locks or closes they are kept for ${v.holdHours} h after it last confirmed them` : 'They are forgotten as soon as Lazarette locks or closes'}
              {v.expiresAt && v.state !== 'ok' ? ` (until ${new Date(v.expiresAt).toLocaleTimeString()})` : ''}; change this under Settings.
            </p>
          </>
        )}
      </div>
      <ConfirmDialog open={confirmUnpair} danger title="Remove the Lazarette pairing?" confirmLabel="Remove pairing" onCancel={() => setConfirmUnpair(false)}
        onConfirm={() => { setConfirmUnpair(false); void run('unpair', unpairLazarette); }}>
        Fairlead forgets the pairing and every key it got from Lazarette. Providers that used them stop until you pair again. Remove the pairing in Lazarette too.
      </ConfirmDialog>
    </Section>
  );
}
