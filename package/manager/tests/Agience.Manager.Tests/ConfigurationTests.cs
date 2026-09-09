using Agience.Core;
using Xunit;

namespace Agience.Manager.Tests;

/// <summary>
/// The instance model: many Origins, many Mantles, and who trusts whom.
/// </summary>
/// <remarks>
/// The failures these prevent are not crashes. A Mantle pointed at the wrong issuer comes up
/// healthy, answers every probe, and rejects every request with "Invalid token audience" — naming
/// the caller when the fault is configuration. Two Mantles sharing a directory come up healthy and
/// answer each other's searches. Neither logs anything.
/// </remarks>
public class ConfigurationTests
{
    private static AgienceConfig WithOrigin(string name, string domain = "")
    {
        var config = new AgienceConfig();
        config.Add(ServiceCatalog.Origin, name, domain);
        return config;
    }

    [Fact]
    public void A_fresh_machine_runs_nothing_and_that_is_valid()
    {
        // No default instance is invented. Creating an Origin nobody asked for would mint signing
        // keys, on a domain nobody chose, behind their back.
        var config = new AgienceConfig();

        Assert.Empty(config.Instances);
        Assert.Empty(config.Problems());
    }

    [Fact]
    public void Two_origins_on_two_domains_is_an_ordinary_arrangement()
    {
        var config = new AgienceConfig();
        var home = config.Add(ServiceCatalog.Origin, "home", "home.agience.ai");
        var lab = config.Add(ServiceCatalog.Origin, "lab", "lab.agience.ai");

        Assert.Empty(config.Problems());
        Assert.Equal("https://origin.home.agience.ai", home.PublicUri);
        Assert.Equal("https://origin.lab.agience.ai", lab.PublicUri);
        Assert.NotEqual(home.Port, lab.Port);
        Assert.NotEqual(config.DataDirectoryOf(home), config.DataDirectoryOf(lab));
    }

    [Fact]
    public void A_mantle_with_no_origin_here_must_be_told_its_authority()
    {
        // The "joining somebody else's plane" case, and the one where guessing would be worst: a
        // store that verifies against nothing accepts nothing.
        var config = new AgienceConfig();
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");

        Assert.Null(config.AuthorityIssuerFor(mantle));
        Assert.Contains(config.Problems(), p => p.Contains("authority", StringComparison.OrdinalIgnoreCase));

        mantle.Authority = "https://origin.agience.ai";
        Assert.Equal("https://origin.agience.ai", config.AuthorityIssuerFor(mantle));
        Assert.Empty(config.Problems());
    }

    [Fact]
    public void A_lone_origin_is_taken_as_the_authority_without_being_chosen()
    {
        var config = WithOrigin("home", "home.agience.ai");
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");

        Assert.Equal("https://origin.home.agience.ai", config.AuthorityIssuerFor(mantle));
        Assert.Empty(config.Problems());
    }

    [Fact]
    public void An_origin_on_the_same_domain_wins_over_one_on_another()
    {
        var config = new AgienceConfig();
        config.Add(ServiceCatalog.Origin, "lab", "lab.agience.ai");
        config.Add(ServiceCatalog.Origin, "home", "home.agience.ai");
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");

        Assert.Equal("https://origin.home.agience.ai", config.AuthorityIssuerFor(mantle));
    }

    [Fact]
    public void Two_candidate_origins_is_an_ambiguity_and_is_not_guessed()
    {
        // Picking one would silently bind this store's whole trust to whichever happened to sort
        // first — a decision nobody made, visible nowhere, and wrong half the time.
        var config = new AgienceConfig();
        config.Add(ServiceCatalog.Origin, "lab", "lab.agience.ai");
        config.Add(ServiceCatalog.Origin, "work", "work.agience.ai");
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");

        Assert.Null(config.AuthorityIssuerFor(mantle));
        Assert.NotEmpty(config.Problems());
    }

    [Fact]
    public void The_issuer_is_public_and_the_address_is_loopback()
    {
        // Collapsing these breaks one case or the other. A Mantle told to expect
        // http://127.0.0.1:8080 rejects a perfectly good token stamped
        // https://origin.home.agience.ai; a Mantle told to reach the public name may not resolve
        // it, or may resolve it somewhere else entirely.
        var config = WithOrigin("home", "home.agience.ai");
        var origin = config.Instances[0];
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");

        Assert.Equal("https://origin.home.agience.ai", config.AuthorityIssuerFor(mantle));
        Assert.Equal($"http://127.0.0.1:{origin.Port}", config.AuthorityAddressFor(mantle));
    }

    [Fact]
    public void An_origin_is_its_own_authority_until_it_is_told_to_join()
    {
        var config = WithOrigin("home", "home.agience.ai");
        var origin = config.Instances[0];

        Assert.Equal(origin.PublicUri, config.AuthorityIssuerFor(origin));

        origin.Authority = "https://origin.agience.ai";
        Assert.Equal("https://origin.agience.ai", config.AuthorityIssuerFor(origin));

        // Its own address does not move. Joining links identities; it does not relocate this
        // node.
        Assert.Equal("https://origin.home.agience.ai", origin.PublicUri);
    }

    [Fact]
    public void A_public_uri_carries_no_port_and_a_loopback_one_does()
    {
        // Under a domain something in front terminates TLS on 443; writing the loopback port into
        // the public URI would publish an address no browser can reach.
        var config = WithOrigin("local");
        var local = config.Instances[0];
        Assert.Contains($":{local.Port}", local.PublicUri);

        local.Domain = "home.agience.ai";
        Assert.DoesNotContain($":{local.Port}", local.PublicUri);
    }

    [Fact]
    public void A_trailing_slash_never_reaches_a_token()
    {
        // `iss` is compared as a string. "https://x/" and "https://x" are different issuers to
        // every verifier, and the difference is invisible in a text box.
        var config = WithOrigin("home", "home.agience.ai");
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");
        mantle.Authority = "https://origin.example.test/";

        Assert.Equal("https://origin.example.test", config.AuthorityIssuerFor(mantle));
    }

    [Fact]
    public void Every_new_instance_gets_a_port_nothing_else_is_using()
    {
        var config = new AgienceConfig();
        var ports = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            ports.Add(config.Add(ServiceCatalog.Origin, "o" + i).Port);
            ports.Add(config.Add(ServiceCatalog.Mantle, "m" + i).Port);
        }

        Assert.Equal(ports.Count, ports.Distinct().Count());

        // The only complaints are about authority, which is the point: four Origins makes every
        // Mantle's authority genuinely ambiguous, so this fixture proves both that no port and no
        // data directory collided, and that the ambiguity is still reported.
        var problems = config.Problems();
        Assert.All(problems, p => Assert.Contains("authority", p, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void Two_instances_on_one_port_is_refused()
    {
        // Not a crash: the first binds, the second exits, and the second is reported as failed
        // forever over a setting nobody remembers typing.
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Origin, "a");
        var b = config.Add(ServiceCatalog.Origin, "b");
        b.Port = a.Port;

        Assert.Contains(config.Problems(), p => p.Contains($"Port {a.Port}"));
    }

    [Fact]
    public void Two_instances_sharing_a_data_directory_is_refused()
    {
        // The silent one: two Mantles on one directory share one lattice and one SSE index.
        // Mantle's own .env.example names the result: "Both come up healthy while answering
        // searches from each other's postings."
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Mantle, "a");
        var b = config.Add(ServiceCatalog.Mantle, "b");
        a.Authority = "https://origin.example.test";
        b.Authority = "https://origin.example.test";
        b.DataDirectory = config.DataDirectoryOf(a);

        Assert.Contains(config.Problems(), p => p.Contains("share the data directory"));
    }

    [Fact]
    public void Each_instance_gets_its_own_data_directory_by_default()
    {
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Mantle, "home");
        var b = config.Add(ServiceCatalog.Mantle, "lab");

        Assert.NotEqual(config.DataDirectoryOf(a), config.DataDirectoryOf(b));
        Assert.Contains(a.Id, config.DataDirectoryOf(a));
    }

    [Fact]
    public void Ids_stay_unique_even_when_names_repeat()
    {
        // The id becomes a directory holding keys. Two instances resolving to one id would mean two
        // services opening one store while both reported success.
        var config = new AgienceConfig();
        var a = config.Add(ServiceCatalog.Origin, "home");
        var b = config.Add(ServiceCatalog.Origin, "home");

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Every_origin_starts_before_any_mantle()
    {
        // A dependency order across the whole machine, not within a domain. Started interleaved,
        // a Mantle reaches an authority not yet publishing keys and reports a trust failure — which
        // sends a person to look at trust configuration over what is actually a race.
        var config = new AgienceConfig();
        config.Add(ServiceCatalog.Mantle, "m1", "a.test");
        config.Add(ServiceCatalog.Origin, "o1", "a.test");
        config.Add(ServiceCatalog.Mantle, "m2", "b.test");
        config.Add(ServiceCatalog.Origin, "o2", "b.test");

        var kinds = config.InStartOrder.Select(i => i.Kind).ToList();
        var lastOrigin = kinds.LastIndexOf("origin");
        var firstMantle = kinds.IndexOf("mantle");

        Assert.True(lastOrigin < firstMantle,
            "every origin must start before any mantle; got " + string.Join(", ", kinds));
    }

    [Fact]
    public void A_settings_file_survives_a_round_trip()
    {
        var config = new AgienceConfig();
        var origin = config.Add(ServiceCatalog.Origin, "home", "home.agience.ai");
        var mantle = config.Add(ServiceCatalog.Mantle, "home", "home.agience.ai");
        mantle.Authority = origin.Id;

        var copy = config.Clone();

        Assert.Equal(2, copy.Instances.Count);
        Assert.Equal(origin.Id, copy.Instances[0].Id);
        Assert.Equal(origin.Port, copy.Instances[0].Port);
        Assert.Equal("https://origin.home.agience.ai", copy.AuthorityIssuerFor(copy.Instances[1]));
    }

    [Fact]
    public void An_unknown_kind_is_refused_rather_than_run()
    {
        // A later build can write a kind this one has never heard of. Trying to launch it would
        // hand python a module name from a settings file.
        var config = new AgienceConfig();
        config.Instances.Add(new ServiceInstance { Id = "x", Kind = "lumen", Name = "x", Port = 9000 });

        Assert.NotEmpty(config.Problems());
    }

    [Fact]
    public void A_domain_that_is_not_a_domain_is_refused()
    {
        var config = WithOrigin("home", "not a domain");
        Assert.NotEmpty(config.Problems());
    }
}
