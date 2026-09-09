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
}
