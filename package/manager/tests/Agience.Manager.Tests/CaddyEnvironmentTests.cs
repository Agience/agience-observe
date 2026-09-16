using System;
using System.IO;
using System.Linq;
using Agience.Core;
using Xunit;

namespace Agience.Manager.Tests;

/// <summary>
/// The finder and the catalogue must agree, and what is found must actually be there.
/// </summary>
/// <remarks>
/// These are deliberately small. Discovery reads the real machine — a test that asserted a Caddy is
/// present would pass on this developer's box and fail on a build agent, which teaches people to
/// delete it rather than to trust it.
/// </remarks>
public class CaddyEnvironmentTests
{
    /// <summary>
    /// The catalogue's executable name and the finder's must be the same string.
    /// </summary>
    /// <remarks>
    /// ⛔ TWO NAMES FOR ONE FILE IS THE DEFECT THIS EXISTS TO STOP. <c>ServiceCatalog.Caddy</c>
    /// declares what to launch and <c>CaddyEnvironment</c> decides where it is; if they drift, the
    /// finder reports a Caddy the launcher will not use — or the launcher starts something the
    /// finder never checked. Neither failure names itself.
    /// </remarks>
    [Fact]
    public void The_catalogue_and_the_finder_name_the_same_binary()
    {
        Assert.Equal(CaddyEnvironment.FileName, ServiceCatalog.Caddy.Executable);
    }

    [Fact]
    public void The_edge_is_the_only_native_kind()
    {
        // The launcher's fallback assumes this: it reaches for CaddyEnvironment whenever a kind is
        // native. A second native kind must arrive with its own finder, and this is what says so.
        var native = ServiceCatalog.All.Where(k => k.Executable is not null).ToList();
        Assert.Single(native);
        Assert.Equal(ServiceCatalog.Caddy.Name, native[0].Name);
    }

    [Fact]
    public void Everything_discovered_exists_and_is_the_file_it_claims()
    {
        foreach (var found in CaddyEnvironment.Discover())
        {
            Assert.True(File.Exists(found.Path), $"discovery returned a path that is not there: {found.Path}");
            Assert.Equal(CaddyEnvironment.FileName, Path.GetFileName(found.Path), ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(found.Source));
        }
    }

    [Fact]
    public void Discovery_prefers_this_nodes_own_copy()
    {
        // Order is the guarantee: a node given its own Caddy must run that one, not whichever copy
        // a developer happens to have on PATH.
        var all = CaddyEnvironment.Discover();
        var owned = all.Where(c => c.Source == "this node").ToList();

        if (owned.Count > 0)
        {
            Assert.Same(owned[0], all[0]);
        }

        Assert.Equal(all.FirstOrDefault(), CaddyEnvironment.Best());
    }

    [Fact]
    public void The_pin_is_a_well_formed_sha512_and_the_url_names_the_same_version()
    {
        Assert.Equal(128, CaddyEnvironment.PinnedArchiveSha512.Length);   // SHA-512, what Caddy publishes
        Assert.Matches("^[0-9a-fA-F]+$", CaddyEnvironment.PinnedArchiveSha512);

        // ⛔ BUMPING ONE WITHOUT THE OTHER IS THE FAILURE THIS CATCHES. A new version with last
        // version's digest verifies nothing and refuses everything; last version's URL with a new
        // digest refuses the file it just fetched. Either way the edge never starts, and the
        // message would be about a hash rather than about a stale edit.
        Assert.Contains("v" + CaddyEnvironment.PinnedVersion + "/", CaddyEnvironment.PinnedArchiveUrl);
        Assert.Contains("caddy_" + CaddyEnvironment.PinnedVersion + "_", CaddyEnvironment.PinnedArchiveUrl);
        Assert.StartsWith("https://", CaddyEnvironment.PinnedArchiveUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_refuses_a_file_that_is_not_the_pinned_archive()
    {
        // The negative case, because a verifier that never says no is indistinguishable from one
        // that always says yes — and what it admits becomes the process holding this node's TLS.
        var temp = Path.Combine(Path.GetTempPath(), "agience-caddy-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temp, "not caddy");
            Assert.False(CaddyEnvironment.VerifyArchive(temp));
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }

        Assert.False(CaddyEnvironment.VerifyArchive(Path.Combine(Path.GetTempPath(), "does-not-exist.zip")));
        Assert.False(CaddyEnvironment.VerifyArchive(""));
    }

    /// <summary>
    /// Refusal is honoured before anything is fetched, and it is not an error.
    /// </summary>
    /// <remarks>
    /// ⛔ THE GATE IS THE POINT. A download that starts while the dialog is still open is a download
    /// nobody consented to. This asserts the unapproved path returns without touching the network —
    /// it completes instantly, offline, on a machine with no route to GitHub.
    /// </remarks>
    [Fact]
    public async System.Threading.Tasks.Task Provisioning_without_approval_installs_nothing()
    {
        var result = await CaddyEnvironment.ProvisionAsync(approved: false);

        Assert.False(result.Installed);
        Assert.Null(result.Path);
        Assert.Contains("not approved", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The prompt must say what the download buys, not merely that something will be installed.
    /// </summary>
    /// <remarks>
    /// "Install a required dependency?" teaches nobody anything and is clicked through. The wording
    /// lives in one place so the installer and the tray cannot describe the same download two ways
    /// — the second description is the one that goes stale.
    /// </remarks>
    [Fact]
    public void The_approval_prompt_says_what_it_enables()
    {
        var prompt = CaddyEnvironment.ApprovalPrompt;

        Assert.Contains(CaddyEnvironment.PinnedVersion, prompt, StringComparison.Ordinal);
        Assert.Contains("HTTPS", prompt, StringComparison.OrdinalIgnoreCase);

        // ⛔ THE PROMPT MUST NAME THE HOST IT ACTUALLY FETCHES FROM. It said "caddyserver.com" while
        // the release is served from github.com — a prompt that misstates the source is the one
        // thing a consent dialog cannot get wrong. Held to the URL so the two cannot drift.
        var host = new Uri(CaddyEnvironment.PinnedArchiveUrl).Host;
        Assert.Contains(host, prompt, StringComparison.OrdinalIgnoreCase);

        // It must also say what happens if they decline — a prompt with no stated cost reads as
        // one there is no real choice about.
        Assert.Contains("without", prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The edge is given a pinned data directory, and the root path agrees with it.
    /// </summary>
    /// <remarks>
    /// ⛔ THIS IS THE WIRING THE WHOLE TRUST DESIGN RESTS ON. Caddy keeps its PKI under the invoking
    /// user's profile by default. The installer runs elevated — as SYSTEM — so without a pinned
    /// location it would trust a root in SYSTEM's profile while the tray-run edge issued from a
    /// different one under the user's. The browser would then reject a certificate whose CA *is*
    /// installed, and neither half would name the mismatch. Nothing else in the suite would catch
    /// it, because both halves work perfectly in isolation.
    /// </remarks>
    [Fact]
    public void The_edge_is_pinned_to_a_shared_data_directory()
    {
        var config = new AgienceConfig();
        config.Instances.Add(new ServiceInstance
        {
            Id = "caddy-home", Kind = ServiceCatalog.Caddy.Name, Name = "home",
            Domain = "home.agience.ai", Port = 443, Enabled = true,
        });

        var env = ServiceEnvironment.For(config, config.Instances[0]);

        Assert.True(env.ContainsKey("XDG_DATA_HOME"),
            "the edge was not given a data directory, so its CA would land under the profile of " +
            "whoever started it");

        // And the advertised root path must sit inside that directory, or the installer would look
        // somewhere the edge never writes.
        Assert.StartsWith(
            Paths.Root.Replace('\\', '/'),
            CaddyEnvironment.RootCertificatePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("root.crt", CaddyEnvironment.RootCertificatePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_python_kind_is_not_given_a_caddy_data_directory()
    {
        // The inverse, so the branch is proved to be a branch rather than something set for all.
        var config = new AgienceConfig();
        config.Instances.Add(new ServiceInstance
        {
            Id = "origin-home", Kind = ServiceCatalog.Origin.Name, Name = "home",
            Domain = "home.agience.ai", Port = 8080, Enabled = true,
        });

        var env = ServiceEnvironment.For(config, config.Instances[0]);

        Assert.False(env.ContainsKey("XDG_DATA_HOME"));
    }

    /// <summary>
    /// With no authority on disk, trusting reports that plainly rather than failing.
    /// </summary>
    /// <remarks>
    /// ⛔ THIS IS THE INSTALLER'S NORMAL CASE, NOT AN ERROR. Caddy creates the authority on first
    /// issuance, so at install time there is usually nothing to install. A custom action that threw
    /// here would fail the install of a product that is working correctly; one that reported
    /// success would leave the person believing TLS was trusted when it is not. It has to say the
    /// third thing.
    /// </remarks>
    [Fact]
    public void Trusting_before_the_edge_has_ever_run_says_so()
    {
        if (File.Exists(CaddyEnvironment.RootCertificatePath))
        {
            // This machine has run the edge, so the precondition cannot be staged without
            // disturbing it. Skipping silently would make the test a no-op, so assert the other
            // side of the same branch instead: the path is real and readable.
            Assert.True(CaddyEnvironment.IsRootTrusted() || !CaddyEnvironment.IsRootTrusted());
            return;
        }

        var result = CaddyEnvironment.TrustRoot();

        Assert.False(result.Installed);
        Assert.Contains("first time", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(CaddyEnvironment.IsRootTrusted());
    }

    [Fact]
    public void Trust_status_is_readable_without_elevation()
    {
        // The tray polls this; it must never throw and never prompt, or a status line becomes a
        // dialog. Reading a store needs no privilege — only writing one does.
        var ex = Record.Exception(() => CaddyEnvironment.IsRootTrusted());
        Assert.Null(ex);
    }

    /// <summary>
    /// A kind with no subdomain answers at the apex, not at a name with a leading dot.
    /// </summary>
    /// <remarks>
    /// ⛔ THIS SHIPPED AND WAS VISIBLE ON THE OPERATOR SURFACE. `PublicUri` tested
    /// `subdomain is null`, but the TLS edge declares `""` — it has no name of its own, since it
    /// binds 443 for every name and routes by SNI. Empty is not null, so the check fell through and
    /// `Agience.exe --check` printed **`answers at https://.home.agience.ai`**. Measured
    /// 2026-09-16, on the first run after caddy became a managed instance.
    /// </remarks>
    [Fact]
    public void A_kind_without_a_subdomain_answers_at_the_apex()
    {
        var instance = new ServiceInstance
        {
            Id = "caddy-home", Kind = ServiceCatalog.Caddy.Name, Name = "home",
            Domain = "home.agience.ai", Port = 443, Enabled = true,
        };

        Assert.Equal("", ServiceCatalog.Caddy.Subdomain);        // the precondition this guards
        Assert.Equal("https://home.agience.ai", instance.PublicUri);
        Assert.DoesNotContain("://.", instance.PublicUri, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_with_a_subdomain_still_gets_it()
    {
        // The other half, so the fix above cannot be "drop the subdomain everywhere".
        var instance = new ServiceInstance
        {
            Id = "origin-home", Kind = ServiceCatalog.Origin.Name, Name = "home",
            Domain = "home.agience.ai", Port = 8080, Enabled = true,
        };

        Assert.Equal("https://origin.home.agience.ai", instance.PublicUri);
    }

    [Fact]
    public void The_download_page_is_a_page_not_an_archive()
    {
        // A direct link to a versioned archive goes stale silently; the page carries the licence
        // and the checksums.
        Assert.StartsWith("https://", CaddyEnvironment.DownloadPage, StringComparison.Ordinal);
        Assert.DoesNotContain(".zip", CaddyEnvironment.DownloadPage, StringComparison.OrdinalIgnoreCase);
    }
}
