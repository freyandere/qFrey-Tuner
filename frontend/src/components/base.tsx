import { useEffect, useId, useRef, type ButtonHTMLAttributes, type ReactNode } from 'react';

export function Button({ children, ...props }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return <button type="button" {...props}>{children}</button>;
}
export function Field({ id, label, hint, error, children }: { id: string; label: string; hint?: string; error?: string; children: ReactNode }) {
  return <div className="field"><label htmlFor={id}>{label}</label>{children}
    {hint && <p id={`${id}-hint`} className="field-hint">{hint}</p>}
    {error && <p id={`${id}-error`} className="field-error" role="alert">{error}</p>}</div>;
}
export function Card({ title, children, className = '' }: { title: string; children: ReactNode; className?: string }) {
  const id = useId();
  return <section className={`card ${className}`.trim()} aria-labelledby={id}><h2 id={id}>{title}</h2>{children}</section>;
}
export function Badge({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'info' | 'warning' | 'error' }) {
  return <span className={`badge badge-${tone}`}>{children}</span>;
}
export function StatusBanner({ children, severity = 'info' }: { children: ReactNode; severity?: 'info' | 'warning' | 'error' }) {
  return <div className={`status-banner status-${severity}`} role={severity === 'error' ? 'alert' : 'status'}>{children}</div>;
}
export function Dialog({ open, title, onClose, children }: { open: boolean; title: string; onClose: () => void; children: ReactNode }) {
  const element = useRef<HTMLDialogElement>(null);
  const id = useId();
  useEffect(() => {
    const dialog = element.current;
    if (open && dialog && !dialog.open) dialog.showModal();
    else if (!open && dialog?.open) dialog.close();
  }, [open]);
  return <dialog ref={element} aria-labelledby={id} onCancel={onClose} onClose={onClose}><h2 id={id}>{title}</h2>{children}</dialog>;
}
export function Tabs({ label, items, selected, onSelect }: {
  label: string; items: readonly { id: string; label: string; panelId: string }[]; selected: string; onSelect: (id: string) => void;
}) {
  const list = useRef<HTMLDivElement>(null);
  return <div className="tabs" role="tablist" aria-label={label} ref={list} onKeyDown={event => {
    const index = items.findIndex(item => item.id === selected);
    const next = event.key === 'ArrowRight' ? (index + 1) % items.length : event.key === 'ArrowLeft' ? (index + items.length - 1) % items.length
      : event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : null;
    if (next === null || !items[next]) return;
    event.preventDefault(); onSelect(items[next].id); list.current?.querySelectorAll<HTMLButtonElement>('[role=tab]')[next]?.focus();
  }}>{items.map(item => <button key={item.id} id={`${item.id}-tab`} type="button" role="tab" aria-controls={item.panelId}
    aria-selected={selected === item.id} tabIndex={selected === item.id ? 0 : -1} onClick={() => onSelect(item.id)}>{item.label}</button>)}</div>;
}
