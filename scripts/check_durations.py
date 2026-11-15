#!/usr/bin/env python3
"""
Check that every episode ID in the channel config has a real duration
resolved in Jellyfin (i.e. not the 1800s fallback).

Usage:
    python3 scripts/check_durations.py

Reads JELLYFIN_API_KEY and JELLYFIN_BASE_URL from environment or .env file.
Exits 1 if any episode IDs are missing from the library or have zero duration.
"""

import json
import os
import sys
import urllib.request
from pathlib import Path

FALLBACK_DURATION_TICKS = 0
ENV_FILE = Path(__file__).parent.parent.parent / "ClassicTV3.0" / ".env"


def load_env() -> None:
    """Load JELLYFIN_API_KEY and JELLYFIN_BASE_URL from .env if not already set."""
    if ENV_FILE.exists() and not os.environ.get("JELLYFIN_API_KEY"):
        for line in ENV_FILE.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, _, value = line.partition("=")
                os.environ.setdefault(key.strip(), value.strip())


def get_channel_episode_ids(config_path: Path) -> list[str]:
    """Extract all Jellyfin IDs from the plugin config XML."""
    content = config_path.read_text()
    start = content.index("<ChannelsJson>") + len("<ChannelsJson>")
    end = content.index("</ChannelsJson>")
    raw = (
        content[start:end]
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
    )
    root = json.loads(raw)
    ids = []
    for channel in root.get("channels", []):
        for block in channel.get("blocks", []):
            for show in block.get("shows", []):
                ids.extend(show.get("jellyfinIds", []))
    return ids


def check_item(base_url: str, api_key: str, item_id: str) -> tuple[str, float | None]:
    """Return (id, duration_seconds) or (id, None) if not found."""
    url = f"{base_url}/Items/{item_id}"
    req = urllib.request.Request(url, headers={"Authorization": f'MediaBrowser Token="{api_key}"'})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            data = json.load(r)
        ticks = data.get("RunTimeTicks")
        if ticks and ticks > 0:
            return item_id, ticks / 10_000_000.0
        return item_id, None
    except urllib.error.URLError:
        return item_id, None


def main() -> int:
    load_env()

    api_key = os.environ.get("JELLYFIN_API_KEY", "")
    base_url = os.environ.get("JELLYFIN_BASE_URL", "http://localhost:8096").rstrip("/")

    if not api_key:
        print("ERROR: JELLYFIN_API_KEY not set", file=sys.stderr)
        return 1

    config_path = (
        Path.home()
        / "Library/Application Support/Jellyfin/plugins/configurations"
        / "Jellyfin.Plugin.ClassicTV.xml"
    )

    if not config_path.exists():
        print(f"ERROR: config not found at {config_path}", file=sys.stderr)
        return 1

    all_ids = get_channel_episode_ids(config_path)
    unique_ids = list(dict.fromkeys(all_ids))  # deduplicate, preserve order
    print(f"Channel config: {len(all_ids)} total IDs, {len(unique_ids)} unique")

    missing = []
    zero_duration = []

    for i, item_id in enumerate(unique_ids):
        item_id_result, duration = check_item(base_url, api_key, item_id)
        if duration is None:
            missing.append(item_id_result)
        elif duration == 0.0:
            zero_duration.append(item_id_result)

        if (i + 1) % 50 == 0:
            print(f"  checked {i + 1}/{len(unique_ids)}...")

    print(f"\nResults ({len(unique_ids)} unique IDs checked):")
    print(f"  Resolved with real duration: {len(unique_ids) - len(missing) - len(zero_duration)}")
    print(f"  Not found in library:        {len(missing)}")
    print(f"  Found but zero duration:     {len(zero_duration)}")

    if missing:
        print("\nMissing IDs:")
        for item_id in missing:
            print(f"  {item_id}")

    if zero_duration:
        print("\nZero duration IDs:")
        for item_id in zero_duration:
            print(f"  {item_id}")

    if missing or zero_duration:
        print("\nFAIL: some episode IDs will use the 1800s fallback duration")
        return 1

    print("\nPASS: all episode IDs have real durations in Jellyfin")
    return 0


if __name__ == "__main__":
    sys.exit(main())
