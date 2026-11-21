#!/usr/bin/env python3
"""Checks a mkosi JSON manifest: required packages present, forbidden ones absent.

    python3 scripts/check-manifest.py mkosi.output/ubit_0.1.0.manifest
"""
import json
import sys

REQUIRED = {"ubit-agent", "firefox", "plasma-desktop", "sddm", "linux-image-generic"}
FORBIDDEN = {"snapd", "ubuntu-report", "popularity-contest", "apport", "whoopsie", "ubuntu-pro-client"}

manifest = json.load(open(sys.argv[1]))
packages = {p["name"]: p.get("version", "") for p in manifest.get("packages", [])}
problems = []
problems += [f"missing required package: {n}" for n in sorted(REQUIRED - packages.keys())]
problems += [f"forbidden package installed: {n}" for n in sorted(FORBIDDEN & packages.keys())]
if "snap" in packages.get("firefox", ""):
    problems.append(f"firefox is the snap transition package ({packages['firefox']})")

print(f"{len(packages)} packages in manifest")
for p in problems:
    print("FAIL", p)
sys.exit(1 if problems else 0)
