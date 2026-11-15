# AGENTS.md — ClassicTV Jellyfin Plugin

**Read this first.** This is the C# server plugin that computes the Classic TV schedule. It
is one half of Classic TV 4.0; the other half (the Kodi addon, hosts, and full operational
guide) is in the sibling repo `~/projects/ClassicTV3.0`. **Read that repo's `AGENTS.md`
first** for the system-level picture, then come back here.

**Last updated:** 2026-10-25 (plugin 12.0.0.1 targeting Jellyfin 12.3 / net10.0; see `~/projects/ClassicTV3.0/docs/jellyfin-12-upgrade-spec.md`)

---

## What this is

A Jellyfin server plugin (`guid a1b2c3d4-e5f6-7890-abcd-ef1234567890`) that answers "what is
playing on channel X right now, and at what offset?" The Classic TV Kodi addon calls it and
delegates playback to the Jellyfin Kodi addon.

Endpoints:
- Read (anonymous — the Kodi addon sends no token):
  - `GET /ClassicTV/channels` → `{ "Channels": [{ "Id", "Name", "Number" }] }` (disabled channels filtered out)
  - `GET /ClassicTV/channel/{id}` → `{ ChannelId, Title, JellyfinItemId, StartOffset, Duration, NextItem, GeneratedAt }` (404 for disabled/unknown)
- Management (admin only, `RequiresElevation`):
  - `GET/POST /ClassicTV/manage/channels` — full config read / validated replace
  - `GET /ClassicTV/manage/library/series`
  - `GET /ClassicTV/manage/library/series/{id}/episodes`
  - Dashboard UI: `Configuration/configPage.html` (registered via `IHasWebPages`)

---

## Layout

```
Jellyfin.Plugin.ClassicTV/
  Plugin.cs                     BasePlugin + IHasWebPages entry point
  Api/ClassicTvController.cs    public schedule read endpoints
  Api/ManagementController.cs   admin channel-management endpoints
  Api/ChannelConfigValidator.cs server-side config validation (IDs, numbers, blocks)
  Api/ManagementDtos.cs         management response DTOs
  Configuration/configPage.html dashboard channel editor markup fragment (embedded resource)
  Configuration/classictv.js      config-page controller ES module (data-controller="__plugin/classictvjs")
  Schedule/ScheduleEngine.cs    pure schedule arithmetic  ← most important file
  Schedule/ScheduleCache.cs     in-memory (channel, minute) → result cache
  Schedule/ScheduleCacheWorker.cs  IHostedService, 48h precompute, hourly; semaphore-guarded rebuild
  Schedule/DurationCache.cs     in-memory jellyfinId → seconds
  Schedule/RefreshDurationsTask.cs  IScheduledTask; StartupTrigger + daily 3am
Jellyfin.Plugin.ClassicTV.Tests/   xUnit, 215 tests
artifacts/                     built release zips (tracked)
DEPLOYMENT.md                  build/deploy details
docs/                          schedule-architecture.md, start-anchor-spec.md
```

---

## Schedule engine rules (do not break these)

`ScheduleEngine` is pure — no I/O, deterministic.

- **Full-catalog rotation.** `ExpandBlockEpisodes` emits **every episode of every show
  exactly once per cycle**, interleaved weighted-round-robin.
- **`weight` = spacing only.** It does not change how many episodes a show contributes.
  Never treat it as episode count.
- **`shuffle`** permutes a show's episodes within each rotation cycle, seeded by
  `(channelId, blockName, showName, cycleIndex)` using **FNV-1a** (`ScheduleEngine.SeedFor`).
  Do not swap in `System.HashCode`/`string.GetHashCode` — they are process-randomised and
  would change the order on every restart. A golden-value test locks the algorithm.
- **`startAnchor`** is a fixed origin; `elapsed = now - anchor`, continuous, never resets.
- **Channel `number`** is server-authoritative (0 = unassigned). Duplicate non-zero numbers
  among enabled channels are rejected by `ChannelConfigValidator` (HTTP 400). `enabled=false`
  hides a channel from `/channels`, 404s `/channel/{id}`, and is skipped by the cache worker.
- `position = elapsed % cycleDuration`; `cycleDuration` = sum of all episode durations.
- **Changing the episode set or any duration changes `cycleDuration` and the current
  position.** A visible schedule jump after such a change is expected.
- Missing durations fall back to `FallbackDurationSeconds` (1800s). `DurationCache` is empty
  on every restart until `RefreshDurationsTask` runs (now via `StartupTrigger`).

Regression tests to know: `ComputeSchedule_AdvancesThroughFullCatalogAcrossCycles`,
`ExpandBlockEpisodes_CoversEveryEpisodeExactlyOnce`,
`GetDefaultTriggers_IncludesStartupTrigger`.

---

## Commands

```bash
dotnet test --configuration Release          # 233 tests, must pass before release
```

Build/deploy a release is a multi-step manual process — see [`DEPLOYMENT.md`](DEPLOYMENT.md).
Summary: `jprm plugin build … --dotnet-framework net10.0 --version X` → `python3 scripts/patch_meta.py` →
`md5` → copy to the ClassicTV3.0 `web/plugins/` → update `manifest.json` →
`bash deploy/deploy-jfmac.sh` → install into the Jellyfin plugins dir → restart Jellyfin.
Tag releases `v12.0.0.N` (was `v10.11.0.N` on Jellyfin 10.11).

---

## Gotchas

- **Config pages must use the `data-controller` pattern.** The React dashboard injects
  `configPage.html` without executing inline `<script>`, so a page whose logic lives in an
  inline script renders inert (buttons do nothing, no console error). Ship the markup in
  `configPage.html` and the logic in a separate JS page registered via `GetPages()`
  (`Name = "classictvjs"`), referenced by `data-controller="__plugin/classictvjs"`. The module
  is an ES module: `export default function (view, params)` and initialize on
  `view.addEventListener('viewshow', ...)`. Same pattern as the official plugins.
- **jprm requires `--version`** and ignores `build.yaml`'s changelog (it emits a stale one).
  Patching the changelog changes the zip checksum — do it before computing the manifest md5.
- **`patch_meta.py` is mandatory** — jprm omits the `assemblies` field and Jellyfin then
  installs but never loads the DLL.
- **Only one `ClassicTV_*` directory** may exist in the Jellyfin plugins folder.
- **Restarting Jellyfin over SSH** does not work via `osascript`; use
  `pkill -TERM -f "Jellyfin.app/Contents/MacOS"`, wait for `:8096` to free, then
  `open -a Jellyfin`. (Full snippet in the ClassicTV3.0 `AGENTS.md` §7.)
- **NuGet packages must match the installed Jellyfin version** (`12.3.0` on `net10.0`) and use
  `ExcludeAssets: runtime`, or the plugin shows as `NotSupported`.
