# Jellyfin.Plugin.ClassicTV

The schedule engine plugin for **Classic TV** — turns your Jellyfin media library into 24/7, wall-clock-synchronized broadcast television channels.

---

## Features

- **Deterministic 24/7 Schedule Engine** — Full-catalog round-robin rotation anchored to a fixed timestamp (`startAnchor`), so tuning in drops viewers mid-episode like real cable/OTA TV.
- **48-Hour Schedule Cache & Startup Duration Refresh** — Precomputes upcoming slots and resolves episode runtimes from Jellyfin's `ILibraryManager` on startup and daily schedule.
- **Dashboard Channel Editor** — Built-in Jellyfin Dashboard configuration page to create channels, arrange time-of-day blocks, pick TV series and movies, and set channel numbers/weights.
- **Mirror Channels** — Follow another Jellyfin user's live playback session on a dedicated channel (`mirrorUser`).
- **Pre-Rendered Guide Channel Support** — Byte-range MP4 streaming endpoint (`/ClassicTV/guide/stream/{chunkId}`) and timestamp-aware schedule queries (`?t=`).
- **Missing-Item Pruning** — Automatically skips deleted or missing library items so channels never stall.

---

## Compatibility

| Plugin Version | Jellyfin Server | Target Framework |
|---|---|---|
| `12.0.0.x` | Jellyfin 12.x | `net10.0` |
| `10.11.0.x` | Jellyfin 10.11.x | `net9.0` |

---

## Installation

### Option 1: Jellyfin Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → Add**.
2. Add your Classic TV plugin `manifest.json` URL.
3. Open **Catalog**, find **ClassicTV**, click **Install**, and restart Jellyfin.

### Option 2: Manual / Release Zip Install

1. Download `classictv_<version>.zip` from GitHub Releases (or build from source below).
2. Extract it into `<jellyfin-data>/plugins/ClassicTV_<version>/`.
   > **Important:** Keep only **one** `ClassicTV_*` folder in `plugins/` at a time.
3. Restart Jellyfin and verify:
   ```bash
   curl -s http://localhost:8096/ClassicTV/channels
   ```

---

## Building from Source

Requires the .NET 10 SDK (and [`jprm`](https://github.com/jellyfin/jellyfin-plugin-repo-manifest) for packaging plugin zips).

```bash
# Run unit tests
dotnet test --configuration Release

# Build release zip
mkdir -p artifacts
jprm plugin build "Jellyfin.Plugin.ClassicTV/" --output=./artifacts \
  --dotnet-configuration Release --dotnet-framework net10.0 --version 12.0.0.13
python3 scripts/patch_meta.py artifacts/classictv_12.0.0.13.zip
```

---

## REST API Endpoints

| Method | Route | Description |
|---|---|---|
| `GET` | `/ClassicTV/channels` | List enabled channels (number, name, flags) |
| `GET` | `/ClassicTV/channel/{id}` | Current + next item, offset, and stream metadata (optional `?t=<unix>`) |
| `GET` | `/ClassicTV/guide/stream/{chunkId}` | Byte-range MP4 stream for pre-rendered Channel 1 guide chunks |
| `GET`/`PUT` | `/ClassicTV/manage/channels` | Admin channel configuration CRUD |
| `GET` | `/ClassicTV/manage/library/series` | List TV series for channel picker |
| `GET` | `/ClassicTV/manage/library/movies` | List movies for channel picker |

---

## License

MIT — see [`LICENSE`](LICENSE).
