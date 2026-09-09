# Agience

**A Windows tray application that runs Origin and Mantle.** It installs them, configures them,
starts and stops them, and removes itself — with your data as a separate decision.

```powershell
installer\build.ps1     →  installer\out\Agience-0.1.0-x64.msi
```

No Docker. No bash. No Python scripts in the observer. One executable that owns the Python
processes.

---

## What it is

**This process owns the services.** They are its children, they sit in its job object, and they
die with it — which is why the menu says *"Exit (stops the services)"* rather than *"Exit"*.

That is a deliberate reversal of what this application used to be. It was a read-only *sentinel*: a
window onto `agience-supervise`, a bash supervisor it watched over an HTTP contract and never
controlled. One process supervising its own children is simpler to install, to stop and to remove
than two supervisors that have to agree with each other — and it is the only shape a stranger can
install from one file.

**Which means the services do not run without a login.** There is no Windows Service and no
scheduled task. The tray starts at logon and the services start with it. A machine that must serve
while nobody is signed in needs a service host this deliberately does not have.

**Ember is not here yet.** Origin and Mantle are the seed — an authority to say who anyone is, and
a store to ground what they know. Adding a kind is additive: one record in `ServiceCatalog.cs` and
one entry in the install plan.

## Instances, not services

**A machine runs a LIST of instances, not "the Origin and the Mantle".** Two Origins on two
domains is an ordinary arrangement. So is a Mantle with no Origin beside it, verifying against an
authority somewhere else — which is what joining somebody's network looks like. A screen with one
Origin section and one Mantle section makes both inexpressible.

Each instance carries its own name, domain, port, data directory and authority:

| | Origin | Mantle |
|---|---|---|
| what it is | identity: principals, grants, and the keys peers verify against | memory: the artifact store, its version lineage and its search index |
| started as | `uvicorn origin.main:app` | `uvicorn mantle.main:app` |
| first port offered | 8080 | 8082 |
| proved up by | `GET /.well-known/jwks.json` → 200 | its port is bound |

**Origin is proved by its keys, not by its port**, and it is the one exception. An Origin that is
listening but publishes no key set is an authority no peer could ever verify — the shell
supervisor's own preflight refuses a node in that state, and a green dot over it would be this
application's most expensive lie.

**Every Origin starts before any Mantle**, whatever domains they are on. A Mantle verifies against
an authority that must already be publishing keys; started interleaved, it reports a *trust* failure
rather than a race, and the person reading that goes looking at trust configuration.

**Every instance gets its own data directory**, and this is the failure the whole model exists to
prevent. Two Mantles sharing one directory share one lattice, one keys directory and — worst,
because nothing errors — one SSE index. Mantle's own `.env.example` names the symptom: *"Both come
up healthy while answering searches from each other's postings."* Sharing one is refused, not warned
about.

## Domains and authority

You type a domain. Everything else follows from it.

| domain | answers at |
|---|---|
| `home.agience.ai` on an Origin | `https://origin.home.agience.ai` |
| `home.agience.ai` on a Mantle | `https://mantle.home.agience.ai` |
| *(empty)* | `http://127.0.0.1:<port>` |

An empty domain is a complete, working configuration and not a missing value: it is what a laptop
wants. A domain is what you set when something other than this machine has to reach it, and
something in front then terminates TLS on 443.

**Which authority an instance trusts** is either worked out or chosen:

- An Origin is its own authority, unless told to join another.
- A Mantle takes the Origin on its own domain; failing that, the only Origin configured.
- Otherwise you pick one from the list, or give the address of one elsewhere.

**Two candidate Origins is an ambiguity and is never guessed.** Picking one would silently bind
that store's entire trust to whichever happened to sort first — a decision nobody made, visible
nowhere, and wrong half the time. It is reported as a question instead.

**The issuer and the address are two different values, and collapsing them breaks one case or the
other.** `AUTHORITY_ISSUER` is what a token must *say* — always the public form. `ORIGIN_URI` is
where to *send* a request — loopback when the Origin is on this machine, because the public name may
not resolve here, or may not point here at all. The compose files make the same split for the same
reason.

## What the icon means

The Agience mark on a filled disc, with a small status dot bottom-right.

| dot | level | means |
|---|---|---|
| dark grey | `NotReady` | no Python environment yet — setup is unfinished |
| light grey | `NotRunning` | nothing configured, or everything stopped |
| red | `Foreign` | a port is held by a process this application did not start |
| amber | `Degraded` | at least one enabled instance is not serving |
| blue | `Starting` | something is coming up and cannot yet be described either way |
| green | `Healthy` | every enabled instance is serving |

**The mark is drawn as a solid disc rather than as line art, and that is a 16-pixel remedy.**
Measured against the real tray: at 16 pixels the mark's thin interlaced strokes antialias to a
pale lilac smudge, and beside WhatsApp's green disc and Defender's shield it was visibly the faintest
thing on the taskbar. The large app icon — Explorer, the Start menu, the shortcut — is still the bare
mark, where the strokes have the pixels they need.

**The dot carries no glyph.** At the size it has to be to leave the mark legible, a glyph is three
or four pixels: it does not read as a symbol, it just muddies the one thing the dot has to say. The
colours are separated by *luminance* as well as hue so they survive colour blindness, and everywhere
a person can actually read something — the tooltip, the menu headline, every row in the window — the
state is a **word and a sentence**, never a colour.

Rules the verdict is held to, each with a test named for it in `TrayStateTests.cs`:

- **Nothing configured says so**, rather than reporting a fault over a machine nobody has told what
  to run — and it outranks "no runtime", because there is nothing to install a runtime *for* yet.
- **No enabled instances is not all-instances-up.** An empty list satisfies "none of them is down"
  vacuously.
- **A starting instance never reads as healthy.** Green over something that has not answered yet is
  the one way this application can actively mislead.
- **Foreign outranks degraded**, and keeps its own colour. On node 71 two abandoned processes once
  held `:80` and `:443` and the status surface reported the proxy up, serving the squatter's 200.
- **A disabled instance is not a failure.** An icon that is always amber is never read.
- **An unrecognised state fails closed**, or every state a later build invents is born green on every
  installation already out there.

## The tray icon has a permanent identity

**It is registered with a fixed GUID, and `System.Windows.Forms.NotifyIcon` cannot do that.**
Without `NIF_GUID`, Windows identifies a tray icon by executable path plus a window handle — so
every build directory, every install location and every rename is a *different* icon to the shell,
hidden by default all over again.

Measured: `HKCU\Control Panel\NotifyIconSettings` had accumulated **five** entries for this one
application before the fix. Shipping that means every update silently un-pins the icon and the
person goes hunting through a list of thirty entries to get it back. With the GUID there is one
entry, and it was verified to survive the move from `bin\Debug` into `Program Files` with its
promoted flag intact.

`TrayIcon.cs` therefore drives `Shell_NotifyIcon` directly. Three things came with that:

- **`TaskbarCreated` is handled**, so the icon returns when Explorer restarts. The window is hidden
  but *not* message-only — a message-only window receives no broadcasts, so the icon would vanish
  permanently on the first Explorer restart, which looks exactly like a crash.
- **`SetForegroundWindow` before the menu** — the oldest bug in tray applications. Without it the
  menu will not dismiss when you click elsewhere.
- **Version-4 callbacks**, so the menu opens where the shell asked rather than at the cursor.

## Setup, and the one prerequisite

**Agience does not bundle Python and does not install it for you.** Setup finds the interpreters
on the machine, and if there is no Python 3.11 or newer it says so and offers the official download
page. Silently running somebody else's installer is not a thing a tray application should do.

It then builds a **private virtualenv** under your profile and installs Origin and Mantle into it.

**Never the machine's Python.** These packages pin exact versions — Origin alone pins fastapi,
starlette, cryptography and uvicorn — and installing those into whatever interpreter somebody
already had is how an installer breaks unrelated work on their machine. This environment is ours, it
is removable, and removing it takes nothing else with it.

**They are not on PyPI**, which is a fact about them rather than a choice made here — their own
pip says so. The two real sources are a checkout on this disk (`agience-origin`,
`agience-mantle`, `agience-prism`) and the git remote. A partial checkout is refused by name: pip
would otherwise install what it found, resolve the rest from an index that does not carry them, and
fail naming a transitive dependency instead of the missing directory.

Install order is a dependency order: Prism → Mantle → Origin.

## Stopping actually stops

**Everything is in a Windows job object with `KILL_ON_JOB_CLOSE`.** Terminating a process on
Windows does not terminate its children, and a uvicorn worker has them. Without the job, "stop"
reports success, the icon goes grey, and the port stays held by an orphan the tray no longer knows
about — so the next start fails to bind against a process nothing will admit to owning.

It also covers the case no stop path can: if the tray is killed — Task Manager, a crash, a forced
logoff — every handle closes, the job closes with them, and the services die too.

**The port is the source of truth, not the process handle.** A handle says a process exists; only
the TCP table says who holds the port. They disagree in the two states that cost time.

**Only an unexpected exit is relaunched.** Something that never bound its port is a configuration
problem, not a transient one; relaunching it turns a legible failure into a loop that fills a log and
never comes up. Backoff doubles from 4s and caps at two minutes.

**Removing an instance from the configuration does not stop its process**, and the status page
lists it as `(removed)` until you do. A running service that no surface lists is exactly the orphan
this application exists to prevent.

## Removing it

Three choices, and **keep-everything is the default**. It is not a close call: the data directories
hold the signing keys of identity authorities and the only copy of whatever has been stored. An
uninstaller that removed them by default is one mis-click from destroying something nobody backed
up, while the person believed they were removing a tray icon.

| choice | removes | keeps |
|---|---|---|
| Keep everything | the program | the environment, the keys, the stores, the settings |
| Remove the environment | the program, `runtime\` | the keys, the stores, the settings |
| Remove everything | all of it | nothing |

**The destructive choice is confirmed by typing, not by pressing OK.** An OK button on a warning
is pressed reflexively.

**The services are stopped before anything is deleted.** A running service holds its store open;
deleting the directory underneath it removes the entries and leaves the file — a store that exists,
opens, and has lost what it held, which is worse than either outcome a person chose between.

**A plain uninstall from Settings › Apps keeps your data**, because the MSI does not reference the
data directories at all and cannot touch what it does not know about. Deleting them is only possible
from the Remove page, which then hands over to `msiexec`.

The uninstaller asks a running tray to shut down first — an event, not a kill, so the stop goes
through the ordinary path and each store is closed rather than removed from under its process.
Verified: uninstalling with the tray running leaves zero processes, no executable, no shortcut, no
Add/Remove Programs entry, and the data intact.

## Where things live

| | |
|---|---|
| program | `%ProgramFiles%\Agience\Agience.exe` |
| settings | `%LOCALAPPDATA%\Agience\manager.json` |
| environment | `%LOCALAPPDATA%\Agience\runtime\` |
| logs | `%LOCALAPPDATA%\Agience\logs\<instance>.log` |
| data | `%LOCALAPPDATA%\Agience\data\<instance>\` — keys, the store, the indexes |

**The install and the data are separate trees, and the uninstaller is the reason.** "Remove the
program but keep my data" is only expressible if the two never overlap.

**The Python environment is data, not program.** It is built on this machine by pip, it is several
hundred megabytes, and rebuilding it needs the network — so it sits beside the data and survives a
reinstall.

## Build, test, check

```powershell
dotnet build src\Agience.Manager
dotnet test  tests\Agience.Manager.Tests     # 48 tests
installer\build.ps1                          # → installer\out\Agience-0.1.0-x64.msi
```

**Verify against the real machine** — the same code path the tray runs, printed once and exited.
Non-zero unless everything is up, so a script can gate on it:

```powershell
"%ProgramFiles%\Agience\Agience.exe" --check
```

`--check` exists because **a tray icon cannot be verified by a script**, and `--icons <dir>`
exists because **a tray icon cannot be looked at** — it renders every state to PNG plus a strip of
all of them at 16px, magnified, which is the only way anyone reviews whether they are actually
distinguishable. That is how the faded mark was caught.

**A WinExe has no console**, so these attach to the parent's. From a shell that does not share
one, redirect: `... --check > out.txt 2>&1`.

The assembly is named `Agience` because that is what a person sees — the exe in Task Manager, the
folder under Program Files, and the label in Settings › Taskbar. The C# namespace stays
`Agience.Manager`; a namespace is a code-organisation detail nobody outside this repository reads.

## The installer

**Per-machine**, into `Program Files`, registering under HKLM. It asks for administrator once.

The other two scopes were built and installed first and both produced an incoherent Add/Remove
Programs entry on Windows 11 26200 — `perUserOrMachine` put the files in one user's profile while
registering machine-wide, and `perUser` still wrote its ARP values to HKLM. `Agience.wxs` records
all three measurements.

**`ARPPRODUCTICON` writes no `DisplayIcon` on this Windows**, in any scope — measured four times,
with the Icon table and the Property table both correct. The entry is written by hand into the same
HKLM key, which is only coherent *because* the package is per-machine.

**Unsigned.** Signing is deferred while this runs on our own machines, which makes it a
**prerequisite for the installer reaching anyone else** rather than a polish item: an unsigned MSI
costs a SmartScreen dialog on every first run.

## Rules this application is held to

1. **No port arithmetic outside the catalogue.** A port comes from an instance, and no two instances
   may hold the same one.
2. **No health verdict invented here.** A port is bound or it is not; Origin's JWKS answers or it
   does not. Nothing times a request to decide health.
3. **Nothing starts an instance except `Supervisor`.** Not the tray, not the configuration screen,
   not the installer. A second start path is how two surfaces come to disagree about how Mantle
   boots — silently, because a service started the other way still answers.
4. **One fact is typed once.** The domain. Everything else is derived, and an override is a separate
   field rather than an edited derivation.
5. **A data directory is never removed without being asked.**

**The service command lines are a second copy** of `agience-cloud/scripts/service_common.sh`'s
`svc_cmd`, and that cost is accepted knowingly: the shell table is what a developer box with Git Bash
runs, and `ServiceCatalog.cs` is what an installed Windows machine runs, where there is no bash to
source it from. The table is deliberately small — a port, a module and an argument list per kind — so
a drift is a three-line diff rather than a hunt.

## Licence

AGPL-3.0-only, with the rest of `agience-observe`. See [`LICENSE`](../../LICENSE) and
[`NOTICE`](../../NOTICE).
