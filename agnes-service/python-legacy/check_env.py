#!/usr/bin/env python3
"""Report which optional dependencies are importable (operator helper)."""
import importlib.util as u
import sys

print("python", sys.version.split()[0])
for m in ("psycopg", "psycopg2"):
    print(m, "importable" if u.find_spec(m) else "MISSING")
