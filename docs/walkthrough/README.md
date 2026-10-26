# Fairlead walkthrough

Set up Fairlead once in the browser so every tool can use one local address, `http://127.0.0.1:8776`, for all its models.

> **Status: skeleton.** The screenshots below are placeholders. Zombie Admin captures them with demo data, and the Chief of Staff scans them for sensitive content before they are added (team directive 10). Until then the image links do not resolve.

## Before you start

- Windows PowerShell 5.1 (part of Windows). No administrator rights and no installer.
- Microsoft Edge or Google Chrome.
- Launch: `build\Build-Fairlead.ps1` once on the build machine, then `.\Launch-Fairlead.bat` (tray icon; the setup page opens on first run).
- Optional: a local model server such as Ollama, or a cloud provider key you are authorized to use.

## Steps

### 1. First launch: Setup

On first run Fairlead opens **Setup** and scans this computer for local model servers.

![Fairlead Setup page on first run, listing the local model servers found on this computer.](../assets/walkthrough/01-first-launch.png)

### 2. Suggest routes

Select **Suggest targets**, check the targets for the `auto`, `smart`, `fast` and `local` routes, then **Save routes**.

![Fairlead Setup routes step, with suggested targets for the auto, smart, fast and local routes.](../assets/walkthrough/02-setup-routes.png)

### 3. Watch the overview

**Overview** shows live requests, route health and whether Kedge mode (local-only) is on.

![Fairlead Overview page with live request counts, route health and the Kedge mode status.](../assets/walkthrough/03-overview.png)

### 4. Check routes

**Routes** lists each route's targets and failover order.

![Fairlead Routes page showing the targets and failover order for each route.](../assets/walkthrough/04-routes.png)

### 5. Add a client

On **Clients**, create a client key for a tool and copy its setup snippet. The key is shown only once, so copy it straight into the tool.

![Fairlead Clients page listing the demo client and its copy-paste setup, with no key value shown.](../assets/walkthrough/05-clients.png)

### 6. Try the playground

In **Playground**, type a prompt and select **Explain** to see which route and target would answer, without sending it.

![Fairlead Playground with a demo prompt and the Explain dry run showing which route and target would answer.](../assets/walkthrough/06-playground-explain.png)

### 7. Read a decision trace

**Activity** lists each request. Open one to see its route, target, tokens and latency.

![Fairlead Activity page with one request's decision trace open, showing route, target, tokens and latency.](../assets/walkthrough/07-activity-trace.png)

## About these images

Images live in [`docs/assets/walkthrough/`](../assets/walkthrough/), named `NN-<screen>.png` after the step they belong to. They are 1440x900 at 100% scale in the Night watch (dark) theme, and show demo data only.
