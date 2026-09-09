namespace Agience.Core;

/// <summary>What the icon shows. Ordered from worst to best; the ordering is not decorative.</summary>
public enum TrayLevel
{
    /// <summary>There is no Python environment yet — setup has not been finished.</summary>
    NotReady,

    /// <summary>Nothing is configured, or everything is stopped. A state somebody chose.</summary>
    NotRunning,

    /// <summary>A port is held by a process this application did not start.</summary>
    Foreign,

    /// <summary>At least one enabled instance is not serving.</summary>
    Degraded,

    /// <summary>Something is coming up and cannot yet be described either way.</summary>
    Starting,

    /// <summary>Every enabled instance is serving.</summary>
    Healthy,
}

/// <summary>One icon, and the sentence that explains it.</summary>
public sealed record Verdict(TrayLevel Level, string Headline, string Detail);

/// <summary>
/// Turns a list of instance states into one icon.
/// </summary>
/// <remarks>
/// <para>
/// This maps; it does not measure. Every input is a state <see cref="ServiceProcess"/> already
/// reached from a process handle, a TCP table and a health probe. A second opinion here — a port
/// reopened, an HTTP code re-interpreted — would be a second implementation that drifts silently,
/// because neither surface fails when the other changes.
/// </para>
/// <para>
/// Order is the design. Each rule below has a test named for it.
/// </para>
/// </remarks>
public static class TrayState
{
    /// <summary>Map the whole machine onto one icon and one sentence.</summary>
    /// <param name="runtimeReady">Is the Python environment built and carrying the packages?</param>
    /// <param name="configuredCount">How many instances exist at all, enabled or not.</param>
    /// <param name="services">Every configured instance's state.</param>
    public static Verdict Evaluate(bool runtimeReady, int configuredCount,
                                   IReadOnlyList<ServiceSnapshot> services)
    {
        // Nothing configured is asked before the runtime, because it is the more actionable of
        // the two on a fresh install and the runtime cannot be judged useful until something needs
        // it. An empty machine is not broken — it is a machine nobody has told what to run.
        if (configuredCount == 0)
        {
            return new Verdict(TrayLevel.NotRunning,
                "Nothing is configured",
                "No Origin and no Mantle have been added yet. Open the window and add one — an " +
                "Origin to be your own identity authority, a Mantle to hold what you store, or a " +
                "Mantle alone if you are joining somebody else's authority.");
        }

        // With no environment, every instance is stopped, and reporting that sends a person to
        // press Start — which cannot work, and says nothing about why.
        if (!runtimeReady)
        {
            return new Verdict(TrayLevel.NotReady,
                "Setup is not finished",
                "There is no Python environment yet, so nothing can start. Open the window and " +
                "finish setup — it installs Origin and Mantle into a private environment under " +
                "your profile.");
        }

        var enabled = services.Where(s => s.State != ServiceState.Disabled).ToList();

        // No enabled instances is not all-instances-up. An empty list satisfies "none of them is
        // down" vacuously, which would paint the icon green over a machine running nothing.
        if (enabled.Count == 0)
        {
            return new Verdict(TrayLevel.NotRunning,
                "Everything is switched off",
                $"All {configuredCount} configured instance(s) are switched off, so this machine " +
                "runs nothing. Turn at least one on in the window.");
        }

        // Foreign outranks merely-down, and keeps its own colour. On node 71, two abandoned
        // processes held :80 and :443, and the status surface reported the proxy up, serving the
        // squatter's 200. "Something is restarting" and "a stranger holds your port" are different
        // instructions to the person looking at the icon.
        var foreign = enabled.Where(s => s.State == ServiceState.Foreign).ToList();
        if (foreign.Count > 0)
        {
            return new Verdict(TrayLevel.Foreign,
                foreign.Count == 1
                    ? $"{foreign[0].Display}: port held by another process"
                    : $"{foreign.Count} ports are held by other processes",
                string.Join(" ", foreign.Select(s => s.Detail)) +
                " Nothing was started onto them: a service launched onto a held port exits, and " +
                "the process already there keeps answering as if it were us.");
        }

        if (enabled.All(s => s.State == ServiceState.Stopped))
        {
            return new Verdict(TrayLevel.NotRunning,
                "Stopped",
                $"None of the {enabled.Count} enabled instance(s) is running. Start them from this menu.");
        }

        var broken = enabled.Where(s => s.State is ServiceState.Failed or ServiceState.Unhealthy).ToList();
        if (broken.Count > 0)
        {
            return new Verdict(TrayLevel.Degraded,
                broken.Count == 1
                    ? $"{broken[0].Display} is not serving"
                    : $"{broken.Count} instances are not serving",
                string.Join(" ", broken.Select(s => $"{s.Display}: {s.Detail}")));
        }

        // Starting is not healthy and it is not broken. Reporting green over something that has
        // not answered yet is the one way this application can actively mislead; reporting amber
        // would make an ordinary launch look like a fault every single time.
        var starting = enabled.Where(s => s.State is ServiceState.Starting or ServiceState.Stopping).ToList();
        if (starting.Count > 0)
        {
            return new Verdict(TrayLevel.Starting,
                starting.Count == 1 ? $"{starting[0].Display} is coming up" : "Coming up",
                string.Join(" ", starting.Select(s => $"{s.Display}: {s.Detail}")));
        }

        // Some enabled, some stopped — a partial arrangement somebody made on purpose, or a stop
        // that only reached half of them. Either way it is not "all up".
        var stopped = enabled.Where(s => s.State == ServiceState.Stopped).ToList();
        if (stopped.Count > 0)
        {
            return new Verdict(TrayLevel.Degraded,
                stopped.Count == 1
                    ? $"{stopped[0].Display} is stopped"
                    : $"{stopped.Count} instances are stopped",
                "Enabled but not running: " + string.Join(", ", stopped.Select(s => s.Display)) + ".");
        }

        // An unrecognised state fails closed. This build can be given a state a later one
        // invents; defaulting it to serving would mean every state anyone adds is born green on
        // every installation already out there.
        var serving = enabled.Where(s => s.State == ServiceState.Serving).ToList();
        if (serving.Count != enabled.Count)
        {
            var unknown = enabled.Except(serving).ToList();
            return new Verdict(TrayLevel.Degraded,
                "Some instances cannot be described",
                "In a state this build does not recognise: " +
                string.Join(", ", unknown.Select(s => $"{s.Display} ({s.State})")) + ".");
        }

        return new Verdict(TrayLevel.Healthy,
            serving.Count == 1 ? "1 instance up" : $"All {serving.Count} instances up",
            string.Join("   ", serving.Select(s => $"{s.Display}:{s.Port}")));
    }
}
