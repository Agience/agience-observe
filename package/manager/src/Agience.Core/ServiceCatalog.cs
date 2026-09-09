namespace Agience.Core;

/// <summary>How a service is proved to be serving, once its process exists.</summary>
public enum HealthRule
{
    /// <summary>Something is listening on the port. The weakest honest claim, and the usual one.</summary>
    PortBound,

    /// <summary>The port is bound AND <see cref="ServiceKind.HealthPath"/> answers 200.</summary>
    HttpOk,
}

/// <summary>
/// A kind of service — what Origin is, not which Origin.
/// </summary>
/// <remarks>
/// <para>
/// This is a type, not an installation. A machine can run three Origins on three domains, or one
/// Mantle and no Origin at all. Everything that varies between them — the domain, the port, the data
/// directory, which authority it trusts — belongs to <see cref="ServiceInstance"/>. What is left
/// here is the part that is the same for every copy: the module to launch, the arguments, and how
/// the thing is proved to be up.
/// </para>
/// <para>
/// The command lines are the ones these services are actually run with, transcribed from
/// <c>agience-cloud/scripts/service_common.sh</c>'s <c>svc_cmd</c> rather than composed from a
/// template. <c>mantle.main:app</c> is the package form and not <c>mantle/main.py</c>: the two
/// import styles yield different class objects for the same class, so <c>except SomeError</c> stops
/// matching across the seam.
/// </para>
/// <para>
/// Every port is loopback. Nothing here binds <c>0.0.0.0</c>. A service on the public interface
/// answers past whatever header and path rules a proxy in front of it applies, and on a laptop that
/// joins untrusted networks it answers to them. A domain is served by putting something in front,
/// not by binding wider.
/// </para>
/// </remarks>
/// <param name="Name">The key in configuration, in instance ids and in log file names.</param>
/// <param name="Display">What the window calls it.</param>
/// <param name="Summary">One sentence, shown when adding one.</param>
/// <param name="Subdomain">The label put in front of a domain: <c>origin.home.agience.ai</c>.</param>
/// <param name="BasePort">Where the first instance of this kind is offered a port.</param>
/// <param name="Module">The <c>-m</c> module, so nothing depends on a console script being on PATH.</param>
/// <param name="ModuleArguments">Everything after the module; <c>{port}</c> is substituted.</param>
/// <param name="Health">How an instance of this kind is proved to be serving.</param>
/// <param name="HealthPath">The path probed under <see cref="HealthRule.HttpOk"/>.</param>
public sealed record ServiceKind(
    string Name,
    string Display,
    string Summary,
    string Subdomain,
    int BasePort,
    string Module,
    IReadOnlyList<string> ModuleArguments,
    HealthRule Health = HealthRule.PortBound,
    string? HealthPath = null)
{
    /// <summary>The arguments to hand <c>python.exe</c>, with the port filled in.</summary>
    public IReadOnlyList<string> ArgumentsFor(int port)
    {
        var args = new List<string> { "-m", Module };
        foreach (var a in ModuleArguments)
        {
            args.Add(a.Replace("{port}", port.ToString()));
        }

        return args;
    }
}

/// <summary>
/// The kinds of service this application can run, in start order.
/// </summary>
/// <remarks>
/// Ember is not here yet, deliberately. Origin and Mantle are the seed — an authority to say who
/// anyone is, and a store to ground what they know — and between them they are the minimum from
/// which a network can be grown. Adding a kind is additive: one record below, and one entry in the
/// install plan.
/// </remarks>
public static class ServiceCatalog
{
    /// <summary>
    /// The identity authority. Everything else verifies the tokens it issues.
    /// </summary>
    /// <remarks>
    /// Its health rule is the JWKS, not the port, and it is the one exception in this table. An
    /// Origin that is listening but publishes no key set is an authority no peer could ever verify —
    /// the shell supervisor's own preflight refuses a node in that state, and a green icon over it
    /// would be this application's most expensive lie.
    /// </remarks>
    public static ServiceKind Origin { get; } = new(
        "origin", "Origin",
        "Identity: principals, grants, and the keys every peer verifies against.",
        "origin", 8080, "uvicorn",
        new[] { "origin.main:app", "--host", "127.0.0.1", "--port", "{port}", "--log-level", "info" },
        HealthRule.HttpOk, "/.well-known/jwks.json");

    /// <summary>The store: artifacts, versions, grants, and the encrypted lexical index.</summary>
    public static ServiceKind Mantle { get; } = new(
        "mantle", "Mantle",
        "Memory: the artifact store, its version lineage and its search index.",
        "mantle", 8082, "uvicorn",
        new[] { "mantle.main:app", "--host", "127.0.0.1", "--port", "{port}", "--log-level", "warning" });

    /// <summary>
    /// Every kind, in start order.
    /// </summary>
    /// <remarks>
    /// Start order is a dependency order, not a preference. Every Origin starts before any Mantle,
    /// because a Mantle verifies against an authority that must already be publishing keys. Started
    /// together, Mantle reports a trust failure rather than a race — and the person reading that
    /// message goes looking at trust configuration.
    /// </remarks>
    public static IReadOnlyList<ServiceKind> All { get; } = new[] { Origin, Mantle };

    /// <summary>Look a kind up by its configuration key. Null for one this build does not know.</summary>
    public static ServiceKind? ByName(string name) =>
        All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
}
