# Bootstrap an Agience node on Windows, from nothing.
#
#     irm https://get.agience.ai/install.ps1 | iex
#
# This script does three things and stops: find a Python, fetch this repository, and hand off to
# `package/install/cli/agience.py`, which is the real installer. It holds no copy of the install
# logic -- a bootstrap that knows how to build an environment is a second installer that drifts from
# the first, silently, because neither fails when the other changes. The POSIX twin is
# `install.sh`; keep the two the same shape.
#
# ## Everything lands under one directory
#
# `$env:AGIENCE_HOME` (default `~\.agience`) holds the source checkout, the virtualenv, the key
# material and the databases. Removing that directory removes the node and takes nothing else with
# it -- the property `agience.py` already promises, and which a checkout anywhere else would break.
#
# ## Run under `iex`, so no `param()` block
#
# `irm ... | iex` executes this text with no file and no arguments, so a `param()` block would be a
# syntax error at the point of use. Configuration is by environment variable for that reason, not by
# preference.

$ErrorActionPreference = 'Stop'

$AgienceHome = if ($env:AGIENCE_HOME) { $env:AGIENCE_HOME } else { Join-Path $HOME '.agience' }
$AgienceSrc  = Join-Path $AgienceHome 'src'
$Repo        = if ($env:AGIENCE_REPO) { $env:AGIENCE_REPO } else { 'https://github.com/Agience/agience-observe.git' }
$Branch      = if ($env:AGIENCE_BRANCH) { $env:AGIENCE_BRANCH } else { 'main' }

function Say([string]$m) { Write-Host "[agience] $m" }
function Die([string]$m) { Write-Host "[agience] $m" -ForegroundColor Red; exit 1 }

# -- Find a Python 3.11 or newer ---------------------------------------------------------------
# The floor comes from the packages: all five declare `requires-python >= 3.11`. Checked here as
# well as in `agience.py` so a missing prerequisite is named up front rather than surfacing as a
# resolver error three minutes into a pip run.
#
# ! `py -3.11` IS TRIED BEFORE BARE `python`. Windows ships an App Execution Alias at
# `python.exe` that is not an interpreter at all -- it opens the Microsoft Store. It exits 9009 and
# prints nothing useful, so a bootstrap that takes the bare name first fails on a stock machine with
# a message about a missing command rather than about the alias. The `py` launcher, when present, is
# the reliable way to ask for a specific version.
function Find-Python {
    $candidates = @()
    if (Get-Command py -ErrorAction SilentlyContinue) {
        foreach ($v in '3.14', '3.13', '3.12', '3.11') {
            $candidates += , @('py', @("-$v"))
        }
    }
    foreach ($n in 'python3', 'python') {
        if (Get-Command $n -ErrorAction SilentlyContinue) { $candidates += , @($n, @()) }
    }

    foreach ($c in $candidates) {
        $exe = $c[0]; $pre = $c[1]
        try {
            # NOT `$args` -- that is an automatic variable in PowerShell (the caller's argument
            # array), and assigning to it inside a function is both a shadowing hazard and, when
            # splatted back with `@args`, a different thing than the local intends.
            $probe = @($pre) + @('-c', 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)')
            & $exe @probe 2>$null
            if ($LASTEXITCODE -eq 0) { return , @($exe, $pre) }
        } catch {
            # An alias or a broken shim: not an interpreter. Try the next candidate rather than
            # failing here, which is the whole reason this loop exists.
            continue
        }
    }
    return $null
}

$found = Find-Python
if (-not $found) {
    Die "no Python 3.11 or newer found. Install one and run this again -- https://www.python.org/downloads/`n          (if 'python' opens the Microsoft Store, that is Windows' App Execution Alias, not an interpreter: turn it off under Settings > Apps > Advanced app settings > App execution aliases)"
}
$PyExe = $found[0]; $PyPre = $found[1]
# Build the argument array in a variable and splat it. An inline `@(...)` in argument position is
# parsed in PowerShell's *argument* mode, where the quoting inside this particular expression is
# ambiguous and fails to parse -- so the array is built in expression mode first.
#
# !! NO EMBEDDED DOUBLE QUOTES IN THIS SNIPPET. Windows PowerShell 5.1 strips `"` when it hands an
# argument to a NATIVE executable, so the obvious spelling -- print(".".join(...)) -- arrives at
# python as print(..join(...)) and dies with a SyntaxError. That failure is quiet: the probe writes
# to stderr, $ver comes back empty, and the script prints "using python ()" and carries on. Measured
# on 5.1, 2026-09-09. `sys.version.split()` needs no quotes and cannot be mangled this way.
$VerScript = 'import sys; print(sys.version.split()[0])'
$VerArgs = $PyPre + @('-c', $VerScript)
$ver = & $PyExe @VerArgs
if (-not $ver) { Die "found $PyExe but could not read its version -- refusing to install against an interpreter that will not answer" }
Say "using $PyExe $($PyPre -join ' ') ($ver)"

# -- Fetch the source --------------------------------------------------------------------------
# git when it is there, a zip when it is not. The zip path exists because a fresh Windows install
# has no git, and "install git first" is a worse first instruction than one that works.
New-Item -ItemType Directory -Force -Path $AgienceHome | Out-Null

if (Get-Command git -ErrorAction SilentlyContinue) {
    if (Test-Path (Join-Path $AgienceSrc '.git')) {
        Say "updating $AgienceSrc"
        git -C $AgienceSrc fetch --depth 1 origin $Branch
        if (-not $?) { Die "git fetch failed" }
        # `reset --hard`, not `pull`: this checkout is ours, not the operator's working tree, and a
        # merge conflict here would strand the bootstrap with no way forward it could explain.
        git -C $AgienceSrc reset --hard "origin/$Branch"
    } else {
        Say "fetching $Repo"
        if (Test-Path $AgienceSrc) { Remove-Item -Recurse -Force $AgienceSrc }
        git clone --depth 1 --branch $Branch $Repo $AgienceSrc
        if (-not $?) { Die "git clone failed" }
    }
} else {
    Say "git not found -- fetching a zip instead"
    $zipUrl = ($Repo -replace '\.git$', '') + "/archive/refs/heads/$Branch.zip"
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "agience-observe-$([guid]::NewGuid()).zip"
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) "agience-observe-$([guid]::NewGuid())"
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $tmp -UseBasicParsing
        Expand-Archive -Path $tmp -DestinationPath $stage -Force
        if (Test-Path $AgienceSrc) { Remove-Item -Recurse -Force $AgienceSrc }
        # GitHub wraps its archives in a `agience-observe-<branch>/` directory. Move that one child
        # up so the layout matches a clone and the handoff below is the same either way.
        $inner = Get-ChildItem -Path $stage -Directory | Select-Object -First 1
        if (-not $inner) { Die "the downloaded archive was empty" }
        Move-Item -Path $inner.FullName -Destination $AgienceSrc
    } finally {
        Remove-Item -Force -ErrorAction SilentlyContinue $tmp
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $stage
    }
}

$Installer = Join-Path $AgienceSrc 'package\install\cli\agience.py'
if (-not (Test-Path $Installer)) {
    Die "fetched the source but $Installer is not there -- the repository layout has changed and this bootstrap is stale"
}

# -- Hand off to the real installer ------------------------------------------------------------
# `--home` is a GLOBAL option and precedes the subcommand. Getting this backwards is not a silent
# failure but it is an immediate one, and this is the line that would carry it to every new operator.
Say "handing off to the installer"
$InstallArgs = $PyPre + @($Installer, '--home', $AgienceHome, 'install')
& $PyExe @InstallArgs
if ($LASTEXITCODE -ne 0) { Die "the installer exited $LASTEXITCODE" }

# `$PyPre` is empty whenever the interpreter was found by bare name rather than through the `py`
# launcher, and interpolating it directly leaves a double space in the middle of a line whose whole
# purpose is to be copied and pasted. Join the non-empty parts instead.
$launchParts = @($PyExe) + $PyPre + @("`"$Installer`"", '--home', "`"$AgienceHome`"")
$launch = ($launchParts | Where-Object { $_ -ne '' }) -join ' '
Write-Host ""
Say "installed. start the node with:"
Write-Host ""
Write-Host "    $launch start"
Write-Host ""
Say "then check it:"
Write-Host ""
Write-Host "    $launch status"
Write-Host ""
