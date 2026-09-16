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
/// Origin and Mantle are the seed — an authority to say who anyone is, and a store to ground what
/// they know — and between them they are the minimum from which a network can be grown. The other
/// three make a machine a full node rather than a member: Ember reasons, Crystal routes, Chorus
/// hosts the personas.
///
/// Adding a kind is additive and stays that way: one record below, one entry in
/// <see cref="PythonEnvironment"/>'s install plan, and — only where the service needs something
/// the others do not — a branch in <see cref="ServiceEnvironment"/>. Nothing else enumerates
/// kinds; start order and the configuration screen both read this list.
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

    /// <summary>The reasoning runtime: an OpenAI-compatible endpoint over the local models.</summary>
    /// <remarks>
    /// <c>ember.cli</c> and not <c>uvicorn</c> — this one owns its own server, and the subcommand
    /// is part of the module arguments.
    ///
    /// ⭐ PROVED BY <c>/health</c>, WHICH IS WHAT THE OTHER INSTALLER ALREADY USED. This was
    /// pinned to the bound port on the grounds that no liveness path was documented; measured
    /// 2026-09-15, `ember/surface/serve.py` serves one and `package/install/cli/agience.py`
    /// declares `Service("ember", 8091, "/health", …)`. Two installers of the same node disagreeing
    /// about how a service is proved up is a difference that survives precisely because each looks
    /// right on its own.
    ///
    /// ⚠ AND IT IS A STRONGER CHECK THAN THE PORT, NOT MERELY A DIFFERENT ONE. With a store bound,
    /// that handler calls <c>genesis.health</c> and answers 503 when the store cannot be shown
    /// healthy — the wedged state this fleet has hit before. A bound port says a socket is open;
    /// this says the service can answer for itself.
    /// </remarks>
    public static ServiceKind Ember { get; } = new(
        "ember", "Ember",
        "Reasoning: an OpenAI-compatible endpoint over the models this machine holds.",
        "ember", 8091, "ember.cli",
        new[] { "serve", "--host", "127.0.0.1", "--port", "{port}" },
        HealthRule.HttpOk, "/health");

    /// <summary>
    /// The gateway: op dispatch, persona registration, and the resolved type catalogue.
    /// </summary>
    /// <remarks>
    /// ⭐ A GATEWAY, NOT A HOST [John, 2026-09-14]. Crystal is the routing and condensation layer;
    /// it answers inside the chorus domain and fronts the personas rather than sitting behind them.
    /// There is no "crystal host".
    ///
    /// Its health path is <c>/health</c>, and Chorus's is <c>/healthz</c> — the two are genuinely
    /// different surfaces and the near-identical spelling is a trap. Verified in
    /// <c>crystal/main.py</c> and <c>crystal/host.py</c> on 2026-09-15.
    /// </remarks>
    public static ServiceKind Crystal { get; } = new(
        "crystal", "Crystal",
        "The gateway: op dispatch, persona registration, and the resolved type catalogue.",
        "crystal", 8085, "uvicorn",
        new[] { "crystal.main:app", "--host", "127.0.0.1", "--port", "{port}", "--log-level", "info" },
        HealthRule.HttpOk, "/health");

    /// <summary>The persona host: it mounts each persona behind the gateway.</summary>
    /// <remarks>
    /// ⛔ THE SPEC IS <c>agience_chorus.server:app</c>, NOT <c>server:app</c>. The module lives
    /// inside the package, so the bare name resolves to nothing and the service cannot start at
    /// all — measured on the fleet 2026-09-14.
    ///
    /// ⚠ NO <c>--app-dir</c> HERE, AND THAT IS THE DIFFERENCE FROM <c>svc_cmd</c>. The fleet runs
    /// this from a source checkout and has to put <c>agience-chorus/src</c> on the path; this
    /// application installs the distribution into its own environment, where
    /// <c>agience_chorus.server</c> is importable outright. Carrying the fleet's flag across would
    /// hard-code a checkout path into a build that has no checkout.
    ///
    /// ⚠ <c>MCP_PORT</c> is still required, and is set in <see cref="ServiceEnvironment"/> rather
    /// than here: it is an environment variable, not an argument, and without it every persona
    /// registers itself at a port it is not on.
    /// </remarks>
    public static ServiceKind Chorus { get; } = new(
        "chorus", "Chorus",
        "The persona host: it mounts each persona and registers them with the gateway.",
        "chorus", 8087, "uvicorn",
        new[] { "agience_chorus.server:app", "--host", "127.0.0.1", "--port", "{port}", "--log-level", "info" },
        HealthRule.HttpOk, "/healthz");

    /// <summary>
    /// Every kind, in start order.
    /// </summary>
    /// <remarks>
    /// Start order is a dependency order, not a preference.
    ///
    /// Every Origin starts before any Mantle, because a Mantle verifies against an authority that
    /// must already be publishing keys. Started together, Mantle reports a trust failure rather
    /// than a race — and the person reading that message goes looking at trust configuration.
    ///
    /// Crystal starts before Chorus for the mirror-image reason: the personas Chorus mounts
    /// register themselves with the gateway on startup (<c>POST /register</c>), so a Chorus that
    /// comes up first advertises into nothing. Crystal retries registration, so the order is a
    /// courtesy rather than a hard dependency — but the first-boot log is much easier to read when
    /// it is honoured.
    /// </remarks>
    public static IReadOnlyList<ServiceKind> All { get; } =
        new[] { Origin, Mantle, Ember, Crystal, Chorus };

    /// <summary>Look a kind up by its configuration key. Null for one this build does not know.</summary>
    public static ServiceKind? ByName(string name) =>
        All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
}
