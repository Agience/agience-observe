using System.Diagnostics;
using System.Text;

namespace Agience.Core;

/// <summary>What one instance is doing. Ordered worst to best; the ordering is not decorative.</summary>
public enum ServiceState
{
    /// <summary>Switched off in configuration. Not a fault.</summary>
    Disabled,

    /// <summary>No process, and nothing on the port.</summary>
    Stopped,

    /// <summary>A start was attempted and it is not serving. Its log says why.</summary>
    Failed,

    /// <summary>The port is held by a process this application did not start.</summary>
    Foreign,

    /// <summary>Asked to stop; the process has not gone yet.</summary>
    Stopping,

    /// <summary>Launched, not yet answering. The ordinary first few seconds.</summary>
    Starting,

    /// <summary>The process is alive but its health rule is not satisfied.</summary>
    Unhealthy,

    /// <summary>Alive, bound, and answering.</summary>
    Serving,
}

/// <summary>
/// One instance's state, detached from the object that owns it.
/// </summary>
/// <remarks>
/// The evaluator takes these, not live processes, which is why the rules can be tested at all.
/// A verdict computed from live <see cref="ServiceProcess"/> objects could only be exercised by
/// actually starting services — so the rules that matter most, the ones about states nobody can
/// arrange on demand, would be the ones with no test.
/// </remarks>
public sealed record ServiceSnapshot(
    string Id, string Display, int Port, ServiceState State, string Detail, int? Pid);

/// <summary>One instance: its process, its port, its log, and the truth about all three.</summary>
/// <remarks>
/// <para>
/// The process handle is not the source of truth about the port. A handle says a process exists;
/// only the TCP table says who holds the port. They disagree in the two states that cost time — an
/// instance that died leaving an orphan holding the port, and a stranger already on it before we
/// started — so both are read on every poll and the port wins.
/// </para>
/// <para>
/// This reports one instance and decides nothing about whether the machine is healthy. Combining
/// instances into one icon is <see cref="Supervisor"/>'s job, and keeping the two apart is what
/// stops an instance learning about its siblings.
/// </para>
/// </remarks>
public sealed class ServiceProcess : IDisposable
{
    /// <summary>How long an instance gets to bind its port before the start is called a failure.</summary>
    /// <remarks>
    /// 45s, the same bound the shell supervisor's <c>wait_healthy</c> uses. It is generous because
    /// the first start of a Mantle opens a store and builds an index; a shorter bound would report a
    /// failure over something that was about to come up, and the retry would make it slower still.
    /// </remarks>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long a stopped instance gets to release its port before we say it has not.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Bytes of log kept per instance before the file is rolled to `.1`.</summary>
    /// <remarks>
    /// An instance in a restart loop writes its traceback every few seconds, unattended, for as
    /// long as the machine is on, so the log is rolled at this size before it fills the disk.
    /// </remarks>
    private const long LogRollBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        // Loopback against a local process: a slow answer means something is wrong rather than
        // something is big, and a long timeout would stall the poll behind a wedged service.
        Timeout = TimeSpan.FromSeconds(3),
    };

    private readonly object _gate = new();
    private readonly JobObject _job;
    private Process? _process;
    private StreamWriter? _log;

    /// <summary>The instance as it was configured when this object was made.</summary>
    /// <remarks>
    /// A snapshot of the configuration, not a live view of it. A running process was launched with
    /// the port and the domain that were in force at the time; showing it the values somebody has
    /// since typed would describe a process that does not exist.
    /// </remarks>
    public ServiceInstance Instance { get; }

    /// <summary>What kind of service this is. Null if a later build wrote a kind this one lacks.</summary>
    public ServiceKind? Kind => Instance.Definition;

    /// <summary>The port this instance was launched on, fixed for the life of the object.</summary>
    public int Port { get; }

    /// <summary>Where this instance's output is written.</summary>
    public string LogPath { get; }

    public ServiceState State { get; private set; } = ServiceState.Stopped;

    /// <summary>One sentence saying why the state is what it is. Never null.</summary>
    public string Detail { get; private set; } = "Not started.";

    /// <summary>The process this application started, when it has one.</summary>
    public int? Pid { get; private set; }

    /// <summary>Whoever holds the port, which is not always <see cref="Pid"/>.</summary>
    public int? PortOwnerPid { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Consecutive automatic relaunches, for the caller's backoff.</summary>
    public int RestartCount { get; internal set; }

    /// <summary>Did the last exit happen without anyone asking? The caller's cue to relaunch.</summary>
    public bool ExitedUnexpectedly { get; private set; }

    public ServiceProcess(ServiceInstance instance, JobObject job)
    {
        Instance = instance;
        Port = instance.Port;
        _job = job;
        LogPath = AgienceConfig.LogPathOf(instance);
    }

    /// <summary>This instance's state, as a value the verdict can be computed from.</summary>
    public ServiceSnapshot Snapshot() =>
        new(Instance.Id, Instance.Display, Port, State, Detail, Pid);

    /// <summary>Declare this instance switched off.</summary>
    /// <remarks>
    /// Disabled is not stopped, and conflating them makes the icon lie in both directions.
    /// "Stopped" is something this machine runs that is currently down — a thing to fix. "Disabled"
    /// is something it does not run — a thing somebody decided. Counting the second as the first
    /// paints a permanent amber over a correct configuration.
    /// </remarks>
    public void MarkDisabled() => Set(ServiceState.Disabled, "Switched off in configuration.");

    /// <summary>
    /// Launch the instance and wait until it is serving, or until the start times out.
    /// </summary>
    /// <remarks>
    /// Refuses to start on a port somebody else holds. Starting anyway produces a process that fails to
    /// bind, exits, and leaves the squatter answering — after which every surface reports the
    /// service up and the requests go to a stranger. Refusing names the pid, which is the one thing
    /// a person needs in order to act.
    /// </remarks>
    public async Task<bool> StartAsync(AgienceConfig config, CancellationToken ct = default)
    {
        if (Kind is null)
        {
            Set(ServiceState.Failed,
                $"'{Instance.Kind}' is not a kind of service this version knows how to run.");
            return false;
        }

        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return State == ServiceState.Serving;
            }
        }

        var owner = PortProbe.OwnerPid(Port);
        if (owner is { } squatter && squatter != Environment.ProcessId)
        {
            PortOwnerPid = squatter;
            Set(ServiceState.Foreign,
                $"Port {Port} is already held by process {squatter} ({NameOf(squatter)}). " +
                "Nothing was started — a service launched onto a held port exits, and the process " +
                "already there keeps answering as if it were us.");
            return false;
        }

        ServiceEnvironment.EnsureDirectories(config, Instance);
        Paths.EnsureRoot();
        RollLog();

        var psi = new ProcessStartInfo(Paths.VenvPython)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // This instance's own data directory, not the install directory. Several of these
            // services resolve a relative default against the working directory — mantle's store is
            // the loud one — so a stray file lands beside the data it belongs with, and beside the
            // right instance's data, instead of under Program Files where an unprivileged process
            // cannot write it and the failure reads as a permission bug rather than a missing
            // setting.
            WorkingDirectory = config.DataDirectoryOf(Instance),
        };

        foreach (var arg in Kind.ArgumentsFor(Port))
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in ServiceEnvironment.For(config, Instance))
        {
            psi.Environment[key] = value;
        }

        try
        {
            var started = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned nothing");

            lock (_gate)
            {
                _process = started;
                Pid = started.Id;
                StartedAt = DateTimeOffset.UtcNow;
                ExitedUnexpectedly = false;
                _log = OpenLog();
            }

            _job.Assign(started);

            started.OutputDataReceived += (_, e) => Append(e.Data);
            started.ErrorDataReceived += (_, e) => Append(e.Data);
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();

            Append($"--- {Instance.Display} started pid {started.Id} on 127.0.0.1:{Port} at " +
                   $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ---");
        }
        catch (Exception exc)
        {
            Set(ServiceState.Failed, $"Could not launch {Paths.VenvPython}: {exc.Message}");
            return false;
        }

        Set(ServiceState.Starting, $"Launched; waiting up to {StartTimeout.TotalSeconds:F0}s for it to answer.");
        return await WaitUntilServingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stop the instance and wait for its port to come free.</summary>
    /// <remarks>
    /// Kills the process tree, not just the process. Killing only the parent leaves whatever it spawned
    /// holding the port, and the next start then fails against an orphan nothing will admit to
    /// owning — the exact state the job object exists to prevent after a crash, applied here to the
    /// ordinary case.
    /// </remarks>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            Set(ServiceState.Stopped, "Not running.");
            CloseLog();
            return;
        }

        Set(ServiceState.Stopping, "Stopping.");
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StopTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(ServiceState.Failed,
                $"Did not exit within {StopTimeout.TotalSeconds:F0}s. Process {Pid} may still hold port {Port}.");
            Append("--- stop timed out ---");
            CloseLog();
            process.Dispose();
            return;
        }
        catch (Exception exc)
        {
            Append($"--- stop failed: {exc.Message} ---");
        }

        Append("--- stopped ---");
        CloseLog();
        process.Dispose();

        Pid = null;
        StartedAt = null;
        ExitedUnexpectedly = false;
        RestartCount = 0;

        // The port can outlive the process by a moment. Reporting Stopped while it is still held
        // would make an immediate restart look like a squatter appearing from nowhere.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && PortProbe.IsBound(Port))
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        Set(ServiceState.Stopped, "Stopped.");
    }

    /// <summary>Re-read this instance's state from the process table, the port and its health rule.</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        PortOwnerPid = PortProbe.OwnerPid(Port);

        if (process is null)
        {
            // Nothing of ours is running. If the port is held anyway, somebody else has it — which
            // is the state a person must be told about before they try to start.
            if (PortOwnerPid is { } holder)
            {
                Set(ServiceState.Foreign,
                    $"Not running here, but port {Port} is held by process {holder} ({NameOf(holder)}).");
                return;
            }

            if (State != ServiceState.Failed && State != ServiceState.Stopping)
            {
                Set(ServiceState.Stopped, "Not running.");
            }

            return;
        }

        if (process.HasExited)
        {
            var code = TryExitCode(process);
            lock (_gate)
            {
                _process = null;
            }

            Pid = null;
            ExitedUnexpectedly = true;
            CloseLog();
            Set(ServiceState.Failed,
                $"Exited on its own with code {code}. The last lines of {Path.GetFileName(LogPath)} say why.");
            return;
        }

        if (!await IsServingAsync(ct).ConfigureAwait(false))
        {
            var age = StartedAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;

            // The first seconds are "starting", not "unhealthy". Reporting unhealthy the instant
            // something launches trains a person to ignore the state entirely, and by the time it
            // means something they have stopped reading it.
            Set(age < StartTimeout ? ServiceState.Starting : ServiceState.Unhealthy,
                age < StartTimeout
                    ? $"Running as {Pid}; not answering yet ({age.TotalSeconds:F0}s)."
                    : $"Running as {Pid} but {Describe()} after {age.TotalSeconds:F0}s.");
            return;
        }

        Set(ServiceState.Serving, $"Serving on 127.0.0.1:{Port} as process {Pid}.");
    }

    private async Task<bool> WaitUntilServingAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            Process? process;
            lock (_gate)
            {
                process = _process;
            }

            if (process is null || process.HasExited)
            {
                var code = process is null ? "?" : TryExitCode(process);
                lock (_gate)
                {
                    _process = null;
                }

                Pid = null;
                CloseLog();
                Set(ServiceState.Failed,
                    $"Exited immediately with code {code} — it never bound port {Port}. See {LogPath}.");
                return false;
            }

            if (await IsServingAsync(ct).ConfigureAwait(false))
            {
                RestartCount = 0;
                Set(ServiceState.Serving, $"Serving on 127.0.0.1:{Port} as process {Pid}.");
                return true;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        Set(ServiceState.Unhealthy,
            $"Started, but {Describe()} within {StartTimeout.TotalSeconds:F0}s. " +
            $"It is still running as {Pid}; see {LogPath}.");
        return false;
    }

    /// <summary>The health rule, applied. The port for most; the JWKS for an Origin.</summary>
    private async Task<bool> IsServingAsync(CancellationToken ct)
    {
        if (!PortProbe.IsBound(Port))
        {
            return false;
        }

        if (Kind is null || Kind.Health == HealthRule.PortBound || Kind.HealthPath is null)
        {
            return true;
        }

        try
        {
            using var response = await Http
                .GetAsync($"http://127.0.0.1:{Port}{Kind.HealthPath}", ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string Describe() =>
        Kind is { Health: HealthRule.HttpOk, HealthPath: not null }
            ? $"{Kind.HealthPath} did not answer"
            : $"nothing bound port {Port}";

    private void Set(ServiceState state, string detail)
    {
        State = state;
        Detail = detail;
    }

    private static string TryExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // "gone" is a real answer, not an error. A pid read from the TCP table can exit
            // between the two calls, and it names the transient case precisely.
            return "gone";
        }
    }

    // ── the log ─────────────────────────────────────────────────────────────────────────────────

    private StreamWriter OpenLog()
    {
        try
        {
            return new StreamWriter(
                new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch (Exception)
        {
            // A log we cannot open is not a service we refuse to run. It still starts, still
            // serves, and still reports its state; only the record of its output is lost.
            return StreamWriter.Null;
        }
    }

    private void RollLog()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > LogRollBytes)
            {
                var previous = LogPath + ".1";
                File.Delete(previous);
                File.Move(LogPath, previous);
            }
        }
        catch (Exception)
        {
            // A log that cannot be rolled is appended to instead. Losing the roll is better than
            // refusing the start.
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        StreamWriter? log;
        lock (_gate)
        {
            log = _log;
        }

        try
        {
            log?.WriteLine(line);
        }
        catch (Exception)
        {
            // The file was moved or the disk filled. Not a reason to take the service down.
        }
    }

    private void CloseLog()
    {
        StreamWriter? log;
        lock (_gate)
        {
            log = _log;
            _log = null;
        }

        try
        {
            log?.Dispose();
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        CloseLog();
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
