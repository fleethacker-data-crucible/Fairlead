// Config reads and writes. Every write returns the new config, which replaces the cached copy, and the
// status is refreshed so the shell and the overview agree with what was saved.
import { api, del, post } from './api';
import { Config } from './schemas';
import { invalidate, useResource } from './useResource';

export function useConfig() {
  return useResource('/api/config', Config);
}

async function saved(promise: Promise<unknown>): Promise<Config> {
  const raw = await promise;
  invalidate('/api/config');
  invalidate('/api/status');
  return Config.parse(raw);
}

export const saveProvider = (provider: Record<string, unknown>, apiKey?: string, clearKey?: boolean) => saved(post('/api/providers', { provider, apiKey: apiKey || null, clearKey: !!clearKey }));
export const deleteProvider = (id: string) => saved(del(`/api/providers/${encodeURIComponent(id)}`));
export const discoverProvider = (id: string) => saved(post(`/api/providers/${encodeURIComponent(id)}/discover`));
export const saveRoute = (route: Record<string, unknown>, previousId?: string) => saved(post('/api/routes', { route, previousId: previousId ?? null }));
export const deleteRoute = (id: string) => saved(del(`/api/routes/${encodeURIComponent(id)}`));
export const saveRouting = (body: Record<string, unknown>) => saved(post('/api/routing', body));
export const resetRouting = () => saved(post('/api/routing/reset'));
export const saveSettings = (body: Record<string, unknown>) => saved(post('/api/settings', body));
export const setKedge = async (mode: 'auto' | 'on' | 'off') => { await post('/api/kedge', { mode }); invalidate('/api/config'); invalidate('/api/status'); };

export type TestResult = { ok: boolean; ms: number; error?: string; kind?: string; models?: Array<{ id: string }>; keySource: string };
export const testProvider = (id: string) => post<TestResult>(`/api/providers/${encodeURIComponent(id)}/test`);

export async function createClient(name: string, routes: string[]): Promise<{ key: string }> {
  const r = await api<{ key: string }>('/api/clients', { method: 'POST', json: { name, routes } });
  invalidate('/api/config');
  invalidate('/api/status');
  return r;
}

export async function deleteClient(id: string): Promise<void> {
  await del(`/api/clients/${encodeURIComponent(id)}`);
  invalidate('/api/config');
  invalidate('/api/status');
}
