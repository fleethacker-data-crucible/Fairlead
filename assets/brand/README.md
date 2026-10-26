# Fairlead brand

> **Placeholder.** These emblems are a simple drawing made to get the app running. Replace `emblem-light.png` and
> `emblem-dark.png` with the detailed original when it exists, then run `build\New-FairleadBrand.ps1 -MarkOnly`
> so the script never overwrites them again.

| File | What it is |
|---|---|
| `emblem-light.png`, `emblem-dark.png` | Full emblem (Fleet frame and wordmark) for light and dark backgrounds. Copied to `web/public/brand/` |
| `emblem-source.html` | The SVG the placeholder emblems are rendered from |
| `mark.svg` | Icon-only mark, simplified to read at 32 px: a roller fairlead with the line led between its rollers |
| `mark-256.png`, `mark-64.png` | The mark as PNG |
| `favicon.ico` | Browser icon (16, 32, 48, 256) |
| `tray.ico` | Tray icon (16, 20, 24, 32) |

The subject is a roller fairlead on its deck plate: two upright rollers with the line passing between them, breaking
out of the circle on both sides. The starboard-green light above it is the signature accent (`#a3d65c` night,
`#4d7a1a` chart room). Everything follows `docs/UNIFIED-UI.md` section 8.

Regenerate all of it with `powershell -File build\New-FairleadBrand.ps1` (needs Edge or Chrome on the build machine).
