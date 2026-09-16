using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Agience.Core;
using Xunit;

namespace Agience.Manager.Tests;

/// <summary>
/// The README's service table must say what <see cref="ServiceCatalog"/> does.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ THE DOC WENT STALE IN THE SAME SESSION THE CODE CHANGED. On 2026-09-15 the catalogue grew from
/// two kinds to five — Ember, Crystal and Chorus — and the README went on saying *"A Windows tray
/// application that runs Origin and Mantle"* and *"Ember is not here yet"*, with a two-column table
/// naming two services. Nothing failed. A reader following it would have concluded the manager
/// could not run a full node, which was true that morning and false by the afternoon.
/// </para>
/// <para>
/// ⚠ THIS IS THE CHEAPEST KIND OF DRIFT TO CATCH AND THE EASIEST TO MISS, because prose has no
/// compiler. The table is the one place in the document that states facts the code also states —
/// name, launch, port, health path — so it is the part worth holding to the code rather than to
/// someone's memory of it.
/// </para>
/// <para>
/// ⚠ IT ASSERTS THE TABLE CONTAINS THE FACTS, NOT THAT THE PROSE IS WORDED A CERTAIN WAY. A gate
/// that pinned sentences would be a gate that makes the document uneditable, and the first person
/// to improve a paragraph would delete the check instead.
/// </para>
/// </remarks>
public class ReadmeMatchesTheCatalogueTests
{
    /// <summary>The manager README, found by walking up from the test assembly.</summary>
    /// <remarks>
    /// Walked rather than hard-coded: the build output sits several directories below the project,
    /// and a fixed `../../../..` breaks the day the target framework or configuration changes —
    /// which fails as "README not found" and reads like the document was deleted.
    /// </remarks>
    private static string ReadmePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(candidate) && File.ReadAllText(candidate).Contains("ServiceCatalog.cs"))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "the manager README was not found above " + AppContext.BaseDirectory +
            " — if it moved, point this test at it rather than deleting the check.");
    }

    private static string Readme() => File.ReadAllText(ReadmePath());

    [Fact]
    public void The_readme_was_actually_found_and_has_a_service_table()
    {
        // A document that cannot be read makes every assertion below vacuously true, which is the
        // exact shape this file exists to prevent one directory over.
        var text = Readme();
        Assert.Contains("| kind |", text);
        Assert.True(text.Length > 2000, "the README is suspiciously short; it may have been truncated");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_appears_in_the_readme_with_its_port(string display, int basePort)
    {
        var text = Readme();
        Assert.True(text.Contains("| " + display + " |"),
            $"{display} is a service this application runs and the README's table does not list it. " +
            "A reader takes that table as the roster.");

        var row = text.Split('\n').First(l => l.StartsWith("| " + display + " |"));
        Assert.True(row.Contains(basePort.ToString()),
            $"the README's {display} row does not name its first port ({basePort}): {row.Trim()}");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_kind_proved_by_an_http_path_names_that_path_in_the_readme(string display, int _)
    {
        var kind = ServiceCatalog.All.Single(k => k.Display == display);
        if (kind.Health != HealthRule.HttpOk)
        {
            return;     // proved by its bound port; there is no path to state
        }

        var row = Readme().Split('\n').First(l => l.StartsWith("| " + display + " |"));
        Assert.True(row.Contains(kind.HealthPath!),
            $"the README says {display} is proved up some other way than {kind.HealthPath}. " +
            "Crystal's is /health and Chorus's is /healthz — the near-identical spelling is why " +
            $"this is checked rather than remembered. Row: {row.Trim()}");
    }

    [Fact]
    public void The_readme_does_not_still_say_a_kind_is_missing()
    {
        // The specific sentence that outlived its subject. Named so it cannot quietly return.
        Assert.DoesNotContain("Ember is not here yet", Readme());
    }

    public static TheoryData<string, int> Kinds()
    {
        var data = new TheoryData<string, int>();
        foreach (var kind in ServiceCatalog.All)
        {
            data.Add(kind.Display, kind.BasePort);
        }

        return data;
    }
}
