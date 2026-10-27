import { useState } from 'react';
import { LuSend } from 'react-icons/lu';
import { describeError } from '../lib/api';
import { testGatewayModel, type GatewayTest } from '../lib/config';
import { Button, Notice } from './ui';

/**
 * Sends one short prompt straight to a gateway model and shows, in plain words, which part works: no key, key
 * rejected, wrong address, model or request refused, unreachable, or all fine. The key is never shown.
 */
export function GatewayTestButton({ target }: { target: string }) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<GatewayTest | null>(null);
  const run = async () => {
    setBusy(true);
    try { setResult(await testGatewayModel(target)); }
    catch (e) { setResult({ ok: false, verdict: 'error', message: describeError(e) }); }
    finally { setBusy(false); }
  };
  return (
    <div className="flex flex-col gap-2">
      <div><Button size="sm" busy={busy} onClick={() => void run()}><LuSend size={14} />Send a test prompt to <span className="mono">{target}</span></Button></div>
      {result ? (
        <Notice tone={result.ok ? 'ok' : result.verdict === 'rate-limited' ? 'warn' : 'err'}>
          <span className="font-medium">{result.message}</span>
          {result.ok && result.text ? <span className="block text-[12px]">Answer: {result.text}</span> : null}
          {!result.ok && result.detail ? <span className="mono block text-[12px] break-all">{result.detail}</span> : null}
        </Notice>
      ) : null}
    </div>
  );
}
