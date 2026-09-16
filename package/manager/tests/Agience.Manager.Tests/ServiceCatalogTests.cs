using Agience.Core;
using Xunit;

namespace Agience.Manager.Tests;

/// <summary>
/// The service table and the environment built from it.
/// </summary>
/// <remarks>
/// ⛔ THESE HOLD THE FACTS THAT ARE SILENT WHEN WRONG. An instance pointed at the wrong store, a
/// public URI on the wrong port, an Origin proved healthy by its port rather than by its keys —
/// none of them throws, none of them logs, and every one of them presents as something else.
/// </remarks>
public class ServiceCatalogTests
{
    private static (AgienceConfig Config, ServiceInstance Origin, ServiceInstance Mantle) Pair(
        string domain = "home.agience.ai")
    {
        var config = new AgienceConfig();
        var origin = config.Add(ServiceCatalog.Origin, "home", domain);
        var mantle = config.Add(ServiceCatalog.Mantle, "home", domain);
        return (config, origin, mantle);
    }

    [Fact]
    public void Origin_is_started_before_mantle()
    {
        // It issues the identity Mantle verifies against.
        Assert.Equal("origin", ServiceCatalog.All[0].Name);
        Assert.Equal("mantle", ServiceCatalog.All[1].Name);
    }

    [Fact]
    public void Origin_is_proved_by_its_keys_and_not_by_its_port()
    {
        // An Origin that is listening but publishes no key set is an authority no peer could ever
        // verify. The shell supervisor's preflight refuses a node in that state; a green icon over
        // it would be this application's most expensive lie.
        Assert.Equal(HealthRule.HttpOk, ServiceCatalog.Origin.Health);
        Assert.Equal("/.well-known/jwks.json", ServiceCatalog.Origin.HealthPath);
    }

    [Fact]
    public void No_kind_binds_a_public_interface()
    {
        // A service on 0.0.0.0 answers past whatever header and path rules a proxy in front applies
        // — and on a laptop that joins untrusted networks, it answers to them.
        foreach (var kind in ServiceCatalog.All)
        {
            var arguments = kind.ArgumentsFor(1234);
            var host = arguments.SkipWhile(a => a != "--host").Skip(1).FirstOrDefault();
            Assert.Equal("127.0.0.1", host);
        }
    }

    [Fact]
    public void The_port_reaches_every_command_line()
    {
        // Everything that manages a service finds it by the port it is EXPECTED on, so a literal
        // left in an argument list would bind one port while everything watched another.
        foreach (var kind in ServiceCatalog.All)
        {
            var arguments = kind.ArgumentsFor(9999);
            Assert.Contains("9999", arguments);
            Assert.DoesNotContain(arguments, a => a.Contains("{port}"));
        }
    }

    [Fact]
    public void Mantle_and_its_shard_are_pointed_at_one_store()
    {
        // ⛔ THE SILENT ONE. Two resolvers read two variables:
        // `mantle.db.lattice_api.open_database()` reads MANTLE_LATTICE_PATH, and
        // `mantle.shard.sqlite_store.open_sqlite_store()` reads EMBER_SQLITE_DIR. Setting only the
        // second leaves mantle on its relative default, `mantle-lattice.db`, resolved against the
        // working directory: it mints a small store, reports a handful of artifacts to reindex, and
        // serves an empty universe while looking healthy.
        var (config, _, mantle) = Pair();
        var env = ServiceEnvironment.For(config, mantle);

        Assert.StartsWith(env["EMBER_SQLITE_DIR"], env["MANTLE_LATTICE_PATH"],
                          StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_mantles_get_two_stores_and_two_indexes()
    {
        // The rule tested the way it actually breaks. Mantle's own .env.example names the symptom:
        // "Both come up healthy while answering searches from each other's postings."
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Mantle, "home");
        var b = config.Add(ServiceCatalog.Mantle, "lab");

        var first = ServiceEnvironment.For(config, a);
        var second = ServiceEnvironment.For(config, b);

        foreach (var key in new[] { "MANTLE_LATTICE_PATH", "MANTLE_SSE_DIR", "MANTLE_CELL_DIR",
                                    "KEYS_DIR", "EMBER_SQLITE_DIR", "AGIENCE_BASE_DIR" })
        {
            Assert.NotEqual(first[key], second[key]);
        }
    }

    [Fact]
    public void Two_origins_get_two_identity_databases()
    {
        // ⛔ ONE origin.db BETWEEN TWO AUTHORITIES would merge two populations of accounts into one
        // file while both reported success.
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Origin, "home", "home.agience.ai");
        var b = config.Add(ServiceCatalog.Origin, "lab", "lab.agience.ai");

        Assert.NotEqual(ServiceEnvironment.For(config, a)["ORIGIN_DB_PATH"],
                        ServiceEnvironment.For(config, b)["ORIGIN_DB_PATH"]);
    }

    [Fact]
    public void The_blas_thread_pool_is_pinned()
    {
        // `numpy.linalg.eigh` faults when two Python threads call it at once on this stack —
        // reproduced with no agience code, 3/3 access violations, 0/3 with the pool pinned. An ASGI
        // host dispatches sync endpoints on a threadpool, so two concurrent requests kill the whole
        // process with no traceback and no log line. It also hangs instead of faulting, so "it
        // worked" is not evidence of safety.
        var (config, _, mantle) = Pair();
        var env = ServiceEnvironment.For(config, mantle);

        Assert.Equal("1", env["OPENBLAS_NUM_THREADS"]);
        Assert.Equal("1", env["OMP_NUM_THREADS"]);
    }

    [Fact]
    public void A_mantle_is_told_the_public_issuer_and_the_loopback_address()
    {
        // ORIGIN_URI is where to send a request; AUTHORITY_ISSUER is what a token must say. The
        // compose files make the same split for the same reason.
        var (config, origin, mantle) = Pair();
        var env = ServiceEnvironment.For(config, mantle);

        Assert.Equal("https://origin.home.agience.ai", env["AUTHORITY_ISSUER"]);
        Assert.Equal($"http://127.0.0.1:{origin.Port}", env["ORIGIN_URI"]);
    }

    [Fact]
    public void An_origin_stamps_its_own_public_name()
    {
        var (config, origin, _) = Pair();
        var env = ServiceEnvironment.For(config, origin);

        Assert.Equal("https://origin.home.agience.ai", env["ORIGIN_URI"]);
        Assert.Equal("https://origin.home.agience.ai", env["AUTHORITY_ISSUER"]);
    }

    [Fact]
    public void A_mantle_with_no_decidable_authority_is_told_nothing_rather_than_a_guess()
    {
        // ⛔ AN INVENTED ISSUER IS WORSE THAN A MISSING ONE. Absent, the service refuses tokens and
        // says so; wrong, it refuses them and blames the caller.
        var config = new AgienceConfig();
        config.Add(ServiceCatalog.Origin, "lab", "lab.agience.ai");
        config.Add(ServiceCatalog.Origin, "work", "work.agience.ai");
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");

        var env = ServiceEnvironment.For(config, mantle);
        Assert.False(env.ContainsKey("AUTHORITY_ISSUER"));
    }

    [Fact]
    public void Every_path_handed_to_python_uses_forward_slashes()
    {
        // These are read by Python, written into logs and pasted into issue reports. A backslash
        // survives none of those cleanly, and Windows accepts a forward slash everywhere.
        var config = new AgienceConfig { DataRoot = @"C:\Users\someone\Agience\data" };
        var mantle = config.Add(ServiceCatalog.Mantle, "home");
        mantle.Authority = "https://origin.example.test";

        var env = ServiceEnvironment.For(config, mantle);
        foreach (var key in new[] { "AGIENCE_BASE_DIR", "KEYS_DIR", "ORIGIN_DB_PATH",
                                    "MANTLE_LATTICE_PATH", "MANTLE_SSE_DIR", "EMBER_SQLITE_DIR" })
        {
            Assert.DoesNotContain('\\', env[key]);
        }
    }

    [Fact]
    public void A_loopback_instance_is_a_complete_configuration()
    {
        // Empty domain is what a laptop wants, not a missing value.
        var config = new AgienceConfig();
        var origin = config.Add(ServiceCatalog.Origin, "local");

        Assert.Empty(config.Problems());
        Assert.Equal($"http://127.0.0.1:{origin.Port}", origin.PublicUri);
        Assert.Equal(origin.PublicUri, ServiceEnvironment.For(config, origin)["ORIGIN_URI"]);
    }

    [Fact]
    public void Each_instance_is_named_in_its_own_environment()
    {
        // So a log line, a crash report or a process listing says WHICH of three Mantles it came
        // from without anyone parsing a command line.
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Mantle, "home");

        Assert.Equal(a.Id, ServiceEnvironment.For(config, a)["AGIENCE_INSTANCE"]);
    }

    [Fact]
    public void No_two_kinds_share_a_base_port()
    {
        var ports = ServiceCatalog.All.Select(k => k.BasePort).ToList();
        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    // ── the full node: Ember, Crystal and Chorus ────────────────────────────────────────────────
    //
    // ⛔ EVERY FAILURE BELOW IS SILENT. A service told it is its own store reads and writes nothing
    // at its own address; a persona that advertises the wrong port registers successfully and is
    // then dispatched to by nobody. Both report healthy throughout, which is why these are held
    // here rather than left to be noticed.

    /// <summary>A whole node: the five kinds on one domain.</summary>
    private static AgienceConfig Node(string domain = "home.agience.ai")
    {
        var config = new AgienceConfig();
        foreach (var kind in ServiceCatalog.All)
        {
            config.Add(kind, "home", domain);
        }

        return config;
    }

    private static ServiceInstance Of(AgienceConfig config, ServiceKind kind) =>
        config.Instances.Single(i => string.Equals(i.Kind, kind.Name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Crystal_starts_before_chorus()
    {
        // The personas Chorus mounts register themselves with the gateway on startup, so a Chorus
        // that comes up first advertises into nothing.
        var order = ServiceCatalog.All.Select(k => k.Name).ToList();
        Assert.True(order.IndexOf("crystal") < order.IndexOf("chorus"),
                    $"crystal must precede chorus in start order, got: {string.Join(", ", order)}");
    }

    [Fact]
    public void The_gateway_and_the_persona_host_are_proved_at_different_paths()
    {
        // `/health` and `/healthz` are genuinely different surfaces, and the near-identical
        // spelling is the trap: the wrong one 404s for ever and paints a working service red.
        Assert.Equal("/health", ServiceCatalog.Crystal.HealthPath);
        Assert.Equal("/healthz", ServiceCatalog.Chorus.HealthPath);
    }

    [Fact]
    public void A_service_that_consumes_the_store_is_not_told_that_it_is_the_store()
    {
        // The regression this guards: `MANTLE_URI = instance.PublicUri` for everything that was
        // not an Origin. Correct while Mantle was the only other kind; a quiet lie afterwards.
        var config = Node();
        var store = Of(config, ServiceCatalog.Mantle);

        foreach (var kind in new[] { ServiceCatalog.Crystal, ServiceCatalog.Chorus, ServiceCatalog.Ember })
        {
            var instance = Of(config, kind);
            var env = ServiceEnvironment.For(config, instance);

            Assert.True(env.ContainsKey("MANTLE_URI"), $"{kind.Display} was told nothing about the store");
            Assert.Equal($"http://127.0.0.1:{store.Port}", env["MANTLE_URI"]);
            Assert.NotEqual(instance.LoopbackUri, env["MANTLE_URI"]);
            Assert.NotEqual(instance.PublicUri, env["MANTLE_URI"]);
        }
    }

    [Fact]
    public void The_store_is_still_told_that_it_is_the_store()
    {
        // The branch above must not have moved Mantle's own case.
        var config = Node();
        var mantle = Of(config, ServiceCatalog.Mantle);
        var env = ServiceEnvironment.For(config, mantle);

        Assert.Equal(mantle.PublicUri, env["MANTLE_URI"]);
    }

    [Fact]
    public void A_persona_host_advertises_the_port_it_is_actually_on()
    {
        // Unset or wrong, every persona registers at a port it is not on: registration succeeds,
        // dispatch reaches nothing, and both services report healthy.
        var config = Node();
        var chorus = Of(config, ServiceCatalog.Chorus);
        var env = ServiceEnvironment.For(config, chorus);

        Assert.Equal(chorus.Port.ToString(), env["MCP_PORT"]);
        Assert.Equal(chorus.LoopbackUri, env["CHORUS_PUBLIC_URI"]);
    }

    [Fact]
    public void A_persona_host_is_told_where_the_gateway_is()
    {
        var config = Node();
        var crystal = Of(config, ServiceCatalog.Crystal);
        var env = ServiceEnvironment.For(config, Of(config, ServiceCatalog.Chorus));

        Assert.Equal($"http://127.0.0.1:{crystal.Port}", env["CRYSTAL_URI"]);
    }

    [Fact]
    public void Only_the_persona_host_is_given_the_persona_host_variables()
    {
        // MCP_PORT reaching another kind would be inert here and confusing in a log, but the real
        // point is that the branch is keyed on the kind rather than on "not origin, not mantle".
        var config = Node();
        foreach (var kind in new[] { ServiceCatalog.Origin, ServiceCatalog.Mantle, ServiceCatalog.Crystal, ServiceCatalog.Ember })
        {
            var env = ServiceEnvironment.For(config, Of(config, kind));
            Assert.False(env.ContainsKey("MCP_PORT"), $"{kind.Display} was handed MCP_PORT");
            Assert.False(env.ContainsKey("CHORUS_PUBLIC_URI"), $"{kind.Display} was handed CHORUS_PUBLIC_URI");
        }
    }

    [Fact]
    public void An_ambiguous_peer_is_not_guessed()
    {
        // Two stores and no choice recorded is a real ambiguity. Picking whichever sorted first
        // would bind the gateway to one of them silently — the same rule the authority case has.
        var config = new AgienceConfig();
        config.Add(ServiceCatalog.Mantle, "one", "a.example");
        config.Add(ServiceCatalog.Mantle, "two", "b.example");
        var crystal = config.Add(ServiceCatalog.Crystal, "gw", "c.example");

        var env = ServiceEnvironment.For(config, crystal);
        Assert.False(env.ContainsKey("MANTLE_URI"));
    }

    [Fact]
    public void Ember_runs_its_own_server_rather_than_uvicorn()
    {
        // The subcommand is part of the module arguments; `-m ember.cli serve`, not `-m uvicorn`.
        var arguments = ServiceCatalog.Ember.ArgumentsFor(8091);
        Assert.Equal(new[] { "-m", "ember.cli", "serve", "--host", "127.0.0.1", "--port", "8091" }, arguments);
    }

    [Fact]
    public void The_services_of_one_node_may_share_a_home()
    {
        // ⛔ THIS IS WHAT A NODE IS, and refusing it made the arrangement every real node already
        // has inexpressible. `service_common.sh:svc_env` derives KEYS_DIR, ORIGIN_DB_PATH and both
        // index directories from ONE AGIENCE_HOME for every service on a node instance — the
        // Origin and the Mantle of one node are meant to hold the same keys.
        var config = new AgienceConfig();
        var home = Path.Combine(Path.GetTempPath(), "agience-node-" + Guid.NewGuid().ToString("N"));

        foreach (var kind in ServiceCatalog.All)
        {
            var instance = config.Add(kind, "home", "home.agience.ai");
            instance.DataDirectory = home;
        }

        Assert.DoesNotContain(config.Problems(), p => p.Contains("share the data directory"));
    }

    [Fact]
    public void Two_of_one_kind_still_may_not_share_a_home()
    {
        var config = new AgienceConfig();
        var home = Path.Combine(Path.GetTempPath(), "agience-node-" + Guid.NewGuid().ToString("N"));
        var a = config.Add(ServiceCatalog.Mantle, "a", "home.agience.ai");
        var b = config.Add(ServiceCatalog.Mantle, "b", "home.agience.ai");
        a.DataDirectory = home;
        b.DataDirectory = home;

        Assert.Contains(config.Problems(), p => p.Contains("share the data directory"));
    }

    [Fact]
    public void Two_mantles_pointed_at_one_lattice_is_refused_even_with_separate_homes()
    {
        // The hole the override opens: distinct homes pass every directory check while both
        // services write one store. "Both come up healthy while answering searches from each
        // other's postings."
        var config = new AgienceConfig();
        var shard = Path.Combine(Path.GetTempPath(), "agience-shard-" + Guid.NewGuid().ToString("N"));
        var a = config.Add(ServiceCatalog.Mantle, "a", "home.agience.ai");
        var b = config.Add(ServiceCatalog.Mantle, "b", "home.agience.ai");
        a.ShardDirectory = shard;
        b.ShardDirectory = shard;

        Assert.Contains(config.Problems(), p => p.Contains("share the shard directory"));
    }

    [Fact]
    public void An_overridden_shard_is_where_the_store_is_read_from()
    {
        // The adoption case: keys and the account database in the home this node already had, the
        // lattice on the volume it already lives on. Derived instead, `<home>/shard` is an empty
        // path — so Mantle mints a new store, reports healthy, and serves an empty universe.
        var config = new AgienceConfig();
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");
        mantle.DataDirectory = @"C:\home";
        mantle.ShardDirectory = @"D:\elsewhere\shard";

        var env = ServiceEnvironment.For(config, mantle);

        Assert.Equal("D:/elsewhere/shard", env["EMBER_SQLITE_DIR"]);
        Assert.Equal("D:/elsewhere/shard/lattice.db", env["MANTLE_LATTICE_PATH"]);

        // Everything else still comes from the home.
        Assert.Equal("C:/home/keys", env["KEYS_DIR"]);
        Assert.Equal("C:/home/origin/origin.db", env["ORIGIN_DB_PATH"]);
    }

    [Fact]
    public void Without_an_override_the_shard_is_under_the_home()
    {
        var config = new AgienceConfig();
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");
        mantle.DataDirectory = @"C:\home";

        var env = ServiceEnvironment.For(config, mantle);
        Assert.Equal("C:/home/shard", env["EMBER_SQLITE_DIR"]);
        Assert.Equal("C:/home/shard/lattice.db", env["MANTLE_LATTICE_PATH"]);
    }

    [Fact]
    public void A_checkout_missing_a_repo_the_plan_installs_from_is_not_complete()
    {
        // The failure this guards: the completeness test naming fewer repositories than the
        // install plan. Setup then builds a whole virtualenv, runs four steps, and fails on the
        // fifth with a pip error naming a path — having reported the checkout usable first.
        var root = Path.Combine(Path.GetTempPath(), "agience-checkout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "agience-origin"));
            File.WriteAllText(Path.Combine(root, "agience-origin", "requirements.txt"), "");
            File.WriteAllText(Path.Combine(root, "agience-origin", "pyproject.toml"), "");
            Directory.CreateDirectory(Path.Combine(root, "agience-prism", "py"));
            File.WriteAllText(Path.Combine(root, "agience-prism", "py", "pyproject.toml"), "");
            Directory.CreateDirectory(Path.Combine(root, "agience-mantle"));
            File.WriteAllText(Path.Combine(root, "agience-mantle", "pyproject.toml"), "");

            // Exactly the old three. Everything the plan grew later is absent.
            Assert.False(PythonEnvironment.IsCompleteCheckout(root));

            foreach (var repo in new[] { "agience-ember", "agience-crystal", "agience-chorus" })
            {
                Directory.CreateDirectory(Path.Combine(root, repo));
                File.WriteAllText(Path.Combine(root, repo, "pyproject.toml"), "");
            }

            Assert.True(PythonEnvironment.IsCompleteCheckout(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_persona_host_is_launched_by_its_qualified_module()
    {
        // `server:app` resolves to nothing — the module lives inside the package, and the bare
        // name meant this service could not start at all.
        Assert.Contains("agience_chorus.server:app", ServiceCatalog.Chorus.ArgumentsFor(8087));

        // No `--app-dir`: this application installs the distribution, so there is no checkout path
        // to put on the import path, and carrying the fleet's flag across would hard-code one.
        Assert.DoesNotContain("--app-dir", ServiceCatalog.Chorus.ArgumentsFor(8087));
    }
}
