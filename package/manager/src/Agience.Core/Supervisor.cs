namespace Agience.Core;

/// <summary>
/// Owns the instances: starts them in order, stops them, polls them, and relaunches what died.
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing that starts an instance. Not the tray, not the configuration screen,
/// not the installer. A second start path is how two surfaces come to disagree about how Mantle
/// boots — and the disagreement is silent, because a service started the other way still answers.
/// </para>
/// <para>
/// Start order is a dependency order, across the whole machine. Every Origin starts before any
/// Mantle, whatever domains they are on, because a Mantle verifies against an authority that must
/// already be publishing keys. Started interleaved, a Mantle reports a trust failure rather than a
/// race — and the person reading that goes looking at trust configuration.
/// </para>
/// </remarks>
public sealed class Supervisor : IDisposable
{
    /// <summary>How often every instance's state is re-read.</summary>
    /// <remarks>
    /// Two seconds. Each poll is a TCP-table read plus at most one loopback request per instance, so
    /// the cost is negligible and the icon follows a stop or a crash closely enough to be believed.
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait between automatic relaunches.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly JobObject _job = new();
    private readonly Dictionary<string, ServiceProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextRestart = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private AgienceConfig _config;

    public Supervisor(AgienceConfig config)
    {
        _config = config;
        Rebuild();
    }

    /// <summary>The configuration in force. Assigning it reconciles the instance list.</summary>
    /// <remarks>
    /// Changing this does not restart anything. A port or a domain change reaches an instance only
    /// when it is next launched; the supervisor does not bounce services because somebody typed in
    /// a text box. The window says so and offers it.
    /// </remarks>
    public AgienceConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            Rebuild();
        }
    }

    /// <summary>Every instance, enabled or not, in start order.</summary>
    public IReadOnlyList<ServiceProcess> Services =>
        _config.InStartOrder
               .Select(i => _processes.TryGetValue(i.Id, out var p) ? p : null)
               .Where(p => p is not null)
               .Select(p => p!)
               .ToList();

    /// <summary>The instances this machine is configured to run.</summary>
    public IReadOnlyList<ServiceProcess> Enabled =>
        Services.Where(s => s.State != ServiceState.Disabled).ToList();

    /// <summary>Is the Python environment built and carrying the packages?</summary>
    public bool RuntimeReady { get; set; }

    /// <summary>The whole machine as one icon and one sentence.</summary>
    public Verdict Verdict { get; private set; } =
        new(TrayLevel.NotRunning, "Starting up", "Nothing has been polled yet.");

    /// <summary>Raised after every poll.</summary>
    public event Action? Changed;

    /// <summary>Look one instance's process up by id.</summary>
    public ServiceProcess? this[string id] =>
        _processes.TryGetValue(id, out var process) ? process : null;

    /// <summary>
    /// Start every enabled instance, in order, waiting for each.
    /// </summary>
    /// <remarks>
    /// One that fails does not stop the ones after it. An Origin failing means the Mantles that
    /// trust it will not verify a token — but they still start, still report their own state, and
    /// their logs still say what they said. Refusing to start them would hide two problems behind
    /// one, and a person fixing the Origin would then find a Mantle broken for a reason nobody
    /// recorded.
    /// </remarks>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var service in Services)
            {
                if (service.State == ServiceState.Disabled || ct.IsCancellationRequested)
                {
                    continue;
                }

                await service.StartAsync(_config, ct).ConfigureAwait(false);
                Recompute();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stop every instance, in reverse start order.</summary>
    /// <remarks>
    /// Reverse order, so a Mantle is never left talking to an Origin that has just gone. It costs
    /// nothing and keeps the logs free of a spray of trust errors on every shutdown — errors that
    /// look identical to the real thing when a person later reads the file.
    /// </remarks>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var service in Services.Reverse())
            {
                await service.StopAsync(ct).ConfigureAwait(false);
                Recompute();
            }

            _nextRestart.Clear();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Start one instance, whatever the others are doing.</summary>
    public async Task StartAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _nextRestart.Remove(id);
            if (_processes.TryGetValue(id, out var process))
            {
                await process.StartAsync(_config, ct).ConfigureAwait(false);
            }

            Recompute();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stop one instance.</summary>
    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Removed here as well as on stop: somebody stopping an instance must not find it
            // relaunched a moment later by a backoff timer set before they asked.
            _nextRestart.Remove(id);
            if (_processes.TryGetValue(id, out var process))
            {
                await process.StopAsync(ct).ConfigureAwait(false);
            }

            Recompute();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stop then start one instance.</summary>
    public async Task RestartAsync(string id, CancellationToken ct = default)
    {
        await StopAsync(id, ct).ConfigureAwait(false);
        await StartAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-read every instance's state, and relaunch anything that died on its own.
    /// </summary>
    /// <remarks>
    /// Only an unexpected exit is relaunched. Something that never bound its port is a
    /// configuration problem, not a transient one — relaunching it turns a legible failure into a
    /// loop that fills a log and never comes up. The distinction is
    /// <see cref="ServiceProcess.ExitedUnexpectedly"/>: it ran, then stopped.
    /// </remarks>
    public async Task PollAsync(CancellationToken ct = default)
    {
        foreach (var service in Services)
        {
            if (service.State == ServiceState.Disabled)
            {
                continue;
            }

            await service.PollAsync(ct).ConfigureAwait(false);
        }

        if (_config.RestartOnExit && RuntimeReady)
        {
            await RelaunchTheDeadAsync(ct).ConfigureAwait(false);
        }

        Recompute();
    }

    private async Task RelaunchTheDeadAsync(CancellationToken ct)
    {
        foreach (var service in Services)
        {
            if (!service.ExitedUnexpectedly || service.State != ServiceState.Failed)
            {
                continue;
            }

            var id = service.Instance.Id;
            if (!_nextRestart.TryGetValue(id, out var due))
            {
                // The backoff is set before the first relaunch, not after it. Set afterward,
                // something that dies during startup is relaunched instantly forever, and the poll
                // loop becomes a fork bomb against a service that cannot come up.
                _nextRestart[id] = DateTimeOffset.UtcNow + Backoff(service.RestartCount);
                continue;
            }

            if (DateTimeOffset.UtcNow < due)
            {
                continue;
            }

            if (!await _lifecycle.WaitAsync(0, ct).ConfigureAwait(false))
            {
                // Somebody is starting or stopping right now. Their intention outranks ours.
                continue;
            }

            try
            {
                service.RestartCount++;
                _nextRestart[id] = DateTimeOffset.UtcNow + Backoff(service.RestartCount);
                await service.StartAsync(_config, ct).ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    /// <summary>Doubling, from four seconds, capped. The cap is what keeps a broken machine quiet.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var seconds = 4.0 * Math.Pow(2, Math.Min(attempt, 6));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    private void Recompute()
    {
        Verdict = TrayState.Evaluate(RuntimeReady, _config.Instances.Count,
                                     Services.Select(s => s.Snapshot()).ToList());
        Changed?.Invoke();
    }

    /// <summary>
    /// Reconcile the process objects with the configured instances.
    /// </summary>
    /// <remarks>
    /// A running process is never replaced, even when its instance has been edited or deleted.
    /// Replacing it would drop the only handle to a process that is still running and still holding
    /// a port — which is how an orphan is made. The old object stays until somebody stops
    /// it, and the window shows what it is actually running as.
    /// </remarks>
    private void Rebuild()
    {
        foreach (var instance in _config.Instances)
        {
            if (_processes.TryGetValue(instance.Id, out var existing))
            {
                if (existing.Pid is not null)
                {
                    // Still running under its old settings. Leave it alone — see the remark above.
                    continue;
                }

                if (existing.Port == instance.Port && existing.Instance.Enabled == instance.Enabled)
                {
                    continue;
                }

                existing.Dispose();
            }

            var process = new ServiceProcess(instance, _job);
            if (!instance.Enabled)
            {
                process.MarkDisabled();
            }

            _processes[instance.Id] = process;
        }

        // Instances somebody deleted. A running one is kept until it is stopped, for the reason
        // above; a stopped one goes.
        foreach (var id in _processes.Keys.ToList())
        {
            if (_config.ById(id) is null && _processes[id].Pid is null)
            {
                _processes[id].Dispose();
                _processes.Remove(id);
            }
        }
    }

    /// <summary>Processes still running for instances not present in the configuration.</summary>
    /// <remarks>
    /// The window has to be able to show these. Deleting an instance from the configuration does
    /// not stop its process, and a running service that no surface lists is an orphan.
    /// </remarks>
    public IReadOnlyList<ServiceProcess> Orphans =>
        _processes.Values.Where(p => _config.ById(p.Instance.Id) is null).ToList();

    public void Dispose()
    {
        foreach (var process in _processes.Values)
        {
            process.Dispose();
        }

        _processes.Clear();

        // Last, and the order matters. Closing the job kills everything still in it, which is the
        // safety net for a crash. During an orderly shutdown the services have already been
        // stopped, so disposing their handles first means nothing is killed out from under a stop
        // that was in progress.
        _job.Dispose();
        _lifecycle.Dispose();
    }
}
