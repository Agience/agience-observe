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
        // Its own directory when it has one — a node adopted from an earlier layout may keep its
        // lattice on another volume entirely. `ShardDirectoryOf` applies the `<home>/shard` default.
        var shard = Slashes(config.ShardDirectoryOf(instance));

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
            // ⛔ "NOT ORIGIN" NO LONGER MEANS "MANTLE". This branch used to set
            // `MANTLE_URI = instance.PublicUri` for everything that was not an Origin, which was
            // exactly right while Mantle was the only other kind — and becomes a quiet lie the
            // moment Crystal, Chorus or Ember exists: each would be told the store is itself, and
            // would then read and write nothing, at its own address, without an error.
            var isMantle = string.Equals(instance.Kind, ServiceCatalog.Mantle.Name,
                                         StringComparison.OrdinalIgnoreCase);

            if (isMantle)
            {
                env["MANTLE_URI"] = instance.PublicUri;
                env["FRONTEND_URI"] = instance.PublicUri;
            }
            else
            {
                // These CONSUME a store rather than being one. Loopback, because a peer this
                // configuration knows about is on this machine.
                if (config.PeerAddressFor(instance, ServiceCatalog.Mantle.Name) is { } store)
                {
                    env["MANTLE_URI"] = store;
                }
            }

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

        // ── the persona host's own three ───────────────────────────────────────────────────────
        // Set here and not in the catalogue because they are environment, not arguments.
        if (string.Equals(instance.Kind, ServiceCatalog.Chorus.Name, StringComparison.OrdinalIgnoreCase))
        {
            // ⚠ REQUIRED, AND NOT FOR THE REASON IT ONCE WAS. The host's index no longer reads it
            // — that comes off the request — but `crystal/type_registration.py` falls back to
            // `http://localhost:${MCP_PORT}` for the endpoint each persona ADVERTISES to the
            // gateway. Unset, every persona registers at a port it is not on and dispatch reaches
            // nothing, with both services reporting healthy.
            env["MCP_PORT"] = instance.Port.ToString();

            // Stated rather than left to that fallback, which spells the host `localhost`. On a
            // machine where `localhost` resolves to ::1 first, an advertised endpoint no caller
            // can reach is indistinguishable from a persona that failed to register.
            env["CHORUS_PUBLIC_URI"] = instance.LoopbackUri;

            // Where the personas register. `crystal/host.py` and `type_registration.py` both read
            // this name.
            if (config.PeerAddressFor(instance, ServiceCatalog.Crystal.Name) is { } gateway)
            {
                env["CRYSTAL_URI"] = gateway;
            }

            // ⛔ WITHOUT THIS A PERSONA LOSES ITS OPERATORS AND ITS FACETS, SILENTLY. Every chorus
            // manifest resolves its organons from a sha-verified bundle and `prism.runner` looks
            // for the payloads here first. Unset, `register_fns` raises,
            // `chorus.personas._persona_manifest` logs it and yields an EMPTY roster for that
            // persona — so the host still boots, still answers, and that persona's facets never
            // reach the router. Nothing reports a fault; the persona is simply not there.
            //
            // ⚠ THE INSTALLED PACKAGE, NOT A CHECKOUT. The payloads are declared package data
            // (`bundles/*.json` in agience-chorus's pyproject), so they ship inside the
            // distribution this application installed. `svc_env` names a checkout because the
            // fleet runs from one; naming a checkout here would point at a directory that need
            // not exist on a machine that installed from git.
            var bundles = Path.Combine(Paths.SitePackages, "agience_chorus", "bundles");
            if (Directory.Exists(bundles))
            {
                env["AGIENCE_BUNDLE_ROOT"] = Slashes(bundles);
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
                     config.ShardDirectoryOf(instance),
                     Path.Combine(home, ".data", "mantle-sse"),
                     Path.Combine(home, ".data", "mantle-cells"),
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string Slashes(string path) => path.Replace('\\', '/').TrimEnd('/');
}
