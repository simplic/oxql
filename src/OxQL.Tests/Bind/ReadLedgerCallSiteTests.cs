using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The read ledger is written at the binder's read sites, not inside <c>Shape.Resolve</c>, which also
/// binds hints, probes sort keys, recurses and serves explain (improvement plan §3.S). So every call
/// site of <c>Shape.Resolve</c> in the engine is classified here: a recorded read (it goes through
/// the binder's <c>Read</c>, with an explicit use), or a non-read that says why. A new call site fails
/// this test until it is one or the other.
/// </summary>
public partial class ReadLedgerCallSiteTests
{
    private static string Source([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [GeneratedRegex(@"\.Resolve\([^;]*(PathUsage\.\w+|\busage\b)")]
    private static partial Regex ResolveCall();

    [GeneratedRegex(@"\bRead\(\w+, .*?, PathUsage\.\w+, ReadUse\.(\w+), index, ")]
    private static partial Regex ReadCall();

    /// <summary>Every line of the engine's sources that calls <c>Shape.Resolve</c>, by file (relative to <c>src</c>).</summary>
    private static List<(string File, int Line, string Text, string Above)> ResolveSites()
    {
        var root = Source();
        var sites = new List<(string, int, string, string)>();

        foreach (var project in new[] { "OxQL.Core", "OxQL.Mongo", "OxQL.AspNetCore" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                    continue;

                var lines = File.ReadAllLines(file);

                for (var index = 0; index < lines.Length; index++)
                    if (ResolveCall().IsMatch(lines[index]) && !lines[index].TrimStart().StartsWith("//", StringComparison.Ordinal))
                        sites.Add((relative, index + 1, lines[index].Trim(), index > 0 ? lines[index - 1].Trim() : ""));
            }

        return sites;
    }

    /// <summary>
    /// The files whose calls are never reads of a query: they ask a shape what a path would be, for
    /// explain's types and flags, for the paths under a keyed alias, for what an owner said a target lacks.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Calls, string Why)> NotTheBinder = new Dictionary<string, (int, string)>(StringComparer.Ordinal)
    {
        ["OxQL.Core/Binding/ExplainTypes.cs"] = (17, "explain describes shapes: the flags of a member at a stage, a catalog entry, an item's element"),
        ["OxQL.Core/Binding/Shape.cs"] = (2, "a path under a keyed alias is checked against each target's own shape"),
        ["OxQL.Mongo/Explain/RemoteExplain.cs"] = (2, "whether a local target of a union has a path its remote targets' owners lack"),
        ["OxQL.Mongo/MongoQueryEngine.Explain.cs"] = (1, "the kind of a result column under an alias, read off the final shape"),
    };

    [Fact]
    public void Every_Resolve_call_site_is_a_recorded_read_or_an_allow_listed_non_read()
    {
        var sites = ResolveSites();
        var binder = sites.Where(site => site.File == "OxQL.Core/Binding/Binder.cs").ToList();

        sites.Select(site => site.File).Distinct().Should().BeEquivalentTo(NotTheBinder.Keys.Append("OxQL.Core/Binding/Binder.cs"),
            "a file that starts resolving paths is classified here first");

        foreach (var (file, (calls, why)) in NotTheBinder)
            sites.Count(site => site.File == file).Should().Be(calls, $"{file} resolves paths as non-reads ({why}); a new call there is checked against that reason");

        // In the binder a direct call is a non-read and says why on the line above it; the one call that
        // is not is the read helper's own, through which every recorded read goes.
        var helper = binder.Where(site => site.Text == "var resolution = at.Resolve(wire, usage);").ToList();
        var direct = binder.Except(helper).ToList();

        helper.Should().ContainSingle("the binder records a read in one place: Read");
        direct.Should().OnlyContain(site => site.Above.StartsWith("// not a read: ", StringComparison.Ordinal), "a direct Shape.Resolve in the binder is a non-read and names its reason");
        direct.Select(site => site.Above["// not a read: ".Length..]).Distinct().Should().BeEquivalentTo(
        [
            "the member an internal owner query is keyed by, on the entity row",
            "the key completing the order of an unwound shape",
            "a probe of which group keys the shape still carries",
            "the load set is bound against the join's own target",
            "the item collection of the join's own target",
            "the paths asked of the join's own target",
            "the key of the join's own target",
            "the child's own reference member, matched inside the join",
            "the select hint (or the default) of the join's own target",
            "a probe of which prefixes of a path are collections",
            "resolved here, recorded below as a read of the object's variant only",
            "the matched member of the join's own target",
            "why no target has a hint path",
            "why a branch path is not one a union join continues from",
        ]);
        direct.Should().HaveCount(19);
    }

    [Fact]
    public void The_recorded_reads_are_the_binders_read_sites_each_with_its_use()
    {
        var binder = File.ReadAllText(Path.Combine(Source(), "OxQL.Core", "Binding", "Binder.cs"));
        var uses = ReadCall().Matches(binder).Select(match => match.Groups[1].Value).GroupBy(use => use).ToDictionary(group => group.Key, group => group.Count());

        uses.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Match"] = 2,          // a condition's path, an any's collection (its inner paths are conditions again)
            ["Sort"] = 1,
            ["Project"] = 2,        // a projected path, the key an inclusion names
            ["Unwind"] = 1,
            ["GroupKey"] = 2,       // a key's path, a dateTrunc's date
            ["Aggregate"] = 1,
            ["ResolveKey"] = 1,
            ["CaseCondition"] = 1,  // the sibling member; the variant form is recorded beside its resolve
            ["LookupOn"] = 2,       // a local lookup's parent key, a remote lookup's
        });

        // Beside them, three reads are recorded without resolving: the variant of the object holding a
        // reference (resolved the line above), the root of a stage continued at an owner, and the root of
        // each branch of a union join, which the owners bind.
        Regex.Matches(binder, @"\bRecord\(index, ").Count.Should().Be(2, "Read's own call and the variant read");
        Regex.Matches(binder, @"reads\.Add\(new PathRead\(").Count.Should().Be(3, "Record's own, the continued stage's root, and a union join's branch roots");
    }
}
