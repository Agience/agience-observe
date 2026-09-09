# Contributing to Agience Observe

## Build and test

```bash
python -m pytest -q
```

The cross-platform installer is **stdlib only** — it runs before there is an environment to install
into. Keep it that way.

`build_bundles.py` holds no copy of the bundling logic. A change to how a bundle is produced belongs
in the producer it loads, not here.

## Contributing

Fork, branch from `main`, sign off every commit (`git commit -s`) to certify the
[DCO](https://developercertificate.org/), open a PR. Commit format: `fix:` · `feat(scope):` ·
`docs:` · `test:` · `chore:`.

**Sign the CLA.** This project is AGPL-3.0-only **or** commercially licensed
([`COMMERCIAL_LICENSE.md`](COMMERCIAL_LICENSE.md)), so the project must hold the right to relicense
every line it ships. The bot checks on PR open and links [`CLA.md`](CLA.md).

Dual-licensed — see [`LICENSE`](LICENSE), [`COMMERCIAL_LICENSE.md`](COMMERCIAL_LICENSE.md),
[`NOTICE`](NOTICE) and [`CLA.md`](CLA.md).
