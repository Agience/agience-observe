using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agience.Core;

/// <summary>
/// Everything this installation is configured with: a list of instances, and how to build them.
/// </summary>
/// <remarks>
/// <para>
/// A list, not a fixed set of services. This machine may run two Origins on two domains, one
/// Mantle and no Origin, or nothing at all. Every arrangement is expressible and none is privileged.
/// </para>
/// <para>
/// An empty list is a valid configuration, not an unconfigured one. It is what a fresh install
/// is, and the window says so rather than inventing a default Origin somebody did not ask for —
/// which would mint signing keys, on a domain nobody chose, behind their back.
/// </para>
/// </remarks>
public sealed class AgienceConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Bumped when a field's meaning changes in a way a reader must notice.</summary>
    [JsonPropertyName("version")] public int Version { get; set; } = 2;

    /// <summary>Every Origin and Mantle this machine is configured to run.</summary>
    [JsonPropertyName("instances")] public List<ServiceInstance> Instances { get; set; } = new();

    /// <summary>The directory each instance's own data directory is created under.</summary>
    [JsonPropertyName("data_root")] public string DataRoot { get; set; } = "";

    /// <summary>The interpreter the virtualenv was built from. Empty means "whatever was found".</summary>
    /// <remarks>
    /// Recorded rather than re-detected: a venv is bound to the interpreter that made it, so knowing
    /// which one that was is the difference between a useful message and a puzzle when the machine
    /// later grows a second Python.
    /// </remarks>
    [JsonPropertyName("base_python")] public string BasePython { get; set; } = "";

    /// <summary>Where the packages are installed from: a checkout on this disk, or git.</summary>
    /// <remarks>
    /// Not PyPI — that is a fact about the packages, not a preference. Their own
    /// <c>pip install agience-mantle</c> resolves nothing from an index; the two real sources are
    /// a checkout on this disk and the git remote, which is what this value selects between.
    /// </remarks>
    [JsonPropertyName("package_source")] public string PackageSource { get; set; } = "auto";

    /// <summary>The directory the sibling checkouts sit in, when installing from source.</summary>
    [JsonPropertyName("source_root")] public string SourceRoot { get; set; } = "";

    /// <summary>Start the tray when this user logs in.</summary>
    [JsonPropertyName("start_at_login")] public bool StartAtLogin { get; set; } = true;

    /// <summary>Start the enabled instances as soon as the tray starts.</summary>
    [JsonPropertyName("start_services_on_launch")] public bool StartServicesOnLaunch { get; set; } = true;

    /// <summary>Relaunch an instance that exits on its own.</summary>
    /// <remarks>
    /// Not a retry of a failed start. An instance that never bound its port is a
    /// configuration problem and is left down with its log named; this covers the process that ran
    /// for an hour and died, which is the case nobody is watching.
    /// </remarks>
    [JsonPropertyName("restart_on_exit")] public bool RestartOnExit { get; set; } = true;

    // ── derived ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The data root, with the default applied.</summary>
    [JsonIgnore]
    public string ResolvedDataRoot =>
        string.IsNullOrWhiteSpace(DataRoot) ? Paths.DefaultData : DataRoot.Trim();

    /// <summary>Instances in start order: every Origin, then every Mantle.</summary>
    /// <remarks>
    /// Every Origin starts before any Mantle. A Mantle verifies against an authority that must
    /// already be publishing keys; starting them interleaved makes a race present as a trust failure.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<ServiceInstance> InStartOrder =>
        Instances.OrderBy(i => Order(i.Kind)).ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList();

    private static int Order(string kind)
    {
        for (var i = 0; i < ServiceCatalog.All.Count; i++)
        {
            if (string.Equals(ServiceCatalog.All[i].Name, kind, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // An unknown kind sorts last rather than first. It cannot be started by this build
        // anyway, and putting it in front would delay everything that can be.
        return ServiceCatalog.All.Count;
    }

    /// <summary>This instance's own data directory: never shared, never derived from the domain.</summary>
    public string DataDirectoryOf(ServiceInstance instance) =>
        string.IsNullOrWhiteSpace(instance.DataDirectory)
            ? Path.Combine(ResolvedDataRoot, instance.Id)
            : instance.DataDirectory.Trim();

    /// <summary>Where this instance writes its output.</summary>
    public static string LogPathOf(ServiceInstance instance) =>
        Path.Combine(Paths.Logs, instance.Id + ".log");

    /// <summary>Find an instance by id.</summary>
    public ServiceInstance? ById(string id) =>
        Instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The authority this instance verifies against, as the public issuer a token carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>iss</c> and the <c>aud</c>, so it is the public URI, never the loopback
    /// one. A Mantle told to expect <c>http://127.0.0.1:8080</c> rejects a perfectly good token
    /// stamped <c>https://origin.home.agience.ai</c>, and the rejection reads "Invalid token
    /// audience" — which names the caller when the fault is here.
    /// </para>
    /// <para>
    /// Returns null when it cannot be decided, rather than guessing. A Mantle with two Origins
    /// configured on two domains and no choice recorded has a real ambiguity, and picking one would
    /// silently bind this store's whole trust to whichever happened to sort first.
    /// </para>
    /// </remarks>
    public string? AuthorityIssuerFor(ServiceInstance instance)
    {
        // An Origin is its own authority unless told to join another.
        if (string.Equals(instance.Kind, ServiceCatalog.Origin.Name, StringComparison.OrdinalIgnoreCase)
            && instance.Authority.Length == 0)
        {
            return instance.PublicUri;
        }

        if (instance.Authority.Contains("://", StringComparison.Ordinal))
        {
            return instance.Authority.Trim().TrimEnd('/');
        }

        if (instance.Authority.Length > 0)
        {
            return ById(instance.Authority)?.PublicUri;
        }

        return DefaultAuthorityFor(instance)?.PublicUri;
    }

    /// <summary>
    /// The address this instance REACHES its authority at — loopback when it is on this machine.
    /// </summary>
    /// <remarks>
    /// Deliberately different from <see cref="AuthorityIssuerFor"/>: the compose files make
    /// the same split, <c>ORIGIN_URI=http://origin:8080</c> beside
    /// <c>AUTHORITY_ISSUER=http://localhost:8080</c>. One is where to send a request, the other is
    /// what a token must say. Collapsing them means a local pair either cannot talk (the public
    /// name may not resolve, or may not point here) or stamps a loopback issuer no peer can verify.
    /// </remarks>
    public string? AuthorityAddressFor(ServiceInstance instance)
    {
        if (string.Equals(instance.Kind, ServiceCatalog.Origin.Name, StringComparison.OrdinalIgnoreCase)
            && instance.Authority.Length == 0)
        {
            return instance.LoopbackUri;
        }

        if (instance.Authority.Contains("://", StringComparison.Ordinal))
        {
            return instance.Authority.Trim().TrimEnd('/');
        }

        var local = instance.Authority.Length > 0 ? ById(instance.Authority) : DefaultAuthorityFor(instance);
        return local?.LoopbackUri;
    }

    /// <summary>
    /// The Origin a Mantle takes when nobody has said which.
    /// </summary>
    /// <remarks>
    /// One on the same domain first — that is the pair a person almost always means. Failing that,
    /// the only Origin there is. Two Origins on other domains is a genuine ambiguity and returns
    /// null, which the window turns into a question rather than a guess.
    /// </remarks>
    public ServiceInstance? DefaultAuthorityFor(ServiceInstance instance)
    {
        var origins = Instances
            .Where(i => string.Equals(i.Kind, ServiceCatalog.Origin.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sameDomain = origins
            .Where(o => string.Equals(o.Domain.Trim().Trim('.'), instance.Domain.Trim().Trim('.'),
                                      StringComparison.OrdinalIgnoreCase))
            .ToList();

        return sameDomain.Count == 1 ? sameDomain[0] : origins.Count == 1 ? origins[0] : null;
    }

    /// <summary>
    /// A port nothing else here is using, at or above this kind's base.
    /// </summary>
    /// <remarks>
    /// Avoids every configured instance, not just this kind's. Two kinds whose bases are close
    /// together will collide as instances are added, and a port assigned to two instances means one
    /// of them can never bind — reported as a failure over a configuration nobody typed.
    /// </remarks>
    public int NextFreePort(ServiceKind kind)
    {
        var taken = new HashSet<int>(Instances.Select(i => i.Port));
        for (var port = kind.BasePort; port < 65535; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        return kind.BasePort;
    }

    /// <summary>Create an instance of a kind, with an id, a port and a data directory of its own.</summary>
    public ServiceInstance Add(ServiceKind kind, string name, string domain = "")
    {
        var instance = new ServiceInstance
        {
            Id = ServiceInstance.MakeId(kind.Name, name, Instances.Select(i => i.Id)),
            Kind = kind.Name,
            Name = name,
            Domain = domain,
            Port = NextFreePort(kind),
            Enabled = true,
        };

        Instances.Add(instance);
        return instance;
    }

    /// <summary>
    /// Everything wrong with this configuration, in the words a person can act on.
    /// </summary>
    /// <remarks>
    /// The duplicate-port check is the one that earns its keep. Two instances on one port is not
    /// a crash: the first binds, the second exits, and the second is reported as failed forever over
    /// a setting nobody remembers typing.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        foreach (var instance in Instances)
        {
            if (instance.Problem() is { } problem)
            {
                problems.Add($"{instance.Display}: {problem}");
            }
        }

        foreach (var clash in Instances.GroupBy(i => i.Port).Where(g => g.Count() > 1))
        {
            problems.Add($"Port {clash.Key} is assigned to " +
                         string.Join(" and ", clash.Select(i => i.Display)) +
                         ". Only one of them could ever bind it; the other would be reported as failed.");
        }

        foreach (var clash in Instances.GroupBy(i => DataDirectoryOf(i), StringComparer.OrdinalIgnoreCase)
                                       .Where(g => g.Count() > 1))
        {
            // Sharing a data directory is silent, which is why it is refused rather than warned
            // about. Two Mantles on one directory share one lattice and one SSE index, and mantle's
            // own .env.example names the result: "Both come up healthy while answering searches
            // from each other's postings."
            problems.Add($"{string.Join(" and ", clash.Select(i => i.Display))} share the data " +
                         $"directory {clash.Key}. Each instance needs its own: sharing one means " +
                         "sharing a store and an index, and nothing reports it.");
        }

        foreach (var mantle in Instances.Where(i =>
                     string.Equals(i.Kind, ServiceCatalog.Mantle.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (AuthorityIssuerFor(mantle) is null)
            {
                problems.Add($"{mantle.Display} has no authority. Choose the Origin it verifies " +
                             "against, or give the address of one elsewhere — without it, every " +
                             "token it is handed is rejected.");
            }
        }

        return problems;
    }

    // ── persistence ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Read the settings, or hand back the defaults. Never throws.</summary>
    /// <remarks>
    /// A configuration that cannot be read must not produce a tray that will not start. An empty
    /// configuration is a state the window can show and a person can fix; a dialog at logon saying
    /// "invalid JSON" is not.
    /// </remarks>
    public static AgienceConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var loaded = JsonSerializer.Deserialize<AgienceConfig>(
                    File.ReadAllText(Paths.ConfigFile), Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Falls through to the defaults below, deliberately.
        }

        return new AgienceConfig();
    }

    /// <summary>Write the settings.</summary>
    /// <remarks>
    /// Written beside and moved into place. The tray polls while the window saves, and a
    /// half-written file is a real state that <see cref="Load"/> would silently replace with an
    /// empty configuration — discarding every instance somebody had just described.
    /// </remarks>
    public void Save()
    {
        Paths.EnsureRoot();
        var temp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, Paths.ConfigFile, overwrite: true);
    }

    /// <summary>A deep copy, for a screen that edits a draft and may be cancelled.</summary>
    public AgienceConfig Clone() =>
        JsonSerializer.Deserialize<AgienceConfig>(JsonSerializer.Serialize(this, Options), Options)!;
}
