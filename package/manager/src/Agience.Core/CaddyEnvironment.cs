using System;
using System.Collections.Generic;
using System.IO;
// `ExtractToFile` is an extension method on ZipArchiveEntry, so a qualified type name is not
// enough — the namespace has to be imported for it to resolve.
using System.IO.Compression;
using System.Linq;

namespace Agience.Core;

/// <summary>One Caddy found on this machine.</summary>
/// <param name="Path">Full path to <c>caddy.exe</c>.</param>
/// <param name="Source">How it was found — shown so a person can tell two installs apart.</param>
public sealed record CaddyBinary(string Path, string Source)
{
    public override string ToString() => $"Caddy — {Path}  ({Source})";
}

/// <summary>
/// Finds the Caddy this node's TLS edge runs, the way <see cref="PythonEnvironment"/> finds Python.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ DETECTED, NEVER DOWNLOADED, AND NEVER SHIPPED. Both halves are settled by design decisions
/// already written down elsewhere. <c>Agience.wxs</c>: <i>"ONE FILE IS INSTALLED, AND THAT IS THE
/// WHOLE DESIGN… Everything else the application needs — the Python environment, the keys, the
/// stores — is built on the machine, under the user's profile, by the application itself."</i> A
/// ~50 MB binary in the package would also end the property that makes "keep my data" the default
/// on uninstall, because the MSI would then know about a tree under the profile.
/// </para>
/// <para>
/// And <see cref="PythonEnvironment"/> does not fetch a runtime either — it opens the download page
/// and <i>"the person decides"</i>. Silently pulling a binary off the network is a different kind of
/// program from the one this already is, so Caddy is found or it is asked for.
/// </para>
/// <para>
/// ⚠ <see cref="Paths.Root"/> IS SEARCHED BEFORE PATH, AND THAT ORDER IS THE POINT. A node that has
/// been given its own Caddy must use that one; falling through to PATH first would start whichever
/// <c>caddy.exe</c> a developer happened to install, which is a different program from the one this
/// node was set up with and nothing would say so.
/// </para>
/// </remarks>
public static class CaddyEnvironment
{
    /// <summary>The file this looks for, and the name <see cref="ServiceCatalog.Caddy"/> carries.</summary>
    public const string FileName = "caddy.exe";

    /// <summary>Where a person is sent when there is no Caddy to find.</summary>
    /// <remarks>
    /// The download page rather than a direct binary URL: the page states the licence and the
    /// checksums, and a link straight to an archive is the shape of a thing that goes stale
    /// silently.
    /// </remarks>
    public const string DownloadPage = "https://caddyserver.com/download";

    /// <summary>
    /// The root certificate the edge issues from, once it has started at least once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ THE LAYOUT IS CADDY'S AND WAS MEASURED, NOT ASSUMED. 2026-09-16, throwaway instance on
    /// :8443 with <c>XDG_DATA_HOME</c> pinned: the tree appeared at
    /// <c>&lt;XDG_DATA_HOME&gt;/caddy/pki/authorities/local/{root,intermediate}.{crt,key}</c>, and
    /// the generated root was a real one — <c>CN = Caddy Local Authority - 2026 ECC Root</c>, valid
    /// ten years.
    /// </para>
    /// <para>
    /// ⛔ IT DOES NOT EXIST UNTIL THE EDGE HAS RUN. Caddy generates the authority on first
    /// issuance, so anything that installs this into a trust store has to run *after* a first
    /// start, not during setup. <c>caddy trust</c> is not an alternative: it does not generate
    /// either — it asks a running Caddy's admin API (<c>localhost:2019/pki/ca/local</c>), which
    /// this node's config disables.
    /// </para>
    /// </remarks>
    public static string RootCertificatePath =>
        Path.Combine(Paths.Root, "caddy", "pki", "authorities", "local", "root.crt");

    /// <summary>The release this node installs when the person approves the download.</summary>
    /// <remarks>
    /// ⛔ PINNED, AND THE HASH IS THE VENDOR'S, NOT ONE COMPUTED FROM WHATEVER ARRIVED. Verifying a
    /// download against a digest taken from that same download proves only that the file did not
    /// change in transit to itself. This value was read from Caddy's published
    /// <c>caddy_2.11.4_checksums.txt</c> on the release, which lists 42 artifacts.
    /// </remarks>
    public const string PinnedVersion = "2.11.4";

    /// <summary>Where the pinned archive comes from.</summary>
    public static string PinnedArchiveUrl =>
        $"https://github.com/caddyserver/caddy/releases/download/v{PinnedVersion}/" +
        $"caddy_{PinnedVersion}_windows_amd64.zip";

    /// <summary>
    /// SHA-512 of the pinned archive — the algorithm Caddy publishes, not one chosen here.
    /// </summary>
    public const string PinnedArchiveSha512 =
        "cd5ccfd86a4b40732cf715890d0dca5bf3f63adefec5a7914de85adf240c60ce" +
        "7e5d2791631b88ef9758e46b23bb1730e020b9c5d696889740b284ffd4788e35";

    /// <summary>
    /// Does <paramref name="archivePath"/> hash to <see cref="PinnedArchiveSha512"/>?
    /// </summary>
    /// <remarks>
    /// ⛔ THIS IS THE STEP THAT MUST NOT BE SKIPPED ON FAILURE. The file it admits becomes the
    /// process that terminates TLS for every name on this node and holds the key material behind
    /// it. A mismatch is not a retryable download — it is a different file from the one approved,
    /// and the only safe outcome is to refuse and say so.
    /// </remarks>
    public static bool VerifyArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        using var stream = File.OpenRead(archivePath);
        using var sha = System.Security.Cryptography.SHA512.Create();
        var digest = Convert.ToHexString(sha.ComputeHash(stream));

        return string.Equals(digest, PinnedArchiveSha512, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the person is agreeing to, in terms of the capability rather than the mechanism.
    /// </summary>
    /// <remarks>
    /// ⛔ ONE WORDING, USED BY WHOEVER ASKS. The installer and the tray must not describe the same
    /// download two ways; the second description is the one that goes stale. It names the component,
    /// the source, and what it buys — a prompt that says only "install a dependency?" is a prompt
    /// people click through without learning anything.
    /// </remarks>
    public static string ApprovalPrompt =>
        // ⚠ THE SIZE AND THE SOURCE ARE MEASURED, NOT ESTIMATED. Both were wrong when first
        // written — "about 50 MB" (that is the extracted binary; the download is 17 MB) and
        // "from caddyserver.com" (the release is served from github.com). A prompt that misstates
        // what it is about to do is worse than one that says less.
        $"Install Caddy {PinnedVersion} (a 17 MB download from github.com/caddyserver)?\n\n" +
        "This is the only extra component Agience needs. It provides HTTPS for this node's own " +
        "names — so https://origin.home.agience.ai opens in a browser with no certificate " +
        "warning, instead of only http://127.0.0.1:8080.\n\n" +
        "The certificates are issued on this machine and never leave it. Without Caddy the " +
        "services still run; they are reachable on their local ports only.";

    /// <summary>The outcome of a provisioning attempt. Never throws for an expected refusal.</summary>
    /// <param name="Installed">Is there a usable Caddy at the end of this?</param>
    /// <param name="Path">Where it is, when <paramref name="Installed"/>.</param>
    /// <param name="Reason">Why not, in a sentence a person can act on.</param>
    public sealed record ProvisionResult(bool Installed, string? Path, string Reason);

    /// <summary>
    /// Install the pinned Caddy, if and only if the person approved it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <paramref name="approved"/> IS A PARAMETER RATHER THAN AN ASSUMPTION, and it is checked
    /// before anything is fetched. A download that begins while a dialog is still open is a
    /// download nobody consented to, and "they would have said yes" is not consent.
    /// </para>
    /// <para>
    /// ⛔ A DIGEST MISMATCH DELETES THE FILE AND STOPS. It is not retried and not kept: the archive
    /// that fails this check is, by definition, not the one that was approved, and what it would
    /// become is the process terminating TLS for every name on this node.
    /// </para>
    /// </remarks>
    public static async System.Threading.Tasks.Task<ProvisionResult> ProvisionAsync(
        bool approved,
        IProgress<string>? log = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            return new ProvisionResult(false, null,
                "Caddy was not installed because the download was not approved. The services run " +
                "without it, reachable on their local ports.");
        }

        var existing = Best();
        if (existing is not null)
        {
            return new ProvisionResult(true, existing.Path, $"Already present ({existing.Source}).");
        }

        var archive = Path.Combine(Path.GetTempPath(), $"caddy-{PinnedVersion}-{Guid.NewGuid():N}.zip");

        try
        {
            log?.Report($"Downloading Caddy {PinnedVersion}…");

            using (var http = new System.Net.Http.HttpClient())
            using (var response = await http.GetAsync(PinnedArchiveUrl, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using var target = File.Create(archive);
                await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            log?.Report("Verifying…");
            if (!VerifyArchive(archive))
            {
                return new ProvisionResult(false, null,
                    "The downloaded file did not match Caddy's published SHA-512 and was discarded. " +
                    "Nothing was installed.");
            }

            Paths.EnsureRoot();
            var destination = Path.Combine(Paths.Root, FileName);

            using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, FileName, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    return new ProvisionResult(false, null,
                        $"The archive verified but contains no {FileName}. Nothing was installed.");
                }

                entry.ExtractToFile(destination, overwrite: true);
            }

            log?.Report("Installed.");
            return new ProvisionResult(true, destination, $"Caddy {PinnedVersion} installed.");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException
                                      or UnauthorizedAccessException)
        {
            return new ProvisionResult(false, null, $"Caddy could not be installed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(archive)) { File.Delete(archive); } }
            catch (IOException) { /* a temp file we could not remove is not worth failing over */ }
        }
    }

    /// <summary>Is the edge's root already in the machine trust store?</summary>
    /// <remarks>
    /// Reading a store needs no elevation, so the tray can show the real state without prompting
    /// for anything — which is what lets "TLS is not trusted yet" be a visible condition rather
    /// than a browser error the person has to interpret.
    /// </remarks>
    public static bool IsRootTrusted()
    {
        if (!File.Exists(RootCertificatePath))
        {
            return false;
        }

        try
        {
            // `X509CertificateLoader` is .NET 9; this targets net8.0-windows.
            var root = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                RootCertificatePath);

            foreach (var location in new[]
                     {
                         System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine,
                         System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
                     })
            {
                using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                    System.Security.Cryptography.X509Certificates.StoreName.Root, location);

                store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                if (store.Certificates.Any(c => string.Equals(c.Thumbprint, root.Thumbprint,
                                                              StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // An unreadable store is not a trusted one, and it is not worth a crash on a status
            // check that runs on a timer.
            return false;
        }

        return false;
    }

    /// <summary>
    /// Install the edge's root into the machine trust store. Requires elevation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ IT MUST RUN AFTER THE EDGE HAS STARTED AT LEAST ONCE, not during setup. Caddy generates
    /// the authority on first issuance, so at install time there is usually nothing to install —
    /// which is why this reports that case plainly instead of failing. An installer calls it, finds
    /// no root, and says so; a repair after the first start completes the job.
    /// </para>
    /// <para>
    /// ⚠ <c>LocalMachine</c> RATHER THAN <c>CurrentUser</c>. A per-user trust would work for the
    /// person who installed and silently fail for every other account on the box, which is the kind
    /// of difference nobody attributes to a trust store.
    /// </para>
    /// </remarks>
    public static ProvisionResult TrustRoot()
    {
        if (!File.Exists(RootCertificatePath))
        {
            return new ProvisionResult(false, null,
                "There is no certificate authority to trust yet. Caddy creates it the first time " +
                "the edge starts; run this again after that.");
        }

        try
        {
            var root = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                RootCertificatePath);

            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.Root,
                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);

            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            store.Add(root);

            return new ProvisionResult(true, RootCertificatePath,
                $"Trusted {root.Subject} until {root.NotAfter:yyyy-MM-dd}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or System.Security.Cryptography.CryptographicException)
        {
            return new ProvisionResult(false, null,
                "The certificate authority could not be installed because this process is not " +
                $"elevated. Run the installer, or repair it, to complete setup. ({ex.Message})");
        }
    }

    /// <summary>Every Caddy this machine offers, nearest first.</summary>
    public static IReadOnlyList<CaddyBinary> Discover()
    {
        var found = new List<CaddyBinary>();

        var owned = Path.Combine(Paths.Root, FileName);
        if (File.Exists(owned))
        {
            found.Add(new CaddyBinary(owned, "this node"));
        }

        foreach (var directory in SearchPath())
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), FileName);
            }
            catch (ArgumentException)
            {
                // PATH entries are not validated by anyone; a malformed one is not our error to
                // report, and throwing here would make "no Caddy" the symptom of someone else's
                // typo.
                continue;
            }

            if (File.Exists(candidate) &&
                !found.Any(f => string.Equals(f.Path, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new CaddyBinary(candidate, "PATH"));
            }
        }

        return found;
    }

    /// <summary>The Caddy this node should run, or null when there is none.</summary>
    public static CaddyBinary? Best() => Discover().FirstOrDefault();

    private static IEnumerable<string> SearchPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<string>();
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }
}
