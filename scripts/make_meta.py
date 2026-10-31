#!/usr/bin/env python3
"""Generate a Jellyfin plugin meta.json from the plugin build.yaml."""

import json
import sys
from pathlib import Path

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required (pip install pyyaml)")


def main() -> None:
    if len(sys.argv) < 3:
        sys.exit("usage: make_meta.py <build.yaml> <meta.json> [artifact-dir]")

    build_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    artifact_dir = Path(sys.argv[3]) if len(sys.argv) > 3 else output_path.parent

    data = yaml.safe_load(build_path.read_text(encoding="utf-8")) or {}

    artifacts = data.get("artifacts") or []
    assemblies = [name for name in artifacts if name.lower().endswith(".dll")]
    if not assemblies:
        assemblies = sorted(path.name for path in artifact_dir.glob("*.dll"))

    meta = {
        "guid": data.get("guid", ""),
        "name": data.get("name", ""),
        "description": data.get("description", ""),
        "overview": data.get("overview", ""),
        "owner": data.get("owner", ""),
        "category": data.get("category", "General"),
        "imageUrl": "",
        "version": str(data.get("version", "0.0.0.0")),
        "targetAbi": str(data.get("targetAbi", "")),
        "changelog": data.get("changelog", ""),
        "status": data.get("status", "Active"),
        "autoUpdate": bool(data.get("autoUpdate", False)),
        "assemblies": assemblies,
    }

    output_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {output_path}")


if __name__ == "__main__":
    main()
