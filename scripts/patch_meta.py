#!/usr/bin/env python3
"""
Patch meta.json inside a jprm-built plugin zip to add the assemblies array.

jprm generates meta.json without an 'assemblies' field. Jellyfin's plugin loader
reads this field to know which DLLs to load. Without it, the DLL is installed but
never executed — the plugin appears as Active but no code runs.

Usage: python3 scripts/patch_meta.py artifacts/classictv_10.11.0.1.zip
"""

import json
import os
import sys
import zipfile


def patch(zip_path: str) -> None:
    patched_path = zip_path + ".patching"

    with zipfile.ZipFile(zip_path, "r") as zin, zipfile.ZipFile(patched_path, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            if item.filename == "meta.json":
                meta = json.loads(data)
                existing = meta.get("assemblies", [])
                if not existing:
                    dlls = [
                        i.filename
                        for i in zin.infolist()
                        if i.filename.endswith(".dll")
                    ]
                    meta["assemblies"] = dlls
                data = json.dumps(meta, indent=4).encode("utf-8")
                print(f"  patched meta.json: assemblies={meta['assemblies']}")
            zout.writestr(item, data)

    os.replace(patched_path, zip_path)
    print(f"  wrote {zip_path}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: patch_meta.py <zip_path>")
        sys.exit(1)

    zip_path = sys.argv[1]
    if not os.path.exists(zip_path):
        print(f"Error: {zip_path} not found")
        sys.exit(1)

    patch(zip_path)
