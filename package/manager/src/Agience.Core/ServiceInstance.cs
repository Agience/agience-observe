using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agience.Core;

/// <summary>
/// One Origin or one Mantle, as this machine is configured to run it.
/// </summary>
/// <remarks>
/// <para>
/// A machine runs a list of these, not a fixed set. Two Origins on two domains is an ordinary
/// arrangement — one serving <c>home.agience.ai</c> and one serving <c>lab.agience.ai</c> — and so
/// is a Mantle with no Origin beside it, verifying against an authority somewhere else. Modelling
/// the machine as "the Origin and the Mantle" makes both of those inexpressible, and the second one
/// is what joining a network looks like.
/// </para>
/// <para>
/// Each instance owns its own data directory. Two Mantles sharing one directory share one lattice,
/// one keys directory and — worst, because nothing errors — one SSE index. Mantle's own
/// <c>.env.example</c> names the symptom:
/// <i>"Both come up healthy while answering searches from each other's postings."</i>
/// </para>
/// </remarks>
public sealed class ServiceInstance
{
    /// <summary>
    /// Stable, unique, and used for the directory name and the log file name.
    /// </summary>
    /// <remarks>
    /// It never changes once set, even when the name or the domain does. It is the name of a
    /// directory holding keys and a store; renaming it would orphan both while the instance came up
    /// healthy and empty beside them.
    /// </remarks>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary><c>origin</c> or <c>mantle</c> — the key of a <see cref="ServiceKind"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>What a person calls this one: <c>home</c>, <c>lab</c>, <c>work</c>.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// The domain this instance answers under, e.g. <c>home.agience.ai</c>. Empty = loopback only.
    /// </summary>
    /// <remarks>
    /// Empty is a complete, working configuration and not a missing value: the service binds
    /// loopback and is addressed by <c>127.0.0.1</c>, which is what a laptop wants. A domain is what
    /// you set when something other than this machine has to reach it.
    /// </remarks>
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";

    /// <summary>The loopback port this instance binds. Unique across every instance.</summary>
    [JsonPropertyName("port")] public int Port { get; set; }

    /// <summary>Where this instance's keys, store and indexes live. Empty = under the data root.</summary>
    [JsonPropertyName("data_directory")] public string DataDirectory { get; set; } = "";

    /// <summary>Does this machine run it?</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which authority this instance trusts. Empty means "work it out".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things can be written here, and the third is what joining means.
    /// </para>
    /// <list type="bullet">
    /// <item>empty — an Origin is its own authority; a Mantle takes the only Origin configured here,
    /// preferring one on its own domain.</item>
    /// <item>the <see cref="Id"/> of an Origin on this machine — the ordinary local pair.</item>
    /// <item>an absolute <c>https://</c> URL — an authority somewhere else. This node then verifies
    /// tokens it did not issue, which is exactly what joining somebody's plane is.</item>
    /// </list>
    /// </remarks>
    [JsonPropertyName("authority")] public string Authority { get; set; } = "";

    /// <summary>The kind, resolved. Null when a later build wrote a kind this one does not know.</summary>
    [JsonIgnore] public ServiceKind? Definition => ServiceCatalog.ByName(Kind);

    /// <summary>What the window calls this instance: <c>Origin — home</c>.</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            var kind = Definition?.Display ?? Kind;
            return string.IsNullOrWhiteSpace(Name) ? kind : $"{kind} — {Name}";
        }
    }

    /// <summary>Loopback, always. The address anything on this machine reaches it at.</summary>
    [JsonIgnore] public string LoopbackUri => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// The address the outside world reaches this instance at, and the <c>iss</c> an Origin stamps.
    /// </summary>
    /// <remarks>
    /// The domain form carries no port and the loopback form does. Under a domain, something in
    /// front terminates TLS on 443 and proxies to the loopback port; writing that port into the
    /// public URI would publish an address no browser can reach.
    /// </remarks>
    [JsonIgnore]
    public string PublicUri
    {
        get
        {
            var domain = Domain.Trim().Trim('.');
            var subdomain = Definition?.Subdomain;
            return domain.Length == 0 || subdomain is null
                ? LoopbackUri
                : $"https://{subdomain}.{domain}";
        }
    }

    /// <summary>Is this instance addressable from anywhere but this machine?</summary>
    [JsonIgnore] public bool HasDomain => Domain.Trim().Trim('.').Length > 0;

    /// <summary>
    /// A stable id from a kind and a name, made unique against what already exists.
    /// </summary>
    /// <remarks>
    /// The suffix is a number rather than the domain. A domain can be changed later and the id
    /// cannot, so an id built from one would be a directory named after a fact that has moved on.
    /// </remarks>
    public static string MakeId(string kind, string name, IEnumerable<string> taken)
    {
        var slug = Slug(name);
        var stem = slug.Length == 0 ? kind : $"{kind}-{slug}";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Lower-case, letters digits and hyphens only — it becomes a directory name.</summary>
    private static string Slug(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    /// <summary>
    /// Why this instance cannot be run as configured, or null.
    /// </summary>
    /// <remarks>
    /// The domain is checked for shape, not resolved. Whether DNS points at this machine is not
    /// this application's business — a domain is often configured before it resolves, and refusing
    /// it would make the ordinary order of work impossible.
    /// </remarks>
    public string? Problem()
    {
        if (Definition is null)
        {
            return $"'{Kind}' is not a kind of service this version knows how to run.";
        }

        if (Port is < 1 or > 65535)
        {
            return $"Port {Port} is not a port.";
        }

        var domain = Domain.Trim().Trim('.');
        if (domain.Length > 0 && !Regex.IsMatch(domain, @"^[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?)+$"))
        {
            return $"'{Domain}' is not a domain name. Leave it empty to run on this machine only.";
        }

        if (Authority.Length > 0 && Authority.Contains("://", StringComparison.Ordinal)
            && (!Uri.TryCreate(Authority, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return $"The authority '{Authority}' is not an http or https address.";
        }

        return null;
    }
}
