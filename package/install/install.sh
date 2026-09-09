#!/bin/sh
# Bootstrap an Agience node on macOS or Linux, from nothing.
#
#     curl -fsSL https://get.agience.ai/install.sh | sh
#
# This script does three things and stops: find a Python, fetch this repository, and hand off to
# `package/install/cli/agience.py`, which is the real installer. It holds no copy of the install
# logic — a bootstrap that knows how to build an environment is a second installer that drifts from
# the first, silently, because neither fails when the other changes.
#
# ## Everything lands under one directory
#
# `$AGIENCE_HOME` (default `~/.agience`) holds the source checkout, the virtualenv, the key material
# and the databases. Removing that directory removes the node and takes nothing else with it. That
# is the property `agience.py` already promises, and cloning the source anywhere else would break
# it — a checkout in `/tmp` disappears, and one in the working directory leaves the machine with a
# node whose source is wherever the operator happened to be standing.
#
# ## POSIX sh, not bash
#
# This runs before anything is installed, on whatever `/bin/sh` the machine has. Debian's is `dash`.
# No `[[`, no arrays, no `local` — all three are bashisms that fail on a system this must work on.

set -eu

AGIENCE_HOME="${AGIENCE_HOME:-$HOME/.agience}"
AGIENCE_SRC="$AGIENCE_HOME/src"
REPO="${AGIENCE_REPO:-https://github.com/Agience/agience-observe.git}"
BRANCH="${AGIENCE_BRANCH:-main}"

say() { printf '[agience] %s\n' "$*"; }
die() { printf '[agience] %s\n' "$*" >&2; exit 1; }

# ── Find a Python 3.11 or newer ───────────────────────────────────────────────────────────────
# The floor comes from the packages: all five declare `requires-python >= 3.11`. Checked here as
# well as in `agience.py` so the failure names the missing prerequisite, rather than surfacing as a
# resolver error three minutes into a pip run.
#
# `python3.13`, `python3.12`, `python3.11` are tried by name BEFORE bare `python3`, because a
# machine with an old system `python3` and a newer one installed alongside is the common case, and
# taking the bare name first finds the wrong one on exactly those machines.
find_python() {
    for candidate in python3.14 python3.13 python3.12 python3.11 python3 python; do
        if command -v "$candidate" >/dev/null 2>&1; then
            if "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)' 2>/dev/null; then
                command -v "$candidate"
                return 0
            fi
        fi
    done
    return 1
}

PYTHON="$(find_python)" || die "no Python 3.11 or newer found. Install one and run this again — see https://www.python.org/downloads/"
# `sys.version.split()` rather than joining `version_info`, to keep this identical to the twin in
# `install.ps1` — which cannot use the joined spelling at all, because PowerShell 5.1 strips
# embedded double quotes when passing an argument to a native executable and turns `"."` into
# nothing. sh has no such problem; the two are kept the same so a fix to one is a fix to both.
say "using $PYTHON ($("$PYTHON" -c 'import sys; print(sys.version.split()[0])'))"

# ── Fetch the source ──────────────────────────────────────────────────────────────────────────
# git when it is there, a tarball when it is not. The tarball path exists because a fresh container
# or a locked-down workstation often has curl and no git, and "install git first" is a worse first
# instruction than one that works.
mkdir -p "$AGIENCE_HOME"

if command -v git >/dev/null 2>&1; then
    if [ -d "$AGIENCE_SRC/.git" ]; then
        say "updating $AGIENCE_SRC"
        git -C "$AGIENCE_SRC" fetch --depth 1 origin "$BRANCH"
        # `reset --hard`, not `pull`: this checkout is ours, not the operator's working tree, and a
        # merge conflict here would strand the bootstrap with no way forward it could explain.
        git -C "$AGIENCE_SRC" reset --hard "origin/$BRANCH"
    else
        say "fetching $REPO"
        rm -rf "$AGIENCE_SRC"
        git clone --depth 1 --branch "$BRANCH" "$REPO" "$AGIENCE_SRC"
    fi
else
    command -v curl >/dev/null 2>&1 || die "neither git nor curl is available; one of them is needed to fetch the source"
    say "git not found — fetching a tarball instead"
    tarball="${REPO%.git}/archive/refs/heads/$BRANCH.tar.gz"
    rm -rf "$AGIENCE_SRC"
    mkdir -p "$AGIENCE_SRC"
    # `--strip-components=1` drops the `agience-observe-main/` wrapper GitHub puts in its archives,
    # so the layout matches a clone and the handoff path below is the same either way.
    curl -fsSL "$tarball" | tar -xz -C "$AGIENCE_SRC" --strip-components=1
fi

INSTALLER="$AGIENCE_SRC/package/install/cli/agience.py"
[ -f "$INSTALLER" ] || die "fetched the source but $INSTALLER is not there — the repository layout has changed and this bootstrap is stale"

# ── Hand off to the real installer ────────────────────────────────────────────────────────────
# `--home` is a GLOBAL option and precedes the subcommand. Getting this backwards is not a silent
# failure but it is an immediate one, and this is the line that would carry it to every new operator.
say "handing off to the installer"
"$PYTHON" "$INSTALLER" --home "$AGIENCE_HOME" install

cat <<EOF

[agience] installed. start the node with:

    $PYTHON $INSTALLER --home $AGIENCE_HOME start

[agience] then check it:

    $PYTHON $INSTALLER --home $AGIENCE_HOME status

EOF
