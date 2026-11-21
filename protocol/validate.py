#!/usr/bin/env python3
"""Check that every fixture in protocol/fixtures agrees with protocol/v1.schema.json.

valid/*.json must validate; invalid/*.json must not. Run from anywhere:
    python3 protocol/validate.py
Requires: pip install jsonschema
"""
import glob
import json
import pathlib
import sys

import jsonschema

ROOT = pathlib.Path(__file__).resolve().parent
schema = json.loads((ROOT / "v1.schema.json").read_text())
jsonschema.Draft202012Validator.check_schema(schema)
validator = jsonschema.Draft202012Validator(schema)

failures = 0
for path in sorted(glob.glob(str(ROOT / "fixtures" / "*" / "*.json"))):
    expect_valid = "/valid/" in path.replace("\\", "/")
    errors = list(validator.iter_errors(json.loads(pathlib.Path(path).read_text())))
    ok = (not errors) == expect_valid
    failures += 0 if ok else 1
    rel = pathlib.Path(path).relative_to(ROOT)
    print(f"{'ok  ' if ok else 'FAIL'} {rel}")
    if not ok and errors:
        print(f"     {errors[0].message}")

sys.exit(1 if failures else 0)
