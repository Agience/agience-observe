# Installing a node

A sovereign node is identity (Origin) and encrypted memory (Mantle), each an ordinary Python
package running in one virtualenv. There is no container and no image to pull.

```sh
python agience.py install          # environment, services, trust seed
python agience.py start            # run both, in the foreground
python agience.py status           # what is installed, and what is answering
```

Everything lands under `--home` (default `~/.agience`): the virtualenv in `runtime/`, key material
and databases in `.data/`. Removing that directory removes the node.

Requires Python 3.11 or newer — the floor both services declare. Nothing here installs Python.

## What install does

1. Builds a virtualenv, never touching the machine's Python. The services pin exact versions, and
   installing those into an interpreter someone already uses breaks unrelated work.
2. Installs prism, then Mantle, then Origin. The order is a dependency order.
3. Verifies the distributions are actually recorded on disk. pip can exit 0 having installed
   nothing that was asked for, and an install that reports success over an environment which cannot
   start a service is worse than one that fails.
4. Runs `init.py` once to generate key material and the authority manifest, and prints the
   **bootstrap token**. That token is single-use: present it to `POST /auth/bootstrap/claim` to
   create the first operator. Re-running skips what already exists.

Install from a checkout instead of from git with `--source <directory holding the three repos>`.

## What start does

Runs each service as `python -m uvicorn <app>` and waits for it to serve before starting the next —
Mantle verifies against an authority that must already be publishing keys, so started together it
reports a trust failure rather than a race.

| service | port | proved serving by |
|---|---|---|
| origin | 8080 | `/.well-known/jwks.json` answers 200 |
| mantle | 8082 | something is listening |

Origin's rule is its key set rather than its port: an Origin that is listening and publishes no
keys is an authority no peer could verify. Mantle publishes no health route, so the honest claim is
that the port is bound.

Both bind `127.0.0.1`. Serving a domain means putting a reverse proxy in front, not binding wider —
a service on the public interface answers past whatever rules that proxy applies.

## On Windows

`package/manager` does all of this behind a tray icon, and supervises the processes across logins.
This script is the same shape for anyone not on Windows, or not wanting a tray icon.
