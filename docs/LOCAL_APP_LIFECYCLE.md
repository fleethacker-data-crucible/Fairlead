# Local app lifecycle standard

Applies to every product in this family that runs a local engine and a browser UI (Halyard, Shellback,
Lazarette, and whatever comes next). A loopback app that keeps running after its tab closes is a defect:
it hides from the user, holds its DLLs and ports, breaks the next build, and warms the CPU for nothing.

## Requirements

1. **The browser tab owns the engine's life.** The UI sends a heartbeat to the engine every 5 seconds
   and a "leaving" beacon on `pagehide`. With no heartbeat for 20 seconds, or 5 seconds after a leaving
   beacon with no new heartbeat, the engine stops itself. A page reload sends a fresh heartbeat inside the
   grace period and nothing happens.
2. **Never stop mid-write.** Auto-stop waits while a background job (seed, apply, sync, send) is running
   and stops on the next tick after it finishes.
3. **An explicit way out, in the UI.** An *End session* control (or equivalent) that stops the engine
   cleanly, visible in the main navigation.
4. **An explicit way out, from the shell.** A `Stop-<App>.ps1` script that finds and stops every running
   engine. It must work without knowing PIDs.
5. **One instance.** The launcher detects a running engine (runtime file, or a process scan) and asks
   *stop it and start fresh?* before starting another. `--replace` answers yes; `--stay` keeps the
   engine alive after the tab closes for the cases that need it (a long job, a report run).
6. **The build stops the app.** The build script calls the stop logic before compiling and says what it
   stopped, so a locked DLL never produces a wall of retry errors.
7. **A runtime file per engine.** On start the engine writes `logs\<app>-server-<pid>.json` with pid,
   port, url and start time, and deletes it on stop. Never the session token. Stop scripts and launchers
   read these; stale files (dead pid) are removed on sight.
8. **An off switch.** `<APP>_AUTO_STOP=0` in the env file disables auto-stop for machines or sessions
   where it gets in the way.

## Reference implementation (Halyard)

| Piece | Where |
|---|---|
| Heartbeat + leaving beacon (keepalive fetch, `pagehide`, `visibilitychange`) | `web/src/lib/api.ts` `startHeartbeat`, mounted once in `app.tsx` |
| Presence rule, timer, `POST /api/heartbeat` | `src/Halyard.Core/Host/HalyardHost.cs` (`RecordPresence`, `BrowserGone`, `PresenceTick`), `ApiRoutes.cs` |
| Options and env | `HostOptions.AutoStop/HeartbeatTimeout/LeavingGrace`, `HALYARD_AUTO_STOP` |
| Process discovery and stop | `scripts/Halyard-Process.ps1` (`Get-HalyardProcesses`, `Stop-HalyardProcesses`), `scripts/Stop-Halyard.ps1` |
| Launcher guard, `--stay`, `--replace` | `scripts/Start-Halyard.ps1` |
| Build stops the app | `build/Build-Halyard.ps1` |
| Test of the rule | `HostTests.EngineStopsItselfOnlyAfterTheBrowserIsGone` |

## Notes for the other stacks

- **Shellback** serves static files from a PowerShell loopback server and keeps its console visible, so
  Ctrl+C works today. Items 1, 5, 6 and 7 still apply: a hidden or forgotten console is the same defect,
  and the build/serve scripts should stop a running server before replacing `dist\`.
- **Lazarette** (`Start.ps1`) runs a GUI and a loopback UI; apply the same heartbeat so closing the
  tab closes the app, keep the vault's own lock-on-idle rules as they are.
- The periodic heartbeat is a normal `fetch` with the session header. The unload signal is
  `navigator.sendBeacon` (a keepalive fetch is dropped by some browsers during unload); it cannot set
  headers, so the engine accepts the session token in the JSON body for that one route only, never in
  the query string, and never logs bodies.
- Finding what holds the DLL: `Process.Modules` does not list .NET-mapped assemblies. Open the file
  with `FILE_READ_ATTRIBUTES` and call `NtQueryInformationFile` with `FileProcessIdsUsingFileInformation`;
  it needs no admin in any session and also reports elevated holders from a normal window. (The Restart
  Manager's `RmGetList` was dropped: it returns access denied to a standard user in session 0, such as a
  headless CI runner.) A failed query is reported as a warning, not as an empty result. Stopping an elevated holder needs one elevated `Stop-Process` (UAC).
