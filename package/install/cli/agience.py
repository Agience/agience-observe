#!/usr/bin/env python3
"""Install and run a sovereign Agience node — identity and encrypted memory, in a virtualenv.

    python agience.py install     # build the environment, install the services, seed the trust
    python agience.py start       # run the four services in the foreground
    python agience.py status      # what is installed and where

Everything lands under one directory (``--home``, default ``~/.agience``): the virtualenv, the key
material, and the databases. Removing that directory removes the node and takes nothing else with
it.

This is the same shape the tray application uses — `package/manager` finds a Python, builds a
private environment, installs the distributions into it and supervises
``python -m uvicorn <service>:app``. This script is that, for anyone not on Windows or not wanting
a tray icon.

## A virtualenv, never the machine's Python

The services pin exact versions — Origin alone pins fastapi, starlette, cryptography and uvicorn —
and installing those into whatever interpreter is already present is how an installer breaks
unrelated work on somebody's machine.

## Loopback

Every service binds 127.0.0.1 — Origin 8080, Mantle 8082, Crystal 8085, Ember 8091. A service on
the public interface answers past whatever header and path rules a proxy in front of it applies.
Serving a domain means putting something in front, not binding wider.

## Stdlib only

This file runs on the interpreter a person already has, before any environment exists. It shells
out to the virtualenv's own Python for everything that needs a dependency.
"""
from __future__ import annotations

import argparse
import os
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

#: The floor comes from the packages: all five declare `requires-python >= 3.11`.
MINIMUM_PYTHON = (3, 11)

#: Where the distributions come from when no checkout is given, in dependency order:
#:
#:     prism                            no dependencies at all
#:     crystal, mantle, origin          each depends on prism
#:     ember                            depends on prism, crystal and mantle
#:
#: pip resolves regardless of order, so this is for the reader and for the failure message: an
#: install that dies on the third line has three working packages behind it, and the order says
#: which.
#:
#: The REPOSITORY names and the DISTRIBUTION names are not the same everywhere — prism is built
#: from `agience-prism-py` and publishes as `agience-prism`. These are git URLs, so the repository
#: name is what belongs here; `REQUIRED` below is the other spelling.
GITHUB = "https://github.com/Agience"
PACKAGES = (
    ("prism (the wire)", f"git+{GITHUB}/agience-prism-py.git"),
    ("crystal (the gateway)", f"agience-crystal[service,ontology] @ git+{GITHUB}/agience-crystal.git"),
    ("mantle (the store)", f"agience-mantle[service,search,s3] @ git+{GITHUB}/agience-mantle.git"),
    ("origin (the authority)", f"git+{GITHUB}/agience-origin.git"),
    ("ember (the leaf)", f"git+{GITHUB}/agience-ember.git"),
)

#: The distribution names pip records, which is how an install is checked without running pip.
REQUIRED = ("agience-origin", "agience-mantle", "agience-crystal", "agience-ember")


class Service:
    """One service: how it is launched, and how it is proved to be serving.

    `app` is passed to `-m uvicorn`, so nothing depends on a console script being on PATH.

    `module` is the alternative for a service that is not an ASGI app. Ember is one: it serves an
    OpenAI-compatible `/v1` on a stdlib `http.server` that `ember.cli` constructs, so there is no
    `app` for uvicorn to import. `python -m ember.cli serve` keeps the same property `app` has —
    the launch does not depend on a console script being on PATH.

    Exactly one of `app` and `module` is given.

    `health` is a path that must answer 200, or None for "something is listening on the port" —
    the weakest honest claim, and the right one for a service that publishes no health route.
    """

    def __init__(self, name: str, port: int, health: str | None, log_level: str,
                 app: str | None = None, module: tuple[str, ...] | None = None):
        if (app is None) == (module is None):
            raise ValueError(f"{name}: give exactly one of `app` and `module`")
        self.name = name
        self.app = app
        self.module = module
        self.port = port
        self.health = health
        self.log_level = log_level

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def command(self, python: Path) -> list[str]:
        if self.app is not None:
            return [str(python), "-m", "uvicorn", self.app,
                    "--host", "127.0.0.1", "--port", str(self.port),
                    "--log-level", self.log_level]
        return [str(python), "-m", *self.module,
                "--host", "127.0.0.1", "--port", str(self.port)]

    def is_serving(self) -> bool:
        if self.health is None:
            return port_is_bound(self.port)
        return answers(self.url + self.health)

    @property
    def health_description(self) -> str:
        return self.health if self.health else f"a listener on port {self.port}"


#: Start order is a dependency order: Mantle verifies against an authority that must already be
#: publishing keys. Started together, Mantle reports a trust failure rather than a race, and the
#: person reading that goes looking at trust configuration.
#:
#: Origin's health rule is its key set, not its port. An Origin that is listening and publishes no
#: keys is an authority no peer could verify.
#:
#: Crystal comes after Mantle because the gateway reads the store at `MANTLE_URI` and calls Origin
#: at `ORIGIN_URI`; it starts either way and answers what it can reach, so this order buys a clean
#: log rather than a working boot. Ember is last: it is the only one that depends on all three.
#: Named rather than reached by index. The environment below is built from these, and `SERVICES[1]`
#: silently means a different service the moment one is inserted above it — which is the same class
#: of defect as the `MANTLE_URI` one recorded in `cmd_start`.
ORIGIN = Service("origin", 8080, "/.well-known/jwks.json", "info", app="origin.main:app")
MANTLE = Service("mantle", 8082, None, "warning", app="mantle.main:app")
CRYSTAL = Service("crystal", 8085, "/health", "info", app="crystal.main:app")
EMBER = Service("ember", 8091, "/health", "info", module=("ember.cli", "serve"))

SERVICES = (ORIGIN, MANTLE, CRYSTAL, EMBER)


# ── layout ──────────────────────────────────────────────────────────────────────────────────────

class Layout:
    """Every path this script writes, derived from one root."""

    def __init__(self, home: Path):
        self.home = home.expanduser().resolve()

    @property
    def runtime(self) -> Path:
        return self.home / "runtime"

    @property
    def python(self) -> Path:
        bin_dir = "Scripts" if os.name == "nt" else "bin"
        exe = "python.exe" if os.name == "nt" else "python"
        return self.runtime / bin_dir / exe

    @property
    def data(self) -> Path:
        return self.home / ".data"

    @property
    def keys(self) -> Path:
        return self.data / "keys"

    @property
    def site_packages(self) -> Path:
        if os.name == "nt":
            return self.runtime / "Lib" / "site-packages"
        major, minor = sys.version_info[:2]
        return self.runtime / "lib" / f"python{major}.{minor}" / "site-packages"


def say(message: str) -> None:
    print(f"[agience] {message}", flush=True)


def die(message: str) -> "None":
    print(f"[agience] {message}", file=sys.stderr, flush=True)
    raise SystemExit(1)


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    """Run a command, streaming its output. Raises `SystemExit` when it fails.

    Output is streamed rather than captured: an install takes minutes on a cold machine, and a
    terminal that says nothing for four minutes is one a person kills — after which the environment
    is half-built and the next run has to continue from there.
    """
    result = subprocess.run(command, **kwargs)
    if result.returncode != 0:
        die(f"failed ({result.returncode}): {' '.join(command)}")
    return result


# ── install ─────────────────────────────────────────────────────────────────────────────────────

def installed_version(layout: Layout, distribution: str) -> str | None:
    """The version pip recorded, or None. Read off the disk rather than by running pip.

    pip normalises a distribution name to underscores in the directory it writes, so
    `agience-origin` is recorded as `agience_origin-0.1.0.dist-info`.
    """
    prefix = distribution.replace("-", "_") + "-"
    try:
        for entry in layout.site_packages.glob(prefix + "*.dist-info"):
            return entry.name[len(prefix):-len(".dist-info")] or "installed"
    except OSError:
        return None
    return None


def cmd_install(args: argparse.Namespace) -> int:
    layout = Layout(Path(args.home))

    if sys.version_info[:2] < MINIMUM_PYTHON:
        die(f"Python {MINIMUM_PYTHON[0]}.{MINIMUM_PYTHON[1]} or newer is required; this is "
            f"{sys.version_info[0]}.{sys.version_info[1]}. Run this script with a newer one.")

    say(f"installing into {layout.home}")
    layout.home.mkdir(parents=True, exist_ok=True)

    if not layout.python.exists():
        say(f"creating the environment at {layout.runtime}")
        run([sys.executable, "-m", "venv", str(layout.runtime)])
    else:
        say(f"reusing the environment at {layout.runtime}")

    run([str(layout.python), "-m", "pip", "install", "--upgrade", "--quiet",
         "pip", "setuptools", "wheel"])

    for label, requirement in resolve_sources(args.source):
        say(f"installing {label}")
        run([str(layout.python), "-m", "pip", "install", requirement])

    # pip can exit 0 having installed nothing that was asked for — a resolver satisfying a
    # requirement from a cached wheel of the wrong name, a path that silently matched nothing.
    # Reporting success from the exit code alone is how an install comes back green over an
    # environment that cannot start a service.
    missing = [d for d in REQUIRED if installed_version(layout, d) is None]
    if missing:
        die("the install commands succeeded but the environment is still missing "
            + ", ".join(missing))

    seed_trust(layout)

    say("done. start the node with:")
    say(f"    python {Path(__file__).name} start --home {layout.home}")
    return 0


def resolve_sources(source: str | None) -> list[tuple[str, str]]:
    """What to install, from a local checkout when one is given and from git otherwise.

    A directory is only accepted when all five repositories are in it. A partial checkout is worse
    than none: pip installs the ones that are there, resolves the rest from an index that does not
    carry them, and the failure names a transitive dependency instead of the missing directory a
    person could actually fix.

    prism is the one whose directory is not its repository name — it lives at `agience-prism/py`,
    beside `js` and `c`.
    """
    if not source:
        return list(PACKAGES)

    root = Path(source).expanduser().resolve()
    expected = {
        "prism (the wire)": root / "agience-prism" / "py",
        "crystal (the gateway)": root / "agience-crystal",
        "mantle (the store)": root / "agience-mantle",
        "origin (the authority)": root / "agience-origin",
        "ember (the leaf)": root / "agience-ember",
    }
    absent = [str(p) for p in expected.values() if not (p / "pyproject.toml").is_file()]
    if absent:
        die(f"{root} is not a complete checkout; missing a pyproject.toml under: "
            + ", ".join(absent))

    # The extras match PACKAGES above, so a checkout install and a git install produce the same
    # environment. They diverged once, and the symptom was a gateway that booted from git and
    # failed to import fastapi from a checkout.
    return [
        ("prism (the wire)", str(expected["prism (the wire)"])),
        ("crystal (the gateway)", str(expected["crystal (the gateway)"]) + "[service,ontology]"),
        ("mantle (the store)", str(expected["mantle (the store)"]) + "[service,search,s3]"),
        ("origin (the authority)", str(expected["origin (the authority)"])),
        ("ember (the leaf)", str(expected["ember (the leaf)"])),
    ]


def seed_trust(layout: Layout) -> None:
    """Generate key material and the authority manifest, once.

    `init.py` skips what already exists, so this is safe to run again — and it is the only place
    key material is written. The services read keys and never generate them.
    """
    init = Path(__file__).resolve().parent / "init.py"
    if not init.is_file():
        die(f"{init} is missing — it is what generates the key material, and this script cannot "
            "seed a node without it")

    if (layout.keys / "authority.manifest.json").is_file():
        say("trust already seeded; leaving it alone")
        return

    say("generating the trust seed")
    layout.data.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env["AGIENCE_INIT_DATA_DIR"] = str(layout.data)
    # `init.py` defaults every service URI to a container hostname, which nothing resolves here.
    # The manifest it writes is what peers verify against, so these have to be the addresses the
    # services are actually reachable at.
    env.setdefault("AUTHORITY_ORIGIN_URI", ORIGIN.url)
    env.setdefault("AUTHORITY_MANTLE_URI", MANTLE.url)
    env.setdefault("MANTLE_URI", MANTLE.url)
    env.setdefault("AUTHORITY_ISSUER", ORIGIN.url)
    run([str(layout.python), str(init)], env=env)

    token = layout.keys / "bootstrap.token"
    if token.is_file():
        print()
        print("===================== BOOTSTRAP TOKEN =====================")
        print(token.read_text(encoding="utf-8").strip())
        print("===========================================================")
        print("Claim it once via POST /auth/bootstrap/claim to create the first operator.")
        print()


# ── run ─────────────────────────────────────────────────────────────────────────────────────────

def answers(url: str, timeout: float = 5.0) -> bool:
    """Does this URL answer 200?

    Five seconds, not two. A health route that reports on a dependency does I/O before it answers:
    Crystal's `/health` contacts Mantle, and when Mantle was unreachable that route took 2.3s to
    return its 200 — against a 2.0s probe, so readiness timed out on a service that was serving.
    The unreachable Mantle was a separate defect and is fixed; the margin is what stops the next
    slow dependency from reading as a dead service.
    """
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:
            return response.status == 200
    except (urllib.error.URLError, OSError, ValueError):
        return False


def port_is_bound(port: int, timeout: float = 2.0) -> bool:
    """Is anything accepting connections on this loopback port?"""
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=timeout):
            return True
    except OSError:
        return False


def cmd_start(args: argparse.Namespace) -> int:
    layout = Layout(Path(args.home))
    if not layout.python.exists():
        die(f"no environment at {layout.runtime} — run `install` first")
    if not (layout.keys / "authority.manifest.json").is_file():
        die(f"no trust seed under {layout.keys} — run `install` first")

    env = dict(os.environ)
    env["KEYS_DIR"] = str(layout.keys)
    env["AGIENCE_BASE_DIR"] = str(layout.home)
    env["AGIENCE_NO_DOTENV"] = "1"
    env.setdefault("AUTHORITY_ISSUER", ORIGIN.url)
    env.setdefault("ORIGIN_URI", ORIGIN.url)
    # `MANTLE_URI` defaults to `http://localhost:8081` in both crystal and mantle — a developer
    # default, and the port this installer actually serves the store on is 8082. Unset, the gateway
    # came up pointed at nothing: `/health` reported `mantle_reachable: false` and the event-driver
    # retried a refused connection in a loop. Seeding it was already done for `init.py`; the
    # services need it too, and they are the half that was missing.
    env.setdefault("MANTLE_URI", MANTLE.url)
    env.setdefault("CRYSTAL_URI", CRYSTAL.url)
    env.setdefault("MANTLE_LATTICE_PATH", str(layout.data / "mantle" / "mantle.db"))

    started: list[tuple[Service, subprocess.Popen]] = []
    try:
        for service in SERVICES:
            say(f"starting {service.name} on {service.url}")
            started.append((service, subprocess.Popen(service.command(layout.python), env=env)))

            # Wait for this one before starting the next: the order is a dependency order, and a
            # peer that comes up against an authority with no key set reports a trust failure.
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                if service.is_serving():
                    say(f"{service.name} is serving")
                    break
                if started[-1][1].poll() is not None:
                    die(f"{service.name} exited before it began serving")
                time.sleep(1)
            else:
                die(f"{service.name} did not reach {service.health_description} within 90s")

        say("running. Ctrl-C to stop.")
        while True:
            for service, process in started:
                if process.poll() is not None:
                    die(f"{service.name} exited ({process.returncode})")
            time.sleep(1)
    except KeyboardInterrupt:
        say("stopping")
        return 0
    finally:
        for _, process in reversed(started):
            if process.poll() is None:
                process.send_signal(signal.SIGTERM if os.name != "nt" else signal.SIGBREAK)
        for _, process in reversed(started):
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()


def cmd_status(args: argparse.Namespace) -> int:
    layout = Layout(Path(args.home))
    print(f"home       {layout.home}")
    print(f"runtime    {layout.runtime}"
          f"{'' if layout.python.exists() else '   (absent — run install)'}")
    print(f"keys       {layout.keys}"
          f"{'' if (layout.keys / 'authority.manifest.json').is_file() else '   (not seeded)'}")
    for distribution in REQUIRED:
        version = installed_version(layout, distribution) or "not installed"
        print(f"  {distribution:20s} {version}")
    for service in SERVICES:
        state = "serving" if service.is_serving() else "not answering"
        print(f"  {service.name:20s} {service.url}  {state}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="agience", description=__doc__.splitlines()[0],
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--home", default=os.environ.get("AGIENCE_HOME", "~/.agience"),
                        help="where the environment, keys and data live (default: ~/.agience)")
    sub = parser.add_subparsers(dest="command", required=True)

    install = sub.add_parser("install", help="build the environment and seed the trust")
    install.add_argument("--source", default=None,
                         help="a directory holding agience-prism, agience-crystal, "
                              "agience-mantle, agience-origin and agience-ember; installs from "
                              "git when omitted")
    install.set_defaults(func=cmd_install)

    sub.add_parser("start", help="run the services in the foreground").set_defaults(func=cmd_start)
    sub.add_parser("status", help="what is installed and where").set_defaults(func=cmd_status)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
