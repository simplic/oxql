using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Refusals;

/// <summary>
/// The codes the engine declares against the documentation a client reads: every code is in the
/// closed list of the syntax guide and in the tables of the operations guide, and every member a
/// refusal carries for a code (<see cref="RefusalParams.Required"/>, which every answer of every
/// suite is held against) is named where the guide describes that code.
/// </summary>
public class RefusalsDocumentedTests
{
    private static string Guide(string name, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "docs", name)));

    private static IReadOnlyList<string> Catalogue() =>
        typeof(Codes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Every_code_the_engine_declares_is_in_both_guides()
    {
        var syntax = Guide("oxql-query-syntax.md");
        var operations = Guide("oxql-operations.md");

        Catalogue().Where(code => !syntax.Contains($"`{code}`", StringComparison.Ordinal)).Should().BeEmpty("the syntax guide lists every code");
        Catalogue().Where(code => !operations.Contains($"`{code}`", StringComparison.Ordinal)).Should().BeEmpty("the operations guide says the status and the cause of every code");
    }

    [Fact]
    public void Every_member_a_refusal_carries_for_a_code_is_named_in_the_operations_guide()
    {
        var operations = Guide("oxql-operations.md");
        var catalogue = Catalogue();

        RefusalParams.Required.Keys.Should().BeSubsetOf(catalogue);

        // The limits are said together: one sentence for what each carries, one table for their names.
        const string Limits = "**What a limit refusal carries.**";
        var limits = operations[operations.IndexOf(Limits, StringComparison.Ordinal)..];

        limits = limits[..limits.IndexOf("A caller without the contract header", StringComparison.Ordinal)];
        limits.Should().Contain("`params { limit, max, count }`");

        foreach (var (code, members) in RefusalParams.Required)
        {
            if (members.SequenceEqual(["limit", "max"]))
            {
                limits.Should().Contain($"`{code}`", "the table of the limits names every limit code");
                continue;
            }

            // What the guide says of a code: every line that names it.
            var said = string.Join('\n', operations.Split('\n').Where(line => line.Contains($"`{code}`", StringComparison.Ordinal)));

            members.Where(member => !said.Contains($"`{member}`", StringComparison.Ordinal) && !said.Contains(member + ",", StringComparison.Ordinal) && !said.Contains(member + " ", StringComparison.Ordinal) && !said.Contains(member + ":", StringComparison.Ordinal) && !said.Contains(member + "`", StringComparison.Ordinal))
                .Should().BeEmpty($"the operations guide names what a {code} refusal carries");
        }
    }
}
