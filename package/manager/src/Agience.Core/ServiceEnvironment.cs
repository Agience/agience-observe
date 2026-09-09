namespace Agience.Core;

/// <summary>
/// The environment one instance is launched with, derived from its own configuration.
/// </summary>
/// <remarks>
/// <para>
/// Every path here is the instance's own. Two Mantles on one machine get two lattices, two keys
/// directories and two index directories, because everything below hangs off
/// <see cref="AgienceConfig.DataDirectoryOf"/> rather than off a shared root. Mantle's own
/// <c>.env.example</c> names what sharing produces: <i>"Both come up healthy while answering
/// searches from each other's postings."</i>
/// </para>
/// <para>
/// Forward slashes, deliberately: these are read by Python, written into logs and pasted into
/// issue reports. A backslash survives none of those cleanly, and Windows accepts a forward slash
/// everywhere a backslash works.
/// </para>
/// </remarks>
public static class ServiceEnvironment
{
    /// <summary>Build the environment for one instance.</summary>
    public static Dictionary<string, string> For(AgienceConfig config, ServiceInstance instance)
    {
        var home = Slashes(config.DataDirectoryOf(instance));

        // The store, named once for both readers. There are two resolvers and they read different
        // variables: `mantle.db.lattice_api.open_database()` reads MANTLE_LATTICE_PATH, and
        // `mantle.shard.sqlite_store.open_sqlite_store()` reads EMBER_SQLITE_DIR. Setting only the
        // second leaves mantle on its relative default, `mantle-lattice.db`, resolved against the
        // working directory: it mints a small store, reports a handful of artifacts to reindex, and
        // serves an empty universe while looking healthy. A store with five artifacts in it is a
        // valid store, so nothing errors.
        var shard = home + "/shard";

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Unbuffered, or a crashed service's last words sit in a pipe buffer that is discarded
            // with the process — and the log ends several lines before the reason.
            ["PYTHONUNBUFFERED"] = "1",

            ["AGIENCE_BASE_DIR"] = home,
            ["KEYS_DIR"] = home + "/keys",
            ["ORIGIN_DB_PATH"] = home + "/origin/origin.db",

            // The two index directories are set rather than left to default. Both derive from the
            // install root and not from the store path, so two instances with separate stores would
            // still share one index directory.
            ["MANTLE_SSE_DIR"] = home + "/.data/mantle-sse",
            ["MANTLE_CELL_DIR"] = home + "/.data/mantle-cells",

            ["EMBER_SQLITE_DIR"] = shard,
            ["MANTLE_LATTICE_PATH"] = shard + "/lattice.db",

            // Pin the BLAS thread pool. `numpy.linalg.eigh` faults when two Python threads call
            // it at once on this stack — reproduced with no agience code, 3/3 access violations,
            // 0/3 with the pool pinned to one. An ASGI host dispatches sync endpoints on a
            // threadpool, so two concurrent requests that both reach the instrument kill the whole
            // process with no traceback and no log line. It also hangs instead of faulting, so "it
            // worked" is not evidence of safety.
            ["OPENBLAS_NUM_THREADS"] = "1",
            ["OMP_NUM_THREADS"] = "1",

            // Which instance a line came from, without anyone parsing a command line.
            ["AGIENCE_INSTANCE"] = instance.Id,
        };

        // ── who this instance is, and who it trusts ─────────────────────────────────────────────
        var isOrigin = string.Equals(instance.Kind, ServiceCatalog.Origin.Name,
                                     StringComparison.OrdinalIgnoreCase);

        if (isOrigin)
        {
            // Its own public identity. This is what it stamps into every token it issues.
            env["ORIGIN_URI"] = instance.PublicUri;

            // Where a browser is sent back to after signing in. Origin refuses a redirect to any
            // base it was not told about, so an unset value is a sign-in that completes at the
            // authority and then fails at the last hop with "Invalid redirect_uri".
            env["FRONTEND_URI"] = instance.PublicUri;
        }
        else
        {
            env["MANTLE_URI"] = instance.PublicUri;
            env["FRONTEND_URI"] = instance.PublicUri;

            // Two different values for the authority, and collapsing them breaks one case or the
            // other. ORIGIN_URI is where to send a request — loopback when the Origin is on this
            // machine, because the public name may not resolve here or may not point here at all.
            // AUTHORITY_ISSUER is what a token must say, which is always the public form. The
            // compose files make the same split for the same reason.
            if (config.AuthorityAddressFor(instance) is { } address)
            {
                env["ORIGIN_URI"] = address;
            }
        }

        if (config.AuthorityIssuerFor(instance) is { } issuer)
        {
            env["AUTHORITY_ISSUER"] = issuer;
        }

        return env;
    }

    /// <summary>
    /// Create the directories an instance will otherwise fail to write into.
    /// </summary>
    /// <remarks>
    /// Created here rather than left to the service. SQLite does not create a missing parent
    /// directory: it reports "unable to open database file", which reads like corruption and sends
    /// a person looking at the store rather than at the path.
    /// </remarks>
    public static void EnsureDirectories(AgienceConfig config, ServiceInstance instance)
    {
        var home = config.DataDirectoryOf(instance);
        foreach (var dir in new[]
                 {
                     home,
                     Path.Combine(home, "keys"),
                     Path.Combine(home, "origin"),
                     Path.Combine(home, "shard"),
                     Path.Combine(home, ".data", "mantle-sse"),
                     Path.Combine(home, ".data", "mantle-cells"),
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string Slashes(string path) => path.Replace('\\', '/').TrimEnd('/');
}
