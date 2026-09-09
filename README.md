# Agience Observe

[![License](https://img.shields.io/badge/license-AGPL--3.0--only-blue)](LICENSE)
[![Python](https://img.shields.io/badge/python-3.11%2B-3776AB?logo=python&logoColor=white)](package/install/cli/agience.py)
[![Windows](https://img.shields.io/badge/tray%20app-Windows-0078D4?logo=windows&logoColor=white)](package/manager/)
[![Sponsor](https://img.shields.io/badge/Sponsor-Agience-EA4AAA?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/Agience)

**The installables and the runtimes — where the platform becomes something an operator can run.**

Observe holds the installers, the host packaging, the seed corpora, the backup and restore
procedure, and the bundle builder. A node installs into a virtualenv: no container, no image to
pull.

## Install a node

Two ways in, the same shape underneath.

```bash
python package/install/cli/agience.py install   # any platform
python package/install/cli/agience.py start     # run the services in the foreground
python package/install/cli/agience.py status    # what is installed, and where
```

```powershell
package\manager\installer\build.ps1    # Windows: builds the MSI into installer\out\
```

Both find a Python, build a private environment, install the service distributions into it, generate
the key material and seed the trust.

Everything lands under one directory — `--home`, default `~/.agience` — holding the virtualenv, the
key material and the databases. Removing that directory removes the node and takes nothing else with
it.

**A virtualenv, never the machine's Python.** The services pin exact versions, and installing those
into whatever interpreter is already present breaks unrelated work on the machine.

**Loopback.** Every service binds `127.0.0.1` — 8080, 8082, 8085 and 8091. Serving a domain means
putting a proxy in front, rather than binding wider.

## The tray application

[`package/manager/`](package/manager/) is a Windows tray application that installs, configures,
starts and stops the services, and removes itself with the data as a separate decision.

**The process owns the services.** They are its children, they sit in its job object, and they die
with it — which is why the menu reads *"Exit (stops the services)"*. It supervises
`python -m uvicorn <service>:app` directly: one executable owning the Python processes.

## Seeds

[`package/seeds/`](package/seeds/) holds three seed corpora — `admin`, `platform` and `user` — each
an `artifacts/` and a `grants/` directory. The store ships bare and applies none of this at boot; a
domain delivers its founding catalog and grants explicitly.

```bash
./package/pack-seeds.sh    # build the distributable tarball
./seed-platform.sh         # apply the platform corpus to a running node, over the API
```

The node reads a seeds directory at `AGIENCE_SEEDS_ROOT` and applies it via
`POST /api/system/seed`.

## Backup and restore

A node's state is three things: the store — one SQLite file plus the content CAS tree — the trust
seed (`KEYS_DIR`: RSA identities, `encryption.key`, the authority manifest), and the identity
service's own SQLite where a node runs one.

```bash
./backup.sh                 # all three into one portable tarball
FORCE=1 ./restore.sh <tar>  # destructive; the node must be stopped
```

`restore.sh` overwrites the lattice and, unless `RESTORE_STORE_ONLY` is set, the trust keys, content
and identity state as well. It is guarded by `FORCE=1` and requires the node stopped: restoring a
SQLite file underneath a process holding it open corrupts both.

[`BACKUP.md`](BACKUP.md) is the full procedure. The key material travels separately from the data.

## Bundles

[`build_bundles.py`](build_bundles.py) is the command that rebuilds the operator payloads from
source. It holds no copy of the bundling logic — it loads the producer by the pinned path the spec
declares, so this repo has no import edge to the source tree.

```bash
python build_bundles.py            # rebuild every group
python build_bundles.py --check    # report drift, write nothing
python build_bundles.py fetch      # rebuild one group
```

Verification lives outside the producer, on the theory that a producer grading its own homework
catches nothing its own reader gets wrong.

## Layout

| path | what it is |
|---|---|
| [`package/install/cli/`](package/install/cli/) | the cross-platform installer: `agience.py` (install · start · status) and `init.py`, one-shot key, identity and authority-manifest generation. Stdlib only |
| [`package/manager/`](package/manager/) | the Windows tray application and MSI — `src/` for the application, `installer/` for the package |
| [`package/hosts/`](package/hosts/) | host packaging: `browser` (the extension) and `desktop` (the relay host and its supervisor) |
| [`package/seeds/`](package/seeds/) | the seed corpora — `admin`, `platform`, `user` |
| [`package/pack-seeds.sh`](package/pack-seeds.sh) | builds the distributable seed tarball |
| [`build_bundles.py`](build_bundles.py) | the bundle builder |
| [`backup.sh`](backup.sh) · [`restore.sh`](restore.sh) · [`seed-platform.sh`](seed-platform.sh) | the operator scripts |
| [`BACKUP.md`](BACKUP.md) | the operator documentation |

Security issues: email **connect@agience.ai** rather than opening a public issue.

Dual-licensed — see [`LICENSE`](LICENSE), [`COMMERCIAL_LICENSE.md`](COMMERCIAL_LICENSE.md),
[`NOTICE`](NOTICE) and [`CLA.md`](CLA.md).
