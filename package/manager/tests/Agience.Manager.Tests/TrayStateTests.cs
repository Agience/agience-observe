using Agience.Core;
using Xunit;

namespace Agience.Manager.Tests;

/// <summary>
/// The verdict rules. Every test is named for the rule it holds, and every rule is one this
/// application would otherwise get wrong in a way that misleads rather than merely annoys.
/// </summary>
public class TrayStateTests
{
    private static IReadOnlyList<ServiceSnapshot> All(params ServiceState[] states) =>
        states.Select((state, i) => new ServiceSnapshot(
            "instance-" + i, "Instance " + i, 8080 + i, state, state.ToString(),
            state == ServiceState.Serving ? 1 : null)).ToList();

    /// <summary>How many instances exist at all. Two, for every test that does not say otherwise.</summary>
    private const int Configured = 2;

    [Fact]
    public void A_node_with_no_runtime_never_reads_as_merely_stopped()
    {
        // Without an environment every service IS stopped, and reporting that would send a person
        // to press Start — which cannot work, and says nothing about why.
        var verdict = TrayState.Evaluate(runtimeReady: false, Configured,
            All(ServiceState.Stopped, ServiceState.Stopped, ServiceState.Stopped));

        Assert.Equal(TrayLevel.NotReady, verdict.Level);
        Assert.Contains("setup", verdict.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_services_is_not_all_services_up()
    {
        // An empty list satisfies "none of them is down" vacuously. Green here would paint a node
        // running nothing at all as healthy.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured, Array.Empty<ServiceSnapshot>());

        Assert.NotEqual(TrayLevel.Healthy, verdict.Level);
        Assert.Equal(TrayLevel.NotRunning, verdict.Level);
    }

    [Fact]
    public void Every_service_disabled_is_not_all_services_up()
    {
        // The same rule reached the other way: three disabled services are three services this node
        // does not run, not three services that are up.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Disabled, ServiceState.Disabled, ServiceState.Disabled));

        Assert.NotEqual(TrayLevel.Healthy, verdict.Level);
    }

    [Fact]
    public void A_starting_service_never_reads_as_healthy()
    {
        // Green over a service that has not answered yet is the one way this application can
        // actively mislead: a person acts on it, and it was never true.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Serving, ServiceState.Serving, ServiceState.Starting));

        Assert.NotEqual(TrayLevel.Healthy, verdict.Level);
        Assert.Equal(TrayLevel.Starting, verdict.Level);
    }

    [Fact]
    public void Foreign_outranks_degraded()
    {
        // ⚑ Two abandoned processes once held :80 and :443 on node 71 and the status surface
        // reported the proxy up, serving the squatter's 200. "Something is restarting" and "a
        // stranger holds your port" are different instructions.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Failed, ServiceState.Foreign, ServiceState.Serving));

        Assert.Equal(TrayLevel.Foreign, verdict.Level);
    }

    [Fact]
    public void Foreign_outranks_starting_too()
    {
        // Ordering is only a rule if it holds against every state below it, not just the one that
        // happened to be tested.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Starting, ServiceState.Foreign, ServiceState.Starting));

        Assert.Equal(TrayLevel.Foreign, verdict.Level);
    }

    [Fact]
    public void An_unrecognised_state_fails_closed()
    {
        // A later build can add a state this one has never heard of. Defaulting it to serving would
        // mean every state anyone invents is born green on every installation already out there.
        var invented = (ServiceState)99;
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Serving, ServiceState.Serving, invented));

        Assert.NotEqual(TrayLevel.Healthy, verdict.Level);
        Assert.Equal(TrayLevel.Degraded, verdict.Level);
    }

    [Fact]
    public void A_disabled_service_is_not_counted_as_a_failure()
    {
        // Disabled is a decision, not a fault. Counting it as down paints a permanent amber over a
        // correct configuration, and an icon that is always amber is an icon nobody reads.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Serving, ServiceState.Serving, ServiceState.Disabled));

        Assert.Equal(TrayLevel.Healthy, verdict.Level);
        Assert.Contains("2", verdict.Headline);
    }

    [Fact]
    public void Everything_serving_is_healthy()
    {
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Serving, ServiceState.Serving, ServiceState.Serving));

        Assert.Equal(TrayLevel.Healthy, verdict.Level);
    }

    [Fact]
    public void Everything_stopped_is_not_a_failure()
    {
        // A stopped node is a node somebody stopped. Amber would report their own action back to
        // them as a fault.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Stopped, ServiceState.Stopped, ServiceState.Stopped));

        Assert.Equal(TrayLevel.NotRunning, verdict.Level);
    }

    [Fact]
    public void A_half_stopped_node_is_not_all_up()
    {
        // The case a stop that only reached two of three produces. It is not healthy and it is not
        // "stopped" either.
        var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
            All(ServiceState.Serving, ServiceState.Stopped, ServiceState.Stopped));

        Assert.Equal(TrayLevel.Degraded, verdict.Level);
    }

    [Fact]
    public void Every_verdict_carries_a_sentence_a_person_can_act_on()
    {
        // A headline with no detail is a status light. The detail is the whole difference between
        // this and a coloured dot.
        foreach (var state in Enum.GetValues<ServiceState>())
        {
            var verdict = TrayState.Evaluate(runtimeReady: true, Configured,
                All(state, ServiceState.Serving, ServiceState.Serving));

            Assert.False(string.IsNullOrWhiteSpace(verdict.Headline), $"{state} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(verdict.Detail), $"{state} has no detail");
        }
    }

    [Fact]
    public void A_machine_with_nothing_configured_says_so_rather_than_reporting_a_fault()
    {
        // ⛔ THE FRESH-INSTALL STATE, AND IT IS NOT AN ERROR. "Stopped" would send a person to
        // press Start; "degraded" would report a fault over a machine nobody has told what to run.
        var verdict = TrayState.Evaluate(runtimeReady: true, configuredCount: 0,
            Array.Empty<ServiceSnapshot>());

        Assert.Equal(TrayLevel.NotRunning, verdict.Level);
        Assert.Contains("configured", verdict.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_configured_outranks_no_runtime()
    {
        // Both are true on a fresh install, and only one of them has a useful next action: there is
        // nothing to install a runtime FOR until something is configured.
        var verdict = TrayState.Evaluate(runtimeReady: false, configuredCount: 0,
            Array.Empty<ServiceSnapshot>());

        Assert.Equal(TrayLevel.NotRunning, verdict.Level);
        Assert.Contains("configured", verdict.Headline, StringComparison.OrdinalIgnoreCase);
    }
}
