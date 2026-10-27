// Config reads and writes. Every write returns the new config, which replaces the cached copy, and the
// status is refreshed so the shell and the overview agree with what was saved.
import { api, del, post } from './api';
import { Config, GatewayView, LazaretteView } from './schemas';
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

export type ImportResult = { saved: boolean; errors?: string[]; warnings?: string[]; gateway?: GatewayView };

/** Sends the chosen model file; refused files come back with their problems and nothing is saved. */
export async function importGatewayFile(yaml: string, fileName: string, values?: Record<string, string>): Promise<ImportResult> {
  const r = await post<ImportResult>('/api/gateway/import', { yaml, fileName, values: values ?? null });
  invalidate('/api/config');
  invalidate('/api/status');
  return r;
}

export async function saveGatewayValues(values: Record<string, string>): Promise<GatewayView> {
  const r = await post<unknown>('/api/gateway/values', { values });
  invalidate('/api/config');
  invalidate('/api/status');
  return GatewayView.parse(r);
}

/** Removes one imported model file, or every one (and the saved address) when no name is given. */
export async function removeGatewayFile(name?: string): Promise<void> {
  await del(name ? `/api/gateway/${encodeURIComponent(name)}` : '/api/gateway');
  invalidate('/api/config');
  invalidate('/api/status');
}

export function useLazarette() {
  return useResource('/api/lazarette', LazaretteView);
}

async function lazarette(promise: Promise<unknown>): Promise<LazaretteView> {
  const raw = await promise;
  invalidate('/api/lazarette');
  invalidate('/api/config');
  invalidate('/api/status');
  return LazaretteView.parse(raw);
}

export const pairLazarette = (code: string) => lazarette(post('/api/lazarette/pair', { code }));
export const unpairLazarette = () => lazarette(del('/api/lazarette/pair'));
export const refreshLazarette = () => lazarette(post('/api/lazarette/refresh'));

export type CurlImportResult = ImportResult & { fileName?: string; target?: string; format?: string; tokenVariable?: string };

/** Builds a gateway from a pasted curl command (URL, format and model from it; the key only by its variable name). */
export async function importGatewayCurl(curl: string, tokenVariable?: string): Promise<CurlImportResult> {
  const r = await post<CurlImportResult>('/api/gateway/curl', { curl, tokenVariable: tokenVariable || null });
  invalidate('/api/config');
  invalidate('/api/status');
  invalidate('/api/lazarette');
  return r;
}

export type GatewayTest = { ok: boolean; verdict: string; message: string; status?: number | null; detail?: string | null; text?: string | null; ms?: number | null };

/** Sends one short prompt straight to provider/model and explains what failed. */
export const testGatewayModel = (target: string) => post<GatewayTest>('/api/gateway/test', { target });
