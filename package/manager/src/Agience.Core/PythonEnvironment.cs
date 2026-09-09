using System.Text.RegularExpressions;

namespace Agience.Core;

/// <summary>One Python found on this machine.</summary>
/// <param name="Path">Full path to <c>python.exe</c>.</param>
/// <param name="Version">What it reported when asked.</param>
/// <param name="Source">How it was found — shown so a person can tell two installs apart.</param>
public sealed record PythonInterpreter(string Path, Version Version, string Source)
{
    public override string ToString() => $"Python {Version} — {Path}  ({Source})";
}

/// <summary>What the private environment currently holds.</summary>
/// <param name="VenvExists">Is there an interpreter at <see cref="Paths.VenvPython"/>?</param>
/// <param name="Packages">Package name to installed version, or null where it is absent.</param>
/// <param name="Ready">Can the services be started right now?</param>
/// <param name="Summary">One sentence, for the window and the tray tooltip.</param>
public sealed record RuntimeStatus(
    bool VenvExists,
    IReadOnlyDictionary<string, string?> Packages,
    bool Ready,
    string Summary)
{
    /// <summary>The packages that are not installed, in install order.</summary>
    public IReadOnlyList<string> Missing =>
        Packages.Where(p => p.Value is null).Select(p => p.Key).ToList();
}

/// <summary>
/// Finds a Python, builds the private environment, and puts Origin and Mantle in it.
/// </summary>
/// <remarks>
/// <para>
/// A virtualenv under the user's profile, never the machine's Python. These packages pin
/// exact versions — origin alone pins fastapi, starlette, cryptography and uvicorn — and installing
/// those into whatever interpreter a person already had is how an installer breaks unrelated work
/// on somebody's machine. The environment is removable, and removing it takes nothing else with it.
/// </para>
/// <para>
/// These packages are not on PyPI. <c>pip install agience-mantle</c> resolves nothing from an
/// index. The two real sources are a checkout on this disk and the git remote, which is what
/// <see cref="AgienceConfig.PackageSource"/> selects between.
/// </para>
/// <para>
/// Nothing here installs Python itself. Silently running somebody else's installer is not a thing
/// a tray application should do; <see cref="DownloadPage"/> is opened and the person decides.
/// </para>
/// </remarks>
public static class PythonEnvironment
{
    /// <summary>
    /// The floor, and it comes from the packages rather than from taste.
    /// </summary>
    /// <remarks>
    /// Origin and Mantle both declare <c>requires-python &gt;= 3.11</c>. The highest floor wins,
    /// because one environment holds both.
    /// </remarks>
    public static readonly Version Minimum = new(3, 11);

    /// <summary>
    /// The highest version this has been run against.
    /// </summary>
    /// <remarks>
    /// A ceiling is a warning, not a refusal. A newer Python usually works and sometimes has no
    /// wheels yet for a pinned dependency; refusing it would strand people on a machine that is
    /// fine, so the setup screen says "untested" and proceeds.
    /// </remarks>
    public static readonly Version Tested = new(3, 13);

    /// <summary>Where a person is sent when this machine has no suitable Python.</summary>
    public const string DownloadPage = "https://www.python.org/downloads/windows/";

    /// <summary>The packages that must be present before anything can start.</summary>
    /// <remarks>
    /// The distribution names, which is what pip records in <c>*.dist-info</c>. The import names
    /// differ — <c>origin</c>, <c>mantle</c> — and checking for those instead would
    /// match any directory that happened to be called <c>origin</c>.
    /// </remarks>
    public static IReadOnlyList<string> RequiredPackages { get; } =
        new[] { "agience-origin", "agience-mantle" };

    // ── finding an interpreter ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every Python on this machine that could build the environment, best first.
    /// </summary>
    /// <remarks>
    /// The py launcher is asked first and its answer is authoritative. <c>py -0p</c> enumerates
    /// every registered install with its path; PATH holds at most one and, on a machine with the
    /// Store's stub, holds one that is not an interpreter at all — it is a shim that opens the
    /// Microsoft Store, and a venv built from it fails in a way that names none of this.
    /// </remarks>
    public static IReadOnlyList<PythonInterpreter> Discover()
    {
        var found = new List<PythonInterpreter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path))
            {
                return;
            }

            var version = VersionOf(path);
            if (version is not null && version >= Minimum)
            {
                found.Add(new PythonInterpreter(path, version, source));
            }
        }

        foreach (var path in FromLauncher())
        {
            Consider(path, "py launcher");
        }

        foreach (var path in FromPath())
        {
            Consider(path, "PATH");
        }

        return found.OrderByDescending(p => p.Version).ToList();
    }

    /// <summary>The interpreter setup will use, or null if this machine has none.</summary>
    /// <remarks>
    /// Highest version wins. A person who needs a specific one sets
    /// <see cref="AgienceConfig.BasePython"/> and the screen shows which was used.
    /// </remarks>
    public static PythonInterpreter? Best(AgienceConfig config)
    {
        var all = Discover();
        if (!string.IsNullOrWhiteSpace(config.BasePython))
        {
            var pinned = all.FirstOrDefault(
                p => string.Equals(p.Path, config.BasePython, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null)
            {
                return pinned;
            }
        }

        return all.FirstOrDefault();
    }

    private static IEnumerable<string> FromLauncher()
    {
        var result = Shell.RunAsync("py", new[] { "-0p" }, timeout: TimeSpan.FromSeconds(20))
                          .GetAwaiter().GetResult();
        if (!result.Ok)
        {
            yield break;
        }

        // Lines look like " -V:3.12 *        C:\Users\...\python.exe". The path is whatever
        // follows the first run of two or more spaces; splitting on single spaces would cut a
        // path containing one, which "Program Files" guarantees.
        foreach (var line in result.Output.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"\s{2,}(?<path>[A-Za-z]:\\.+python\.exe)\s*$",
                                    RegexOptions.IgnoreCase);
            if (match.Success)
            {
                yield return match.Groups["path"].Value.Trim();
            }
        }
    }

    private static IEnumerable<string> FromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), "python.exe");
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters is not this application's problem.
                continue;
            }

            // The store stub is skipped by location. `WindowsApps\python.exe` is a zero-byte
            // reparse point that opens the Microsoft Store; it answers `--version` with nothing and
            // builds a venv that cannot run. Excluding it here is why setup does not appear to
            // succeed and then fail at the first service.
            if (candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static Version? VersionOf(string python)
    {
        var result = Shell.RunAsync(python, new[] { "-c", "import sys;print('%d.%d.%d' % sys.version_info[:3])" },
                                    timeout: TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        if (!result.Ok)
        {
            return null;
        }

        return Version.TryParse(result.Output.Trim(), out var version) ? version : null;
    }

    // ── inspecting the environment ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What is in the private environment right now.
    /// </summary>
    /// <remarks>
    /// Read off the disk, not by running pip. This is asked on every poll behind the tray icon,
    /// and <c>pip list</c> costs about a second each time — which would make the icon's own status
    /// check the heaviest thing this application does.
    /// </remarks>
    public static RuntimeStatus Inspect()
    {
        var venv = File.Exists(Paths.VenvPython);
        var packages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequiredPackages)
        {
            packages[name] = venv ? InstalledVersion(name) : null;
        }

        var missing = packages.Where(p => p.Value is null).Select(p => p.Key).ToList();
        var ready = venv && missing.Count == 0;

        var summary = !venv
            ? "No environment yet. Setup builds one under your profile and installs Origin and Mantle into it."
            : missing.Count == 0
                ? "Ready: " + string.Join(", ", packages.Select(p => $"{p.Key} {p.Value}"))
                : "Environment exists but is missing " + string.Join(", ", missing) + ".";

        return new RuntimeStatus(venv, packages, ready, summary);
    }

    /// <summary>The version pip recorded for a distribution, or null if it is not installed.</summary>
    private static string? InstalledVersion(string distribution)
    {
        try
        {
            var sitePackages = Path.Combine(Paths.Runtime, "Lib", "site-packages");
            if (!Directory.Exists(sitePackages))
            {
                return null;
            }

            // pip normalises a distribution name to underscores in the directory it writes, so
            // `agience-origin` is recorded as `agience_origin-0.1.0.dist-info`.
            var prefix = distribution.Replace('-', '_') + "-";
            foreach (var directory in Directory.EnumerateDirectories(sitePackages, prefix + "*.dist-info"))
            {
                var name = Path.GetFileName(directory);
                var version = name[prefix.Length..^".dist-info".Length];
                return version.Length == 0 ? "installed" : version;
            }
        }
        catch (Exception)
        {
            // An unreadable site-packages reads as "not installed", which sends a person to setup —
            // a place where the real error will be shown rather than swallowed.
        }

        return null;
    }

    // ── building it ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the environment and install the packages. Safe to run again.
    /// </summary>
    /// <remarks>
    /// Every step reports before it runs. This takes minutes on a cold machine, and a window that
    /// says nothing for four minutes is a window a person kills — after which the environment is
    /// half-built and the next run has to be able to continue, which is why nothing here assumes a
    /// clean start.
    /// </remarks>
    public static async Task<ShellResult> SetupAsync(
        AgienceConfig config, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Paths.EnsureRoot();

        var interpreter = Best(config);
        if (interpreter is null)
        {
            return new ShellResult(-1,
                $"No Python {Minimum} or newer was found on this machine. Install one from " +
                $"{DownloadPage} — tick \"Add python.exe to PATH\" — then run setup again.");
        }

        progress?.Report($"Using {interpreter}");
        if (interpreter.Version > Tested)
        {
            progress?.Report(
                $"Note: Python {interpreter.Version} is newer than the {Tested} this has been " +
                "tested against. If a dependency has no wheel for it yet, the install below is " +
                "where that will show up.");
        }

        if (!File.Exists(Paths.VenvPython))
        {
            progress?.Report($"Creating the environment at {Paths.Runtime} ...");
            var venv = await Shell.RunAsync(interpreter.Path, new[] { "-m", "venv", Paths.Runtime },
                                            progress: progress, ct: ct).ConfigureAwait(false);
            if (!venv.Ok || !File.Exists(Paths.VenvPython))
            {
                return new ShellResult(venv.ExitCode,
                    "Could not create the environment." + Environment.NewLine + venv.Output);
            }
        }
        else
        {
            progress?.Report($"Reusing the environment at {Paths.Runtime}");
        }

        config.BasePython = interpreter.Path;
        config.Save();

        progress?.Report("Updating pip ...");
        await Shell.RunAsync(Paths.VenvPython,
                             new[] { "-m", "pip", "install", "--upgrade", "pip", "setuptools", "wheel" },
                             progress: progress, ct: ct).ConfigureAwait(false);

        foreach (var step in PlanFor(config, out var source))
        {
            progress?.Report($"--- {step.Description} ({source}) ---");
            var install = await Shell.RunAsync(Paths.VenvPython, step.Arguments,
                                               progress: progress, ct: ct).ConfigureAwait(false);
            if (!install.Ok)
            {
                return new ShellResult(install.ExitCode,
                    $"{step.Description} failed." + Environment.NewLine + install.Output);
            }
        }

        var status = Inspect();
        progress?.Report(status.Summary);
        return status.Ready
            ? new ShellResult(0, status.Summary)

            // pip can exit 0 having installed nothing asked for — a resolver that satisfied a
            // requirement from a cached wheel of the wrong name, a path that silently matched
            // nothing. Reporting success from the exit code alone is how setup comes back green
            // over an environment that cannot start a service.
            : new ShellResult(-1,
                "The install commands succeeded but the environment is still missing " +
                string.Join(", ", status.Missing) + ".");
    }

    /// <summary>One pip invocation.</summary>
    private sealed record InstallStep(string Description, IReadOnlyList<string> Arguments);

    /// <summary>
    /// The install order, and where each package comes from.
    /// </summary>
    /// <remarks>
    /// Order is a dependency order. Prism carries the wire and the belief model that mantle's
    /// semantic arm imports. Installed out of order, pip resolves each sibling from an index that
    /// does not have it, and the failure names a package nobody typed.
    /// </remarks>
    private static IEnumerable<InstallStep> PlanFor(AgienceConfig config, out string source)
    {
        var root = SourceRootFor(config);
        if (root is not null)
        {
            source = root;
            return new[]
            {
                // Origin pins its runtime dependencies in requirements.txt rather than in
                // pyproject, because that file is what its own image installs. Honour it, or origin
                // arrives importable and without a web framework.
                new InstallStep("Origin's pinned dependencies",
                    new[] { "-m", "pip", "install", "-r", Path.Combine(root, "agience-origin", "requirements.txt") }),
                new InstallStep("Prism (the wire)",
                    new[] { "-m", "pip", "install", Path.Combine(root, "agience-prism", "py") }),
                new InstallStep("Mantle (the store)",
                    new[] { "-m", "pip", "install", Path.Combine(root, "agience-mantle") + "[service,search,s3]" }),
                new InstallStep("Origin (the authority)",
                    new[] { "-m", "pip", "install", Path.Combine(root, "agience-origin") }),
            };
        }

        const string github = "https://github.com/Agience";
        source = github;
        return new[]
        {
            new InstallStep("Prism (the wire)",
                new[] { "-m", "pip", "install", $"git+{github}/agience-prism.git#subdirectory=py" }),
            new InstallStep("Mantle (the store)",
                new[] { "-m", "pip", "install", $"agience-mantle[service,search,s3] @ git+{github}/agience-mantle.git" }),
            new InstallStep("Origin (the authority)",
                new[] { "-m", "pip", "install", $"git+{github}/agience-origin.git" }),
        };
    }

    /// <summary>
    /// The checkout directory to install from, or null to install from git.
    /// </summary>
    /// <remarks>
    /// A directory is only accepted when all three repositories are in it. A partial checkout is
    /// worse than none: pip installs the ones that are there, resolves the rest from an index that
    /// does not carry them, and the failure names a transitive dependency instead of the missing
    /// directory a person could actually fix.
    /// </remarks>
    public static string? SourceRootFor(AgienceConfig config)
    {
        if (config.PackageSource.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.SourceRoot))
        {
            candidates.Add(config.SourceRoot.Trim());
        }

        if (config.PackageSource.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // Where the checkouts sit when this is being run out of the workspace it was built in.
            var here = AppContext.BaseDirectory;
            for (var dir = new DirectoryInfo(here); dir is not null; dir = dir.Parent)
            {
                candidates.Add(dir.FullName);
            }
        }

        foreach (var candidate in candidates)
        {
            if (IsCompleteCheckout(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Does this directory hold all three repositories setup installs from?</summary>
    public static bool IsCompleteCheckout(string root)
    {
        try
        {
            return File.Exists(Path.Combine(root, "agience-origin", "requirements.txt"))
                && File.Exists(Path.Combine(root, "agience-mantle", "pyproject.toml"))
                && File.Exists(Path.Combine(root, "agience-prism", "py", "pyproject.toml"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Delete the private environment.
    /// </summary>
    /// <remarks>
    /// The environment only. The data directory is a separate tree and a separate decision — this
    /// is the half a person is always willing to lose, because rebuilding it costs a download
    /// rather than a memory.
    /// </remarks>
    public static void Remove()
    {
        if (Directory.Exists(Paths.Runtime))
        {
            Directory.Delete(Paths.Runtime, recursive: true);
        }
    }
}
