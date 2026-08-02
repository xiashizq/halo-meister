#!/usr/bin/env python3
"""Validate the build-profile and capability catalog without game binaries."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "src/HaloMeister.App/Assets/GameBuildProfiles.json"
LEVELS = {
    "Unsupported",
    "Cataloged",
    "StaticVerified",
    "Integrated",
    "LiveValidated",
}
CAPABILITIES = {
    "RuntimeTags",
    "ObjectSpawn",
    "WeaponLoad",
    "ObjectAppearance",
    "BipedPossession",
    "AiPlacement",
    "GameplayCheats",
    "SoftCeilings",
    "RuntimeBoundaries",
    "PlayerTools",
    "PlayerAllegiance",
    "Machinima",
    "SavedFilm",
}


def main() -> int:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    profiles = catalog.get("profiles")
    if catalog.get("schemaVersion") != 1 or not isinstance(profiles, list):
        raise ValueError("expected schemaVersion 1 and a profiles list")

    ids: set[str] = set()
    for profile in profiles:
        identifier = profile.get("id")
        if not isinstance(identifier, str) or identifier in ids:
            raise ValueError(f"invalid or duplicate profile id: {identifier!r}")
        ids.add(identifier)

        capabilities = profile.get("capabilities", {})
        if not isinstance(capabilities, dict):
            raise ValueError(f"{identifier}: capabilities must be an object")
        for capability, level in capabilities.items():
            if capability not in CAPABILITIES:
                raise ValueError(f"{identifier}: unknown capability {capability}")
            if level not in LEVELS:
                raise ValueError(
                    f"{identifier}: invalid validation level {level!r} for {capability}"
                )

    for profile in profiles:
        layout = profile.get("nativeLayout")
        if layout is not None and layout not in ids:
            raise ValueError(
                f"{profile['id']}: nativeLayout {layout!r} does not name a profile"
            )

    print(f"Validated {len(profiles)} build profile(s) and capability declarations.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
