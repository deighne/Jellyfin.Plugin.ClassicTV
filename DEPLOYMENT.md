# ClassicTV Plugin — Deployment

## Current state

- Repository manifest: `https://your-server.example.com/plugins/manifest.json`
- Plugin zips: `https://your-server.example.com/plugins/classictv_<version>.zip`
- Jellyfin server: `localhost:8096` (or your Jellyfin LAN IP)
- Plugin version: **12.0.0.1** — targeting Jellyfin 12.x (`targetAbi: 12.0.0.0`, `net10.0`)
- Live installed at `~/Library/Application Support/jellyfin/plugins/ClassicTV_12.0.0.1/`

The repository is already added to Jellyfin. To install or reinstall, go to:
Dashboard → Plugins → Catalog → ClassicTV → Install → Restart when prompted.

> The install directory must contain **exactly one** `ClassicTV_*` folder. Two versions of
> the same GUID cause load conflicts; move the old folder out before restarting.

For the wider system (addon, hosts, commands), see the ClassicTV3.0 repo's `AGENTS.md`.

---

## How Jellyfin plugin installation works

Jellyfin installs plugins via a **repository manifest** — a JSON array hosted at a URL.
The manifest lists each plugin with a `versions` array containing the zip URL, checksum,
and `targetAbi`. The dashboard Catalog reads from any repo URL added under Repositories.

Installing via the UI validates that the manifest structure, assembly, GUID, and NuGet
packaging are all correct.

---

## Building a new release

### Prerequisites (one-time)

```bash
pipx install jprm
```

### Build steps

```bash
cd ~/projects/Jellyfin.Plugin.ClassicTV

# 1. Run tests — must pass before building
dotnet test --configuration Release

# 2. Build the zip (--version is REQUIRED; jprm reads Jellyfin.Plugin.ClassicTV/build.yaml)
mkdir -p artifacts
jprm plugin build "Jellyfin.Plugin.ClassicTV/" \
    --output=./artifacts \
    --dotnet-configuration Release \
    --dotnet-framework net10.0 \
    --version 12.0.0.1

# 3. Patch meta.json — REQUIRED
#    jprm omits the 'assemblies' field; without it Jellyfin installs the
#    plugin but never loads the DLL. This caused a kernel panic on first deploy.
python3 scripts/patch_meta.py artifacts/classictv_<version>.zip

# 4. Get the MD5 checksum for the manifest
md5 -q artifacts/classictv_<version>.zip

# 5. Patch the changelog into meta.json if you care (optional)
#    jprm ignores build.yaml's changelog and emits a stale one. Patching the
#    zip changes its checksum, so do this BEFORE computing the MD5 in step 4.
```

### Known jprm issues

- `jprm` requires the `artifacts/` directory to exist before running — create it first.
- `jprm` finds the `.csproj` by scanning the project directory. If a `.slnx` file is
  present in that directory, dotnet will use it instead of the `.csproj` and publish
  nothing. Keep `.slnx` files at the solution root only, not in the plugin project dir.
- `jprm repo add` expects a directory path, not a file path. It's simpler to update
  `manifest.json` and the `web/plugins/` zip manually (see below).

---

## Deploying to tv.dane.photos

The plugin zip and manifest are served from `~/projects/ClassicTV3.0/web/plugins/` on
jfmac via Caddy. The `deploy-jfmac.sh` script syncs this directory automatically.

To update manually:

```bash
# From dev machine
cp artifacts/classictv_<version>.zip \
   ~/projects/ClassicTV3.0/web/plugins/classictv_<version>.zip

# Update the checksum in the manifest
nano ~/projects/ClassicTV3.0/web/plugins/manifest.json

# Deploy
cd ~/projects/ClassicTV3.0
bash deploy/deploy-jfmac.sh
```

The manifest `sourceUrl` must match the public URL exactly:
`https://tv.dane.photos/plugins/classictv_<version>.zip`

---

## Adding channel data

Once the plugin is confirmed Active in Dashboard → Plugins:

1. Go to Dashboard → Plugins → ClassicTV → Configuration (gear icon)
2. Paste channel JSON in the text field — format documented in
   `docs/classic-tv-4-architecture.md` under "Q3: Channel schedule storage format"

Example minimal config:

```json
{
  "channels": [
    {
      "id": "4",
      "name": "Classic Sitcoms",
      "blocks": [
        {
          "name": "All Day",
          "startHour": 0,
          "endHour": 24,
          "offline": false,
          "shows": [
            {
              "name": "Cheers",
              "weight": 1,
              "jellyfinIds": [
                "<episode-uuid>",
                "<episode-uuid>"
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

Jellyfin item UUIDs: click any episode in the Jellyfin web UI — the URL contains
the ID (`/web/#/details?id=<uuid>`).

---

## Validation

After installing and restarting:

```bash
# Should return 404 JSON — means the controller loaded
curl -s "http://192.168.1.207:8096/ClassicTV/channel/test"

# After adding channel data, should return play command
curl -s "http://192.168.1.207:8096/ClassicTV/channel/4" | python3 -m json.tool
```

Expected response:
```json
{
  "channelId": "4",
  "title": "Cheers S01E01",
  "jellyfinItemId": "abc123...",
  "startOffset": 423.7,
  "duration": 1800.0,
  "nextItem": { ... },
  "generatedAt": 1725900000
}
```

`startOffset` should increase with wall-clock time as the episode plays out.

---

## Startup notes

- `ScheduleCacheWorker` (an `IHostedService`) runs its first build **5 seconds** after
  start, then hourly. It makes no library calls — only schedule arithmetic — so it is safe
  to start early.
- `RefreshDurationsTask` (an `IScheduledTask`) resolves real episode runtimes from
  `ILibraryManager`. It has a **`StartupTrigger`** (added in 10.11.0.8) plus a daily 3am
  trigger, so `DurationCache` is repopulated on every Jellyfin start.
- Until durations resolve, the schedule uses a 1800s fallback for any unresolved ID, and it
  will visibly shift once real durations land. The startup trigger makes that window seconds,
  not hours.

Verify a fresh start with the merged log tool from the ClassicTV3.0 repo:

```bash
python3 tools/classictv-logs.py --since 10m
```

Expect to see `Loaded plugin: "ClassicTV" "<version>"`, then `RefreshDurationsTask` and a
`cache build complete` line shortly after.

## Schedule engine semantics (important)

`ScheduleEngine.ComputeSchedule` is pure arithmetic over a channel's config, a unix time,
and a duration map. Key rules:

- `ExpandBlockEpisodes` emits **every episode of every show exactly once per cycle**,
  weighted-round-robin interleaved.
- `weight` controls **spacing only** — it does not change how many episodes a show
  contributes. Do not set weight equal to episode count.
- Elapsed time is measured from the channel's fixed `startAnchor`; the rotation is continuous
  and does not reset daily.
- `cycleDuration` is the sum of all episode durations in the cycle. Changing the episode set,
  a duration, or the engine changes it — and therefore changes the current position. A
  schedule "jump" after such a change is expected.

Regression test: `ComputeSchedule_AdvancesThroughFullCatalogAcrossCycles` in
`Jellyfin.Plugin.ClassicTV.Tests/Schedule/ScheduleEngineTests.cs`.
