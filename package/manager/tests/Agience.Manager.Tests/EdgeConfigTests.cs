using System;
using System.Linq;
using Agience.Core;
using Xunit;

namespace Agience.Manager.Tests;

/// <summary>
/// The generated Caddyfile must front the services this node runs, on loopback, and nothing else.
/// </summary>
/// <remarks>
/// ⛔ THIS FILE EXISTS BECAUSE THE EDGE PUBLISHED THE WHOLE NODE ONCE. Measured 2026-09-16: a
/// hand-written Caddyfile with no <c>bind</c> listened on <c>::</c> — every interface — while all
/// five Python services were correctly on 127.0.0.1. Adding TLS had exposed them to any network the
/// laptop joined, and nothing failed. <c>ServiceCatalogTests.No_kind_binds_a_public_interface</c>
/// holds that rule for kinds whose address is a command-line argument; a native kind declares its
/// address here instead, so this is the only place the same rule can be enforced.
/// </remarks>
public class EdgeConfigTests
{
    private static AgienceConfig NodeWith(params (string kind, int port)[] services)
    {
        var config = new AgienceConfig();
        foreach (var (kind, port) in services)
        {
            config.Instances.Add(new ServiceInstance
            {
                Id = kind + "-home",
                Kind = kind,
                Name = "home",
                Domain = "home.agience.ai",
                Port = port,
                Enabled = true,
            });
        }

        return config;
    }

    [Fact]
    public void Every_site_binds_loopback()
    {
        var text = EdgeConfig.Render(NodeWith(
            ("origin", 8080), ("mantle", 8082), ("crystal", 8085), ("chorus", 8087)));

        Assert.NotNull(text);

        var opens = text!.Split('\n').Count(l => l.TrimEnd().EndsWith(" {", StringComparison.Ordinal)
                                                 && !l.StartsWith("\t", StringComparison.Ordinal));
        var binds = text.Split('\n').Count(l => l.Trim() == "bind 127.0.0.1");

        // Every site block, and the count compared rather than "contains", because one unbound site
        // among five is exactly the case that would ship.
        Assert.True(opens > 0, "no site blocks were rendered — this check would pass on an empty file");
        Assert.Equal(opens, binds);
        Assert.DoesNotContain("0.0.0.0", text);
    }

    /// <summary>
    /// The rendered file is what <c>caddy fmt</c> would produce, so starting it logs no warning.
    /// </summary>
    /// <remarks>
    /// A generated file that Caddy calls unformatted prints "Caddyfile input is not formatted" on
    /// every start. Nobody can act on it — the file is overwritten — so the only thing it teaches
    /// is that warnings from this service are noise. Measured 2026-09-16: a blank line before the
    /// global block was enough to trigger it.
    /// </remarks>
    [Fact]
    public void The_rendered_file_is_canonically_formatted()
    {
        var text = EdgeConfig.Render(NodeWith(("origin", 8080), ("chorus", 8087)));

        Assert.NotNull(text);
        Assert.DoesNotContain("\n\n{", text!.Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_edge_never_fronts_itself()
    {
        var text = EdgeConfig.Render(NodeWith(("origin", 8080), ("chorus", 8087), ("caddy", 443)));

        Assert.NotNull(text);
        Assert.DoesNotContain("127.0.0.1:443", text);
        Assert.DoesNotContain("caddy.home.agience.ai", text);
    }

    [Fact]
    public void Each_fronted_service_is_proxied_to_its_own_port()
    {
        var text = EdgeConfig.Render(NodeWith(
            ("origin", 8080), ("mantle", 8082), ("ember", 8091)));

        Assert.NotNull(text);
        Assert.Contains("origin.home.agience.ai {", text);
        Assert.Contains("127.0.0.1:8080", text);
        Assert.Contains("mantle.home.agience.ai {", text);
        Assert.Contains("127.0.0.1:8082", text);
        Assert.Contains("ember.home.agience.ai {", text);
        Assert.Contains("127.0.0.1:8091", text);
    }

    /// <summary>
    /// The apex and the wildcard go to the host router, not to the gateway.
    /// </summary>
    /// <remarks>
    /// ⛔ 8085 LOOKS LIKE THE ROUTER AND IS NOT. Measured 2026-09-16: <c>crystal.main:app</c> serves
    /// six API paths and answers <c>{"detail":"Not Found"}</c> for <c>/</c>. The service that
    /// answers a host-routed request is chorus, which runs <c>crystal.host.build_app</c> and
    /// self-identifies as <c>"service":"crystal-host"</c>. Sending the apex to crystal would 404
    /// every persona name while looking correct in the config.
    /// </remarks>
    [Fact]
    public void The_apex_and_the_wildcard_reach_the_host_router()
    {
        var text = EdgeConfig.Render(NodeWith(("crystal", 8085), ("chorus", 8087)));

        Assert.NotNull(text);
        Assert.Contains("\nhome.agience.ai {", text);
        Assert.Contains("\n*.home.agience.ai {", text);

        // Both catch-all blocks must name chorus's port, not crystal's.
        var apex = text!.Substring(text.IndexOf("\nhome.agience.ai {", StringComparison.Ordinal));
        Assert.Contains("127.0.0.1:8087", apex);
    }

    [Fact]
    public void Nothing_to_front_renders_no_file()
    {
        // An empty Caddyfile is valid and serves nothing, so it would start an edge that refuses
        // every connection and reads like a crashed service. Null says "do not write one".
        Assert.Null(EdgeConfig.Render(new AgienceConfig()));
        Assert.Null(EdgeConfig.Render(NodeWith(("caddy", 443))));
    }

    [Fact]
    public void A_service_without_a_domain_is_not_given_a_name()
    {
        var config = NodeWith(("origin", 8080), ("chorus", 8087));
        config.Instances[0].Domain = "";

        var text = EdgeConfig.Render(config);

        Assert.NotNull(text);
        Assert.DoesNotContain("origin.", text);
        Assert.DoesNotContain("127.0.0.1:8080", text);
    }
}
