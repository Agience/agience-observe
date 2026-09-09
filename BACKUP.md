# Agience — backup & restore

A sovereign node's state is:

| Component | What | Captured by |
|---|---|---|
| **Lattice** (Mantle's store) | one SQLite file (`mantle.db`) + the content CAS tree on the mantle volume — artifacts, grants, settings, people, KEK-wrapped key material | sqlite3 **online-backup** snapshot + tree copy |
| **Trust seed** (`KEYS_DIR`) | RSA identities, `encryption.key`, `authority.manifest.json` | filesystem copy |
| **Origin SQLite** (if this node runs one) | identity DB | filesystem copy |
| **Content** (MinIO — local S3 edge) | encrypted object blobs + SSE cells | filesystem copy (skippable) |

`backup.sh` captures all of them into one portable `.tar.gz`; `restore.sh` puts them back.

> The lattice holds the envelope-**encrypted** key material and grants — it is the crown
> jewels. The MinIO blobs are already encrypted at rest. Treat the tarball as secret
> (it contains `KEYS_DIR`); store it encrypted and access-controlled.

## Back up

```bash
# defaults target the source-build local stack (.data-local, agience-local-mantle-1)
./backup.sh                       # -> ./backups/agience-backup-<UTC>.tar.gz
./backup.sh nightly               # custom label

# full/home or a named stack: point it at that stack's data dir + mantle container
DATA_DIR=./.data-full MANTLE_CONTAINER=full-mantle-1 OUT_DIR=/srv/backups ./backup.sh

# lattice + keys only (skip the large MinIO object store — e.g. replicated separately)
SKIP_CONTENT=1 ./backup.sh
```

How the SQLite file is captured depends on whether mantle is running:

- **Mantle running** — the script snapshots via the **sqlite3 online-backup API**, executed
  inside the mantle container (`MANTLE_DB_PATH`, default `/app/.data/mantle/mantle.db`).
  A plain `cp` of a live SQLite db is torn by design; the online backup is consistent
  under active writes.
- **Mantle stopped** — the store is closed, so a direct copy is consistent; any
  `-wal`/`-shm` files are copied alongside, so a crashed-but-stopped store restores exactly.

The CAS tree (content-addressed, immutable blobs) is safe to copy live and is taken as a
tree copy, excluding the live db files already snapshotted. The tarball holds `mantle/`
(db + CAS), `keys/`, `origin/` (if present), `minio/` (unless skipped), and a `MANIFEST`
recording label, time, snapshot mode, and components.

Env: `DATA_DIR` (default `./.data-local`), `MANTLE_CONTAINER` (default
`agience-local-mantle-1`), `MANTLE_DB_PATH`, `OUT_DIR` (default `./backups`),
`SKIP_CONTENT=1`.

## Restore

**Destructive** — overwrites the lattice (and, unless `RESTORE_STORE_ONLY=1`, the trust
keys + content + Origin state). Guarded by `FORCE=1`, and the **mantle container must be
stopped**: restoring a SQLite file underneath a process holding it open corrupts both.

```bash
# 1) stop the stack (or at least mantle) — restore.sh requires mantle to be stopped first
python package/install/cli/agience.py status      # confirm nothing is serving

# 2) restore everything (lattice + keys + origin + content)
FORCE=1 ./restore.sh backups/agience-backup-<UTC>.tar.gz

# lattice only (leave on-disk keys/content/origin as-is):
FORCE=1 RESTORE_STORE_ONLY=1 ./restore.sh backups/agience-backup-<UTC>.tar.gz

# 3) restart the stack to pick up restored state
python package/install/cli/agience.py start
```

A tarball from an ArangoDB-based store has no `mantle/` component; `restore.sh` detects
this and stops with an explicit message rather than restoring a partial state.

## Notes

- **Consistency**: the online snapshot is point-in-time consistent for the SQLite file;
  the CAS tree is immutable content, so a live copy is safe. For a strict global snapshot
  across lattice **and** MinIO content under active writes, quiesce writers first (or stop
  the app services) — for a sovereign node this is a brief maintenance window.
- **Content at scale**: for large MinIO stores prefer object-store replication / `mc
  mirror` and back up lattice+keys with `SKIP_CONTENT=1`.
- **Schedule** it (cron / the `/loop` skill) and copy the tarball off-box.
