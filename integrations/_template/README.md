# <Tool name>

**Status:** Hackathon (not verified yet)

## Setup

```
<the exact settings, with http://127.0.0.1:8776/v1 and fl-YOUR-KEY>
```

## Checklist

- [ ] A plain chat answers through `auto`
- [ ] Streaming works (tokens appear as they are generated)
- [ ] Tool calls / function calling work (if the tool uses them)
- [ ] With Kedge mode forced on (Settings or tray), the tool keeps working on a local model
- [ ] The Activity page shows the tool's key name on its requests
- [ ] Errors (stop Ollama, use a wrong key) show up readably in the tool

## Notes

What broke, what Fairlead had to change, quirks of the tool. When everything above is ticked, change the status
here and in `integrations/README.md` to Verified, and the snippet in `web/src/lib/snippets.ts` to `ready`.
