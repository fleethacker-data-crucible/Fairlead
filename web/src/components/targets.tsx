import { useState } from 'react';
import { LuArrowDown, LuArrowUp, LuCloud, LuHardDrive, LuPlus, LuX } from 'react-icons/lu';
import type { Config, ModelInfo } from '../lib/schemas';
import { Button, Select } from './ui';

export type TargetOption = { ref: string; provider: string; model: string; local: boolean; info?: ModelInfo };

/** Every provider/model Fairlead knows: configured models plus what discovery found, chat models only. */
export function targetOptions(config: Config | undefined, includeEmbeddings = false): TargetOption[] {
  if (!config) return [];
  const out: TargetOption[] = [];
  for (const p of config.providers) {
    if (!p.enabled) continue;
    const seen = new Set<string>();
    for (const m of [...p.models, ...p.discovered]) {
      const key = m.id.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      if (m.embedding && !includeEmbeddings) continue;
      out.push({ ref: `${p.id}/${m.id}`, provider: p.id, model: m.id, local: p.local, info: m });
    }
  }
  return out;
}

export function describeModel(info: ModelInfo | undefined): string {
  if (!info) return '';
  const bits: string[] = [];
  if (info.contextWindow) bits.push(`${Math.round(info.contextWindow / 1000)}k context`);
  if (info.tools === true) bits.push('tools');
  if (info.tools === false) bits.push('no tools');
  if (info.vision) bits.push('vision');
  return bits.join(' · ');
}

/** An ordered list of targets: first is tried first. Add, remove, move up and down. */
export function TargetList({ value, onChange, options, localOnly }: { value: string[]; onChange: (next: string[]) => void; options: TargetOption[]; localOnly?: boolean }) {
  const [pick, setPick] = useState('');
  const available = options.filter((o) => !value.includes(o.ref) && (!localOnly || o.local));
  const byRef = new Map(options.map((o) => [o.ref, o]));
  const move = (i: number, d: number) => {
    const next = [...value];
    const [item] = next.splice(i, 1);
    next.splice(i + d, 0, item!);
    onChange(next);
  };
  return (
    <div className="flex flex-col gap-1.5">
      {value.length === 0 ? <div className="text-[12.5px] text-ink-3">No targets: this route falls back to local models.</div> : null}
      <ol className="flex flex-col gap-1">
        {value.map((ref, i) => {
          const o = byRef.get(ref);
          return (
            <li key={ref} className="flex items-center gap-2 rounded-ctl border border-line-2 bg-surface px-2 py-1">
              <span className="num w-5 text-right text-ink-3">{i + 1}</span>
              {o?.local ? <LuHardDrive size={14} className="text-ink-3" aria-label="local" /> : <LuCloud size={14} className="text-ink-3" aria-label="cloud" />}
              <span className="flex min-w-0 flex-1 flex-col"><span className="mono truncate" title={ref}>{ref}</span><span className="truncate text-[11.5px] text-ink-3">{o ? describeModel(o.info) || 'capabilities unknown' : 'not found on the provider'}</span></span>
              <Button size="sm" variant="quiet" aria-label="Move up" disabled={i === 0} onClick={() => move(i, -1)}><LuArrowUp size={14} /></Button>
              <Button size="sm" variant="quiet" aria-label="Move down" disabled={i === value.length - 1} onClick={() => move(i, 1)}><LuArrowDown size={14} /></Button>
              <Button size="sm" variant="quiet" aria-label="Remove" onClick={() => onChange(value.filter((v) => v !== ref))}><LuX size={14} /></Button>
            </li>
          );
        })}
      </ol>
      <div className="flex gap-2">
        <Select value={pick} onChange={(e) => setPick(e.target.value)} aria-label="Add a target">
          <option value="">{available.length ? 'Add a model…' : 'No more models (add providers first)'}</option>
          {available.map((o) => <option key={o.ref} value={o.ref}>{o.local ? 'Local · ' : 'Cloud · '}{o.ref}{o.info?.contextWindow ? ` (${Math.round(o.info.contextWindow / 1000)}k)` : ''}</option>)}
        </Select>
        <Button disabled={!pick} onClick={() => { onChange([...value, pick]); setPick(''); }}><LuPlus size={14} />Add</Button>
      </div>
    </div>
  );
}
