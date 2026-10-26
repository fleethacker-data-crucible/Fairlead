// Shapes of the admin API. Every response is validated before the UI reads it; fields the engine may add
// later pass through untouched, so an older UI never breaks on a newer engine.
import { z } from 'zod';

const n = z.number().nullable().optional();
const s = z.string().nullable().optional();
const b = z.boolean().nullable().optional();

export const Connectivity = z.object({
  internet: z.string(),
  kedge: z.boolean(),
  kedgeReason: s,
  mode: z.string(),
  since: s,
  lastProbe: s,
  reached: z.array(z.string()).default([]),
  osReportsNetwork: z.boolean().default(true),
}).passthrough();
export type Connectivity = z.infer<typeof Connectivity>;

export const TargetStats = z.object({
  target: z.string(),
  requests: z.number(),
  failures: z.number(),
  ewmaFirstByteMs: n,
  ewmaTotalMs: n,
  inputTokens: z.number(),
  outputTokens: z.number(),
  cost: z.number(),
  lastUsed: s,
}).passthrough();
export type TargetStats = z.infer<typeof TargetStats>;

export const MetricsSummary = z.object({
  since: z.string(),
  total: z.number(),
  failed: z.number(),
  failovers: z.number(),
  servedLocally: z.number(),
  kedge: z.number(),
  sensitive: z.number(),
  lastHour: z.object({
    requests: z.number(),
    failed: z.number(),
    inputTokens: z.number(),
    outputTokens: z.number(),
    cost: n,
    p50Ms: n,
    p95Ms: n,
    p50FirstByteMs: n,
  }).passthrough(),
  targets: z.array(TargetStats),
}).passthrough();
export type MetricsSummary = z.infer<typeof MetricsSummary>;

export const GatewayVariable = z.object({
  name: z.string(),
  kind: z.enum(['baseUrl', 'token', 'other']),
  source: z.enum(['stored', 'lazarette', 'env', 'missing']),
});
export type GatewayVariable = z.infer<typeof GatewayVariable>;

/** The AI gateway model file: keys, counts, problems and where each variable comes from; never values. */
export const GatewayView = z.object({
  present: z.boolean().default(false),
  canImport: z.boolean().default(true),
  imported: z.boolean().default(false),
  source: z.enum(['imported', 'environment', 'file']).nullable().optional(),
  path: z.string().optional(),
  ok: z.boolean().default(false),
  endpoints: z.array(z.string()).default([]),
  models: z.number().default(0),
  enabledModels: z.number().default(0),
  providers: z.array(z.string()).default([]),
  variables: z.array(GatewayVariable).default([]),
  errors: z.array(z.string()).default([]),
  warnings: z.array(z.string()).default([]),
}).passthrough();
export type GatewayView = z.infer<typeof GatewayView>;

export const Status = z.object({
  version: z.string(),
  port: z.number(),
  registryPort: z.number(),
  preferredPort: z.number(),
  gatewayUrl: z.string(),
  startedAt: z.string(),
  autoStop: z.boolean(),
  setupComplete: z.boolean(),
  requireClientKey: z.boolean(),
  clients: z.number(),
  configFile: s,
  logFile: s,
  connectivity: Connectivity,
  providers: z.array(z.object({
    id: z.string(),
    name: z.string(),
    local: z.boolean(),
    enabled: z.boolean(),
    reachable: b,
    authOk: b,
    probeMs: n,
    lastError: s,
    models: z.number(),
    breaker: z.string(),
    keySource: z.string(),
  }).passthrough()),
  routes: z.array(z.object({ id: z.string(), strategy: z.string(), targets: z.number() }).passthrough()),
  gatewayModels: GatewayView.nullable().optional(),
  metrics: MetricsSummary,
}).passthrough();
export type Status = z.infer<typeof Status>;

export const ModelInfo = z.object({
  id: z.string(),
  contextWindow: n,
  tools: b,
  vision: b,
  embedding: z.boolean().default(false),
  inputPricePerMTok: n,
  outputPricePerMTok: n,
  discovered: z.boolean().default(false),
}).passthrough();
export type ModelInfo = z.infer<typeof ModelInfo>;

export const Provider = z.object({
  id: z.string(),
  name: z.string(),
  kind: z.enum(['openAi', 'anthropic', 'ollama', 'responses']),
  preset: s,
  baseUrl: z.string(),
  local: z.boolean(),
  enabled: z.boolean(),
  apiKeyEnv: s,
  timeoutSeconds: z.number(),
  headers: z.record(z.string()).default({}),
  streamUsage: z.boolean().default(true),
  models: z.array(ModelInfo),
  discovered: z.array(ModelInfo).default([]),
  keySource: z.enum(['stored', 'lazarette', 'env', 'none']),
  keyHint: s,
  /** From the AI gateway model file: shown, never edited here. */
  managed: z.boolean().default(false),
  path: s,
  retries: n,
}).passthrough();
export type Provider = z.infer<typeof Provider>;

export const Strategy = z.enum(['failover', 'fastest', 'roundRobin', 'auto']);
export type Strategy = z.infer<typeof Strategy>;

export const Route = z.object({
  id: z.string(),
  description: z.string().default(''),
  strategy: Strategy,
  targets: z.array(z.string()),
  hedgeAfterMs: z.number().default(0),
  localFallback: z.boolean().default(true),
}).passthrough();
export type Route = z.infer<typeof Route>;

export const RuleCondition = z.object({
  sensitive: b,
  minTokens: n,
  maxTokens: n,
  hasTools: b,
  hasImages: b,
  keywords: z.array(z.string()).nullable().optional(),
  client: s,
}).passthrough();
export type RuleCondition = z.infer<typeof RuleCondition>;

export const Rule = z.object({ name: z.string(), route: z.string(), enabled: z.boolean().default(true), when: RuleCondition }).passthrough();
export type Rule = z.infer<typeof Rule>;

export const Config = z.object({
  setupComplete: z.boolean(),
  providers: z.array(Provider),
  routes: z.array(Route),
  rules: z.array(Rule),
  aliases: z.record(z.string()),
  defaultRoute: z.string(),
  autoFallbackRoute: z.string(),
  kedge: z.enum(['auto', 'on', 'off']),
  requireClientKey: z.boolean(),
  clients: z.array(z.object({ id: z.string(), name: z.string(), prefix: z.string(), created: z.string(), routes: z.array(z.string()) }).passthrough()),
  network: z.object({
    probeHosts: z.array(z.string()),
    probeIntervalSeconds: z.number(),
    probeTimeoutMs: z.number(),
    offlineAfterFailures: z.number(),
    onlineAfterSuccesses: z.number(),
    deepCheckMinutes: z.number(),
  }).passthrough(),
  privacy: z.object({ detectSecrets: z.boolean(), forceLocalWhenSensitive: z.boolean() }).passthrough(),
  corsOrigins: z.array(z.string()),
  logContent: z.boolean(),
  lazaretteHoldHours: z.number().default(8),
}).passthrough();
export type Config = z.infer<typeof Config>;

export const Preset = z.object({
  id: z.string(),
  name: z.string(),
  kind: z.string(),
  baseUrl: z.string(),
  local: z.boolean(),
  apiKeyEnv: s,
  needsKey: z.boolean(),
  keyUrl: s,
  hint: z.string(),
}).passthrough();
export type Preset = z.infer<typeof Preset>;
export const Presets = z.array(Preset);

export const Detected = z.array(z.object({
  preset: z.string(),
  name: z.string(),
  baseUrl: z.string(),
  running: z.boolean(),
  configured: s,
  models: z.array(ModelInfo),
  error: s,
}).passthrough());
export type Detected = z.infer<typeof Detected>;

export const Attempt = z.object({ target: z.string(), outcome: z.string(), status: n, ms: z.number(), error: s }).passthrough();
export const RequestRecord = z.object({
  id: z.string(),
  time: z.string(),
  client: s,
  dialect: z.string(),
  endpoint: z.string(),
  requestedModel: z.string(),
  route: s,
  decision: z.array(z.string()),
  attempts: z.array(Attempt),
  target: s,
  local: z.boolean(),
  kedge: z.boolean(),
  sensitive: z.boolean(),
  failover: z.boolean(),
  stream: z.boolean(),
  status: z.number(),
  error: s,
  firstByteMs: n,
  totalMs: z.number(),
  estimatedTokens: z.number(),
  inputTokens: n,
  outputTokens: n,
  cost: n,
  promptPreview: s,
  responsePreview: s,
}).passthrough();
export type RequestRecord = z.infer<typeof RequestRecord>;
export const RequestList = z.array(RequestRecord);

export const ProviderHealth = z.object({
  connectivity: Connectivity,
  providers: z.array(z.object({
    id: z.string(),
    local: z.boolean(),
    reachable: b,
    authOk: b,
    probeMs: n,
    lastError: s,
    lastChecked: s,
    lastDeepCheck: s,
    models: z.array(ModelInfo),
    loaded: z.array(z.string()).default([]),
  }).passthrough()),
  breakers: z.array(z.object({ key: z.string(), state: z.string(), openUntil: s, failures: z.number(), lastError: s, lastFailure: s }).passthrough()),
}).passthrough();
export type ProviderHealth = z.infer<typeof ProviderHealth>;

export const LogEntries = z.array(z.object({ time: z.string(), level: z.string(), event: z.string(), message: s, route: s, requestId: s }).passthrough());
export type LogEntries = z.infer<typeof LogEntries>;

/** Fairlead's link to Lazarette: states and names only; never a value or the pairing secret. */
export const LazaretteView = z.object({
  paired: z.boolean(),
  pairing: z.string().nullable().optional(),
  state: z.string(),
  message: z.string().nullable().optional(),
  lastChecked: z.string().nullable().optional(),
  expiresAt: z.string().nullable().optional(),
  holdHours: z.number(),
  port: z.number(),
  registryPort: z.number(),
  names: z.array(z.object({ name: z.string(), state: z.string() })).default([]),
}).passthrough();
export type LazaretteView = z.infer<typeof LazaretteView>;
