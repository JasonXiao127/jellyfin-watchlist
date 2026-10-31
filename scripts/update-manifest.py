#!/usr/bin/env python3
"""Add (or replace) this release in the Jellyfin plugin repository manifest.

Usage: update-manifest.py <manifest.json> <zip-file> <source-url>

<source-url> is the public download address of the zip (a GitHub release
asset). The zip checksum is the MD5 Jellyfin verifies on install. Older
versions already listed are kept; the same version is replaced, newest first.
"""

import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required (pip install pyyaml)")


def main() -> None:
    if len(sys.argv) != 4:
        sys.exit("usage: update-manifest.py <manifest.json> <zip-file> <source-url>")

    manifest_path = Path(sys.argv[1])
    zip_path = Path(sys.argv[2])
    source_url = sys.argv[3]

    build = yaml.safe_load(Path("build.yaml").read_text(encoding="utf-8")) or {}
    version = str(build["version"])
    guid = build["guid"]

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    entry = {
        "checksum": checksum,
        "changelog": build.get("changelog", ""),
        "targetAbi": str(build.get("targetAbi", "")),
        "sourceUrl": source_url,
        "timestamp": timestamp,
        "version": version,
    }

    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else []
    plugin = next((p for p in manifest if p.get("guid") == guid), None)
    if plugin is None:
        plugin = {
            "category": build.get("category", "General"),
            "guid": guid,
            "name": build.get("name", ""),
            "description": build.get("description", ""),
            "owner": build.get("owner", ""),
            "overview": build.get("overview", ""),
            "versions": [],
        }
        manifest.append(plugin)

    plugin["versions"] = [v for v in plugin.get("versions", []) if v.get("version") != version]
    plugin["versions"].insert(0, entry)

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"manifest: {plugin['name']} {version} ({checksum})")


if __name__ == "__main__":
    main()
