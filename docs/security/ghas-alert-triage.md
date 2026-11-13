# GHAS alert triage (sprint 2026-11)

Review of the open GitHub Advanced Security alerts in this repository. This document does not dismiss any alert.

| Alert | Rule / package | Path | Disposition | Reason |
|---|---|---|---|---|
| dependabot #1 | zod (npm), [GHSA-mrhx-c7xc-m3w5](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5) | web/package-lock.json | needs owner decision | Installed zod is 3.25.76 and the only patched release is 4.6.6, a major bump; the UI validates local admin JSON with unbounded collections, so that bump is left for the owner. |

## dependabot #1

**Package.** `zod` 3.25.76 is a direct dependency of `web` (`web/package.json` and `web/package-lock.json`). The lockfile has one copy. 3.25.76 is the latest stable 3.x release on npm. GitHub's advisory range is `< 4.6.6`, and the first patched version is `4.6.6` (`4.6.7` is also published). No 3.x release is inside the patched range.

**Advisory.** [GHSA-mrhx-c7xc-m3w5](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5) (alias CVE-2023-54404) is high severity, CWE-770, and still published (`withdrawn_at` is null as of this review). It describes Zod through 4.6.5 exhausting memory by accumulating one validation issue per failing element of a large array, with no cap and no early stop. The change that shipped in 4.6.6 ([colinhacks/zod@a0ebf97](https://github.com/colinhacks/zod/commit/a0ebf970f2d752e5f58527cd2942ff48be4f46bd), `fix(v4)`) checks `.max()`, `.length()`, and `.size()` on arrays, sets, and maps before walking elements. Unbounded collections are unchanged. NVD lists CVE-2023-54404 as Rejected (28 October 2026): the described memory use grows with input `JSON.parse` has already allocated, and the CVE numbering authority called it expected validator behavior. Dependabot follows the open GitHub advisory, so the alert stays open while the GHSA is published.

**Reachability.**

- Zod runs in the admin UI. `web/src/lib/useResource.ts` calls `schema.safeParse` on JSON that `web/src/lib/api.ts` has already parsed from the local Fairlead host. `web/src/lib/config.ts` calls `Config.parse`, `GatewayView.parse`, and `LazaretteView.parse` on that host's write responses. `web/test/requestlog.test.ts` parses fixtures. Gateway bodies (`/v1/...`) are read in C# (`Json.Body` in `src/Fairlead.Core`). The core does not reference zod.
- Every collection in `web/src/lib/schemas.ts` is unbounded. No array, set, or map uses `.max()`, `.length()`, or `.size()`. The 4.6.6 early size check does not apply to these schemas.
- Zod builds a per-element issue list when elements fail their item schema. Responses the host serializes in its own shape follow the success path. The UI's list calls are also capped by the host: `/api/requests` clamps `limit` to 1..1000 (`ApiRoutes.cs`), and the request log clamps to 1..5000 (`RequestLog.MaxLimit`). The page asks for at most 1000 rows.
- Gateway callers submit bodies to the C# host. Those bodies are not passed to these schemas. The case that reaches the issue-accumulation path is a local host response that is a large array of mistyped elements. `JSON.parse` has already allocated that payload in the admin browser tab, and the added cost is the issue objects in that tab.

**Version left in place.** Moving to 4.6.6 is a major bump, which this sprint does not make on its own. Checked against the 4.6.6 type declarations, it is also a source change: the package main entry is Zod 4, `ZodTypeDef` is not exported (`web/src/lib/useResource.ts` imports it and uses the three-parameter `ZodType`), and `z.record` takes a key schema and a value schema (`web/src/lib/schemas.ts` calls `z.record(z.string())` for `headers` and `aliases`). `.passthrough()` is still present on 4.6.6 and marked deprecated. Installing 4.6.6 and importing `zod/v3` would put the lockfile in the patched range and would still run the v3 parser. The 4.6.6 size check is in the v4 parser.

**Decision.** Keep 3.25.76, or approve a separate Zod 4 migration of `web/src/lib/schemas.ts` and `web/src/lib/useResource.ts`. If the installed version stays, dismiss Dependabot alert 1 with reason `tolerable_risk` ("Risk is tolerable to this project"). `not_used` does not match this tree: the UI calls zod. This review leaves the false-positive call open because the GitHub advisory is still published after NVD rejected the CVE.
