#!/usr/bin/env python3
"""Build the source bundles — a thin CLI over the bundling capability, not a second implementation.

The production logic lives in `agience-chorus/src/agience_chorus/seraph/bundling.py` as two
capabilities:

    op.bundle.observe    organon — reaches the source tree, interprets nothing
    op.bundle.condense   tekton  — source in, canonical sha-addressed payload out

`test_the_tekton_reproduces_every_shipped_payload_BYTE_FOR_BYTE` (chorus) compares whole payloads,
not just shas, across all 16 groups, so this file cannot drift into a second producer without that
test catching it.

This file is the ergonomic entry point — a command to type — and holds no copy of the bundling
logic itself. It loads the tekton by the pinned path, the same coupling `bundle_spec.json` already
declares (every module it names lives in agience-chorus), rather than adding a package import edge
between the repos.

It is not the oracle: verification lives outside the producer, in
`agience-cloud/deploy/test_bundles_are_what_they_claim.py`, because a producer grading its own
homework catches nothing its own reader gets wrong.

    python build_bundles.py            # rebuild every group from current source
    python build_bundles.py --check    # report drift, write nothing (CI / pre-commit)
    python build_bundles.py fetch      # rebuild one group
"""
from __future__ import annotations

import argparse
import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
TEKTON = WORKSPACE / "agience-chorus" / "src" / "agience_chorus" / "seraph" / "bundling.py"

sys.path.insert(0, str(WORKSPACE / "agience-prism" / "py" / "src"))


def _load_tekton():
    """Load the producer from its pinned path — the same path resolution `bundle_spec.json` uses.

    A missing tekton raises here rather than falling back to a local implementation, which would
    be a second producer: two implementations that agree until the day they do not."""
    if not TEKTON.is_file():
        raise SystemExit(
            "the bundle producer is not at %s.\n"
            "  It is a chorus capability (op.bundle.observe / op.bundle.condense) and this file is\n"
            "  only a CLI over it. There is deliberately no local fallback — a second producer\n"
            "  would be a second implementation that could silently drift from the first." % TEKTON)
    spec = importlib.util.spec_from_file_location("seraph_bundling", TEKTON)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("groups", nargs="*", help="groups to build (default: all)")
    ap.add_argument("--check", action="store_true",
                    help="report drift and write nothing; exit 1 if any bundle is stale")
    args = ap.parse_args()

    bundling = _load_tekton()
    need = {"groups": args.groups or None, "land": not args.check}
    try:
        answer = bundling.bundle_condense_handler(str(WORKSPACE))(need)
    except KeyError as e:                       # unknown group — named in the error
        raise SystemExit(str(e).strip('"'))
    except FileNotFoundError as e:              # a declared module moved
        raise SystemExit(str(e))

    for group, r in sorted(answer["groups"].items()):
        if not r["moved"]:
            print("  %-12s unchanged  %s" % (group, r["sha256"][:16]))
        else:
            what = "NEW" if r["was"] is None else "%s -> %s" % (r["was"][:12], r["sha256"][:12])
            print("  %-12s %-10s %s" % (group, "STALE" if args.check else "written", what))

    moved = answer["moved"]
    if args.check and moved:
        print("\n%d bundle(s) stale — the shipped payload no longer matches chorus source.\n"
              "Run `python build_bundles.py` to rebuild." % len(moved))
        return 1
    if not args.check and moved:
        print("\n%d bundle(s) rebuilt. They are DATA, not code — the same bytes and the same\n"
              "sha the mesh will carry as `bundle-<group>` artifacts." % len(moved))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
