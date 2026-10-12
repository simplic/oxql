using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The contract 1 legacy support is removable: everything it is lives in the three <c>Legacy</c>
/// folders, every type there carries the <c>Legacy</c> prefix, and the engine outside them names a
/// legacy type on a handful of lines, each under the marker <c>LEGACY(compat.v1)</c> and each
/// listed in <c>src/docs/LEGACY.md</c>. A legacy type named anywhere else fails here, and so does
/// a type with the prefix declared outside the folders and a read of the legacy options outside
/// them, so the pipeline cannot grow into the contract 2 engine unnoticed.
/// </summary>
public class LegacyIsolationTests
{
    private const string Marker = "LEGACY(compat.v1)";

    /// <summary>The projects the engine ships.</summary>
    private static readonly string[] Projects = ["OxQL.Model", "OxQL.Core", "OxQL.Mongo", "OxQL.AspNetCore", "OxQL.Studio"];

    /// <summary>The files outside the legacy folders that name a legacy type: the seams. Removing the legacy support edits exactly these.</summary>
    private static readonly string[] Seams =
    [
        "OxQL.AspNetCore/Controllers/OxQLController.cs",
        "OxQL.AspNetCore/OxQLQueryService.cs",
        "OxQL.AspNetCore/ServiceCollectionExtensions.cs",
        "OxQL.Core/Models/OxQLOptions.cs",
        "OxQL.Mongo/ServiceCollectionExtensions.cs",
    ];

    /// <summary>The files outside the legacy folders that carry the marker without naming a legacy type: wording to revisit on removal.</summary>
    private static readonly string[] Mentions = ["OxQL.AspNetCore/Compat/CompatBinder.cs", "OxQL.Core/Engine/Outcomes.cs"];

    private static string Source([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));

    private static bool IsLegacy(string relative) => relative.Split('/').Contains("Legacy");

    private static IEnumerable<(string Relative, string[] Lines)> Files()
    {
        var root = Source();

        foreach (var project in Projects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                    continue;

                yield return (relative, File.ReadAllLines(file));
            }
        }
    }

    private static List<string> LegacyTypes() =>
        Files().Where(file => IsLegacy(file.Relative))
            .SelectMany(file => file.Lines)
            .SelectMany(line => Regex.Matches(line, @"\b(?:class|record|interface|enum|struct)\s+([A-Za-z_]\w*)").Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Every_legacy_file_carries_the_marker_and_every_type_in_it_the_prefix()
    {
        var legacy = Files().Where(file => IsLegacy(file.Relative)).ToList();

        legacy.Select(file => file.Relative.Split('/')[0]).Distinct().Should().BeEquivalentTo("OxQL.Core", "OxQL.Mongo", "OxQL.AspNetCore");
        legacy.Should().OnlyContain(file => file.Lines[0].StartsWith("// " + Marker, StringComparison.Ordinal), "the first line of a legacy file says what it is and where its removal is described");
        legacy.SelectMany(file => file.Lines).Where(line => Regex.IsMatch(line, @"^\s*namespace\s")).Distinct()
            .Should().OnlyContain(line => Regex.IsMatch(line, @"^namespace OxQL\.(Core|Mongo|AspNetCore)\.Legacy;$"));
        LegacyTypes().Should().NotBeEmpty().And.OnlyContain(name => name.StartsWith("Legacy", StringComparison.Ordinal) || name.StartsWith("ILegacy", StringComparison.Ordinal));
    }

    [Fact]
    public void The_engine_outside_the_legacy_folders_names_a_legacy_type_only_at_its_marked_seams()
    {
        var names = new Regex(@"\b(?:" + string.Join('|', LegacyTypes().Select(Regex.Escape)) + @")\b|\bLegacy\.Legacy\w+|OxQL\.(?:Core|Mongo|AspNetCore)\.Legacy\b");
        var named = new List<string>();
        var unmarked = new List<string>();
        var marked = new List<string>();

        foreach (var (relative, lines) in Files().Where(file => !IsLegacy(file.Relative)))
        {
            if (lines.Any(line => line.Contains(Marker, StringComparison.Ordinal)))
                marked.Add(relative);

            for (var index = 0; index < lines.Length; index++)
            {
                if (!names.IsMatch(lines[index]))
                    continue;

                named.Add(relative);

                // The marker stands on the line or within the five lines above it.
                if (!Enumerable.Range(Math.Max(0, index - 5), index - Math.Max(0, index - 5) + 1).Any(at => lines[at].Contains(Marker, StringComparison.Ordinal)))
                    unmarked.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
            }
        }

        named.Distinct().Order(StringComparer.Ordinal).Should().Equal(Seams, "a legacy type is named outside the legacy folders in these files and no other; a new seam is added here and to src/docs/LEGACY.md");
        unmarked.Should().BeEmpty("every line that names a legacy type stands under the marker");
        marked.Order(StringComparer.Ordinal).Should().Equal(Seams.Concat(Mentions).Order(StringComparer.Ordinal), "the marker stands where the legacy support is to be taken out, and nowhere else");
    }

    [Fact]
    public void No_legacy_type_is_declared_and_no_legacy_option_is_read_outside_the_legacy_folders()
    {
        var declared = new Regex(@"\b(?:class|record|interface|enum|struct)\s+I?Legacy[A-Z]\w*");
        var read = new Regex(@"\bCompat\s*\.\s*Legacy\b|\bLegacy\s*\.\s*(?:Enabled|Always|OrganisationPath|AllowedLookupSources|MaxTimeMs|EffectiveMaxTimeMs|DefaultMaxTimeMs)\b|\bLegacyOptions\b");
        var outside = Files().Where(file => !IsLegacy(file.Relative)).ToList();

        outside.SelectMany(file => file.Lines.Select((line, index) => (file.Relative, line, index)))
            .Where(entry => declared.IsMatch(entry.line))
            .Select(entry => $"{entry.Relative}:{entry.index + 1}: {entry.line.Trim()}")
            .Should().BeEmpty("a type that is part of the legacy support is declared in a Legacy folder, where its removal finds it");

        // The options are read where the pipeline decides and runs, and declared at the one marked seam of the options.
        outside.SelectMany(file => file.Lines.Select((line, index) => (file.Relative, line, index)))
            .Where(entry => read.IsMatch(entry.line) && !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(entry => entry.Relative)
            .Distinct()
            .Should().BeEquivalentTo(["OxQL.Core/Models/OxQLOptions.cs"], "no code outside the legacy folders behaves differently for a legacy option");

        // And the check sees a read where there is one.
        read.IsMatch("if (options.Compat.Legacy.Always)").Should().BeTrue();
        declared.IsMatch("internal sealed class LegacyThing").Should().BeTrue();
        declared.IsMatch("public const string LegacyStageUnsupported").Should().BeFalse();
    }

    [Fact]
    public void The_removal_guide_lists_every_legacy_file_and_every_seam()
    {
        var guide = File.ReadAllText(Path.Combine(Source(), "docs", "LEGACY.md"));

        foreach (var file in Files().Where(file => IsLegacy(file.Relative)).Select(file => file.Relative).Concat(Seams).Concat(Mentions))
            guide.Should().Contain(file, "the guide names what is deleted or edited when the legacy support goes");
    }
}
