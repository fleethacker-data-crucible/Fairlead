import { cva, type VariantProps } from 'class-variance-authority';
import { clsx, type ClassValue } from 'clsx';
import type React from 'react';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';
import { twMerge } from 'tailwind-merge';
import { LuCheck, LuChevronDown, LuCircleAlert, LuCopy, LuInfo, LuLoaderCircle, LuX } from 'react-icons/lu';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

// ---- buttons ----

const button = cva(
  'inline-flex items-center justify-center gap-1.5 rounded-ctl border text-[13px] font-medium whitespace-nowrap select-none transition-colors disabled:opacity-50 disabled:cursor-not-allowed',
  {
    variants: {
      variant: {
        primary: 'bg-accent text-accent-ink border-accent hover:brightness-110',
        default: 'bg-surface text-ink border-line hover:bg-surface-2',
        quiet: 'bg-transparent text-ink-2 border-transparent hover:bg-surface-2 hover:text-ink',
        danger: 'bg-surface text-err border-err/40 hover:bg-err-bg',
      },
      size: {
        sm: 'h-7 px-2.5',
        md: 'h-8 px-3',
        lg: 'h-9 px-4 text-sm',
      },
    },
    defaultVariants: { variant: 'default', size: 'md' },
  },
);

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & VariantProps<typeof button> & { busy?: boolean };

export function Button({ className, variant, size, busy, children, disabled, ...rest }: ButtonProps) {
  return (
    <button type="button" className={cn(button({ variant, size }), className)} disabled={disabled || busy} {...rest}>
      {busy ? <LuLoaderCircle className="animate-spin" size={14} aria-hidden /> : null}
      {children}
    </button>
  );
}

// ---- status pill ----

const pill = cva('inline-flex items-center gap-1 rounded-full px-2 py-[1px] text-[11.5px] font-medium', {
  variants: {
    tone: {
      ok: 'bg-ok-bg text-ok',
      warn: 'bg-warn-bg text-warn',
      err: 'bg-err-bg text-err',
      info: 'bg-info-bg text-info',
      neutral: 'bg-surface-2 text-ink-2',
      accent: 'bg-surface-2 text-accent',
    },
  },
  defaultVariants: { tone: 'neutral' },
});

export function Pill({ tone, className, children }: VariantProps<typeof pill> & { className?: string; children: ReactNode }) {
  return <span className={cn(pill({ tone }), className)}>{children}</span>;
}

// ---- layout primitives ----

export function Page({ title, lede, actions, children }: { title: string; lede?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return (
    <div className="mx-auto flex w-full max-w-[1400px] flex-col gap-5 px-6 py-5">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div className="max-w-[70ch]">
          <h1>{title}</h1>
          {lede ? <p className="mt-1 text-ink-2">{lede}</p> : null}
        </div>
        {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
      </header>
      {children}
    </div>
  );
}

export function Section({ title, aside, children, className }: { title?: ReactNode; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={cn('panel', className)}>
      {title !== undefined ? (
        <div className="flex items-center justify-between gap-3 px-4 py-2.5 hairline first:border-t-0">
          <h2 className="text-[14px]">{title}</h2>
          {aside ? <div className="flex items-center gap-2 text-ink-2">{aside}</div> : null}
        </div>
      ) : null}
      <div className={cn(title !== undefined && 'hairline')}>{children}</div>
    </section>
  );
}

export function Emblem({ size }: { size: number }) {
  // The full Halyard emblem (assets/brand/emblem-*.png with the backdrop made transparent), one per theme.
  return (
    <>
      <img src="brand/emblem-light.png" alt="Halyard" width={size} height={size} className="only-light" />
      <img src="brand/emblem-dark.png" alt="Halyard" width={size} height={size} className="only-dark" />
    </>
  );
}

export function Empty({ title, children, action, emblem }: { title: string; children?: ReactNode; action?: ReactNode; emblem?: boolean }) {
  if (emblem) {
    // First-run empty state (UNIFIED-UI 6, .fl-empty): emblem, display-serif headline, one primary action.
    return (
      <div className="flex flex-col items-center gap-3 px-4 py-8 text-center text-ink-2">
        <Emblem size={240} />
        <h2 className="font-display text-[28px] font-semibold text-ink">{title}</h2>
        {children ? <div className="max-w-[60ch]">{children}</div> : null}
        {action ? <div className="mt-1">{action}</div> : null}
      </div>
    );
  }
  return (
    <div className="flex flex-col items-start gap-2 px-4 py-8 text-ink-2">
      <p className="font-medium text-ink">{title}</p>
      {children ? <div className="max-w-[60ch]">{children}</div> : null}
      {action ? <div className="mt-1">{action}</div> : null}
    </div>
  );
}

export function Notice({ tone = 'info', children, className }: { tone?: 'info' | 'warn' | 'err' | 'ok'; children: ReactNode; className?: string }) {
  const tones = { info: 'bg-info-bg text-info', warn: 'bg-warn-bg text-warn', err: 'bg-err-bg text-err', ok: 'bg-ok-bg text-ok' };
  return <div className={cn('rounded-ctl px-3 py-2 text-[12.5px]', tones[tone], className)} role={tone === 'err' ? 'alert' : 'status'}>{children}</div>;
}

export function Stat({ label, value, hint, accent }: { label: string; value: ReactNode; hint?: ReactNode; accent?: string }) {
  return (
    <div className="flex flex-col gap-0.5 px-4 py-3">
      <span className="text-ink-2">{label}</span>
      <span className="num text-[22px] font-semibold leading-tight" style={accent ? { color: accent } : undefined}>{value}</span>
      {hint ? <span className="text-[12px] text-ink-3">{hint}</span> : null}
    </div>
  );
}

// ---- form controls ----

export function Field({ label, hint, children, className }: { label: string; hint?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <label className={cn('flex flex-col gap-1', className)}>
      <span className="text-ink-2">{label}</span>
      {children}
      {hint ? <span className="text-[12px] text-ink-3">{hint}</span> : null}
    </label>
  );
}

const control = 'h-8 w-full rounded-ctl border border-line bg-surface px-2.5 text-[13px] text-ink placeholder:text-ink-3 focus:border-focus';

export function Input(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={cn(control, props.className)} />;
}

export function Select({ className, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <span className="relative block">
      <select {...rest} className={cn(control, 'appearance-none pr-7', className)}>{children}</select>
      <LuChevronDown size={14} className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 text-ink-3" aria-hidden />
    </span>
  );
}

export function Checkbox({ label, ...rest }: InputHTMLAttributes<HTMLInputElement> & { label: ReactNode }) {
  return (
    <label className="inline-flex items-center gap-2 text-ink">
      <input type="checkbox" {...rest} className="h-3.5 w-3.5 accent-[var(--accent)]" />
      <span>{label}</span>
    </label>
  );
}

export function Progress({ done, total, label }: { done: number; total: number; label?: string | null }) {
  const pctValue = total > 0 ? Math.min(100, Math.round((done / total) * 100)) : null;
  return (
    <div className="flex flex-col gap-1">
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-surface-2">
        <div className={cn('h-full bg-accent transition-[width]', pctValue === null && 'w-1/3 animate-pulse')} style={pctValue === null ? undefined : { width: `${pctValue}%` }} />
      </div>
      {label ? <span className="text-[12px] text-ink-2">{total > 0 ? `${done}/${total} · ` : ''}{label}</span> : null}
    </div>
  );
}

// ---- tabs ----

export function Tabs<T extends string>({ value, onChange, items }: { value: T; onChange: (v: T) => void; items: Array<{ value: T; label: ReactNode; count?: number }> }) {
  return (
    <div role="tablist" className="flex gap-1 border-b border-line-2 px-2">
      {items.map((it) => (
        <button
          key={it.value}
          role="tab"
          type="button"
          aria-selected={it.value === value}
          onClick={() => onChange(it.value)}
          className={cn('-mb-px flex items-center gap-1.5 border-b-2 px-3 py-2 text-[13px]', it.value === value ? 'border-accent text-ink' : 'border-transparent text-ink-2 hover:text-ink')}
        >
          {it.label}
          {it.count !== undefined ? <span className="num rounded-full bg-surface-2 px-1.5 text-[11px] text-ink-2">{it.count}</span> : null}
        </button>
      ))}
    </div>
  );
}

// ---- toasts ----

type Toast = { id: number; tone: 'ok' | 'err' | 'info'; text: string };
const ToastContext = createContext<{ push: (tone: Toast['tone'], text: string) => void }>({ push: () => {} });

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const counter = useRef(0);
  const push = useCallback((tone: Toast['tone'], text: string) => {
    const id = ++counter.current;
    setToasts((t) => [...t, { id, tone, text }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), tone === 'err' ? 9000 : 4500);
  }, []);
  const value = useMemo(() => ({ push }), [push]);
  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="pointer-events-none fixed bottom-4 right-4 z-50 flex w-[360px] max-w-[calc(100vw-2rem)] flex-col gap-2" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={cn('pointer-events-auto flex items-start gap-2 rounded-panel border px-3 py-2 text-[12.5px] shadow-none', t.tone === 'err' ? 'border-err/40 bg-err-bg text-err' : t.tone === 'ok' ? 'border-ok/40 bg-ok-bg text-ok' : 'border-line bg-surface text-ink')}>
            {t.tone === 'ok' ? <LuCheck size={14} className="mt-0.5 shrink-0" /> : t.tone === 'err' ? <LuCircleAlert size={14} className="mt-0.5 shrink-0" /> : <LuInfo size={14} className="mt-0.5 shrink-0" />}
            <span className="flex-1">{t.text}</span>
            <button type="button" aria-label="Dismiss" onClick={() => setToasts((all) => all.filter((x) => x.id !== t.id))} className="opacity-70 hover:opacity-100"><LuX size={14} /></button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  return useContext(ToastContext);
}

// ---- confirm dialog (native <dialog>; no portal library needed) ----

export function ConfirmDialog({ open, title, children, confirmLabel, danger, onConfirm, onCancel, confirmDisabled }: {
  open: boolean; title: string; children: ReactNode; confirmLabel: string; danger?: boolean; onConfirm: () => void; onCancel: () => void; confirmDisabled?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (open && !el.open) el.showModal();
    if (!open && el.open) el.close();
  }, [open]);
  return (
    <dialog ref={ref} onClose={onCancel} className="m-auto w-[480px] max-w-[calc(100vw-2rem)] rounded-dialog border border-line bg-surface p-0 text-ink backdrop:bg-black/40">
      <div className="flex flex-col gap-3 p-5">
        <h2>{title}</h2>
        <div className="text-ink-2">{children}</div>
        <div className="mt-2 flex justify-end gap-2">
          <Button variant="quiet" onClick={onCancel}>Cancel</Button>
          <Button variant={danger ? 'danger' : 'primary'} onClick={onConfirm} disabled={confirmDisabled}>{confirmLabel}</Button>
        </div>
      </div>
    </dialog>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="rounded border border-line bg-surface-2 px-1 text-[11px]">{children}</kbd>;
}

export function Mono({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn('mono text-ink-2', className)}>{children}</span>;
}

export function Textarea(props: React.TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea {...props} className={cn('w-full rounded-ctl border border-line bg-surface px-2.5 py-2 text-[13px] text-ink placeholder:text-ink-3 focus:border-focus', props.className)} />;
}

/** Status in words with a dot: colour is never the only signal (UNIFIED-UI 7). */
export function Dot({ tone, children }: { tone: 'ok' | 'warn' | 'err' | 'neutral'; children: ReactNode }) {
  const colour = tone === 'ok' ? 'bg-ok' : tone === 'warn' ? 'bg-warn' : tone === 'err' ? 'bg-err' : 'bg-ink-3';
  return <span className="inline-flex items-center gap-1.5"><span className={cn('inline-block h-2 w-2 rounded-full', colour)} aria-hidden />{children}</span>;
}

export function CopyButton({ text, label = 'Copy' }: { text: string; label?: string }) {
  const toast = useToast();
  return (
    <Button size="sm" variant="quiet" onClick={() => { void navigator.clipboard.writeText(text).then(() => toast.push('ok', 'Copied.'), () => toast.push('err', 'The browser blocked the clipboard; select the text and copy it.')); }}>
      <LuCopy size={14} />{label}
    </Button>
  );
}
