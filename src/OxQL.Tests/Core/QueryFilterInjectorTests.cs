using FluentAssertions;
using OxQL.Core.Filtering;
using OxQL.Core.Models;
using OxQL.Core.Normalization;
using Xunit;

namespace OxQL.Tests.Core;

public class QueryFilterInjectorTests
{
    private static QueryRequest RequestWithMatch(MatchStage? match, QueryVariables? variables = null)
    {
        var pipeline = new List<PipelineStage>();
        if (match is not null)
            pipeline.Add(new PipelineStage { Match = match });
        pipeline.Add(new PipelineStage { Page = new PageStage { Limit = 25 } });

        return new QueryRequest
        {
            EntityType = "vehicle",
            Variables = variables,
            Pipeline = pipeline
        };
    }

    [Fact]
    public void Inject_WithNoFilters_ReturnsSameInstance()
    {
        var request = RequestWithMatch(new MatchStage
        {
            Condition = new FilterCondition { Path = "MatchCode", Op = "eq" }
        });

        var result = QueryFilterInjector.Inject(request, []);

        result.Should().BeSameAs(request);
    }

    [Fact]
    public void Inject_WrapsExistingMatch_InRootAnd()
    {
        var request = RequestWithMatch(new MatchStage
        {
            Condition = new FilterCondition { Path = "MatchCode", Op = "neq" }
        });

        var filter = InjectedFilter.Create("OrganizationId", "org-1");
        var result = QueryFilterInjector.Inject(request, [filter]);

        var match = result.Pipeline.First(s => s.Match is not null).Match!;
        match.And.Should().NotBeNull();
        match.And.Should().HaveCount(2);

        // First operand is the caller's original condition.
        match.And![0].Path.Should().Be("MatchCode");
        match.And[0].Op.Should().Be("neq");

        // Second operand is the injected condition referencing a $var.
        match.And[1].Path.Should().Be("OrganizationId");
        match.And[1].Op.Should().Be("eq");
        match.And[1].Value!.Value.GetProperty("$var").GetString().Should().Be(filter.VariableName);
    }

    [Fact]
    public void Inject_PreservesExistingLogicalGroup_AsSingleOperand()
    {
        var request = RequestWithMatch(new MatchStage
        {
            Or =
            [
                new FilterCondition { Path = "A", Op = "eq" },
                new FilterCondition { Path = "B", Op = "eq" }
            ]
        });

        var result = QueryFilterInjector.Inject(request, [InjectedFilter.Create("OrganizationId", "org-1")]);

        var match = result.Pipeline.First(s => s.Match is not null).Match!;
        match.And.Should().HaveCount(2);
        // The existing OR is preserved intact as the first AND operand.
        match.And![0].Or.Should().NotBeNull();
        match.And[0].Or.Should().HaveCount(2);
        match.And[1].Path.Should().Be("OrganizationId");
    }

    [Fact]
    public void Inject_WhenNoMatchStage_PrependsMatchStage()
    {
        var request = RequestWithMatch(match: null);

        var result = QueryFilterInjector.Inject(request, [InjectedFilter.Create("OrganizationId", "org-1")]);

        result.Pipeline[0].Match.Should().NotBeNull();
        result.Pipeline[0].Match!.And.Should().ContainSingle();
        result.Pipeline[0].Match!.And![0].Path.Should().Be("OrganizationId");
    }

    [Fact]
    public void Inject_AddsValue_ToVariables()
    {
        var request = RequestWithMatch(new MatchStage
        {
            Condition = new FilterCondition { Path = "MatchCode", Op = "eq" }
        });

        var filter = InjectedFilter.Create("OrganizationId", "org-42");
        var result = QueryFilterInjector.Inject(request, [filter]);

        result.Variables.Should().NotBeNull();
        result.Variables!.GetValue(filter.VariableName).Should().Be("org-42");
    }

    [Fact]
    public void Inject_OverwritesCallerVariable_OfSameName()
    {
        var filter = InjectedFilter.Create("OrganizationId", "trusted-org");
        var variables = new QueryVariables
        {
            Values = new Dictionary<string, object?> { [filter.VariableName] = "spoofed-org" }
        };
        var request = RequestWithMatch(
            new MatchStage { Condition = new FilterCondition { Path = "MatchCode", Op = "eq" } },
            variables);

        var result = QueryFilterInjector.Inject(request, [filter]);

        result.Variables!.GetValue(filter.VariableName).Should().Be("trusted-org");
    }

    [Fact]
    public void Inject_MergesMultipleFilters()
    {
        var request = RequestWithMatch(match: null);

        var result = QueryFilterInjector.Inject(request,
        [
            InjectedFilter.Create("OrganizationId", "org-1"),
            InjectedFilter.Create("TenantId", "tenant-9")
        ]);

        var match = result.Pipeline[0].Match!;
        match.And.Should().HaveCount(2);
        match.And!.Select(c => c.Path).Should().BeEquivalentTo(["OrganizationId", "TenantId"]);
    }

    [Fact]
    public void Inject_ProducesCacheKey_IndependentOfTenantValue()
    {
        // The security-critical property: two requests that differ only by the injected
        // tenant value must yield the SAME plan-cache key (shape), because the value is a
        // $var reference resolved per-request rather than a baked-in literal.
        var normalizer = new QueryRequestNormalizer(new OxQLOptions());

        QueryRequest ForTenant(string org)
        {
            var request = RequestWithMatch(new MatchStage
            {
                Condition = new FilterCondition { Path = "MatchCode", Op = "eq" }
            });
            return QueryFilterInjector.Inject(request, [InjectedFilter.Create("OrganizationId", org)]);
        }

        var keyA = normalizer.GenerateCacheKey(ForTenant("org-1"));
        var keyB = normalizer.GenerateCacheKey(ForTenant("org-2"));

        keyA.Should().Be(keyB);
    }

    [Fact]
    public void Inject_TwoFilters_SamePath_GetDistinctVariableNamesAndValues()
    {
        // Regression: two providers independently returning a filter for the same field
        // path both produce __oxql_OrganizationId via InjectedFilter.Create. Before the
        // fix MergeVariables would silently overwrite the first value with the second's,
        // and both $var conditions would point at the same (wrong) variable.
        var request = RequestWithMatch(match: null);

        var filterA = InjectedFilter.Create("OrganizationId", "org-A");
        var filterB = InjectedFilter.Create("OrganizationId", "org-B");

        var result = QueryFilterInjector.Inject(request, [filterA, filterB]);

        // Both conditions must be present in the match.
        var match = result.Pipeline[0].Match!;
        match.And.Should().HaveCount(2);
        match.And![0].Path.Should().Be("OrganizationId");
        match.And![1].Path.Should().Be("OrganizationId");

        // The variable names referenced by the two conditions must be distinct.
        var varA = match.And[0].Value!.Value.GetProperty("$var").GetString()!;
        var varB = match.And[1].Value!.Value.GetProperty("$var").GetString()!;
        varA.Should().NotBe(varB);

        // Each variable must carry its own value.
        result.Variables!.GetValue(varA).Should().Be("org-A");
        result.Variables!.GetValue(varB).Should().Be("org-B");
    }

    [Fact]
    public void Inject_ThreeFilters_SamePath_GetSuffix2And3()
    {
        var request = RequestWithMatch(match: null);

        var result = QueryFilterInjector.Inject(request,
        [
            InjectedFilter.Create("TenantId", "t1"),
            InjectedFilter.Create("TenantId", "t2"),
            InjectedFilter.Create("TenantId", "t3")
        ]);

        var match = result.Pipeline[0].Match!;
        match.And.Should().HaveCount(3);

        var names = match.And!
            .Select(c => c.Value!.Value.GetProperty("$var").GetString()!)
            .ToList();

        names[0].Should().Be("__oxql_TenantId");
        names[1].Should().Be("__oxql_TenantId_2");
        names[2].Should().Be("__oxql_TenantId_3");

        result.Variables!.GetValue("__oxql_TenantId").Should().Be("t1");
        result.Variables!.GetValue("__oxql_TenantId_2").Should().Be("t2");
        result.Variables!.GetValue("__oxql_TenantId_3").Should().Be("t3");
    }

    [Fact]
    public void Inject_SuffixGeneration_SkipsNamesAlreadyPresentInList()
    {
        // If the list already contains a filter whose name is __oxql_x_2, the deduplicator
        // must not assign __oxql_x_2 to the second __oxql_x — it must skip to __oxql_x_3.
        var request = RequestWithMatch(match: null);

        var explicit2 = new InjectedFilter { Path = "x", Op = "eq", VariableName = "__oxql_x_2", Value = "explicit" };
        var first    = InjectedFilter.Create("x", "first");   // gets __oxql_x
        var collision = InjectedFilter.Create("x", "collision"); // must NOT get __oxql_x_2

        var result = QueryFilterInjector.Inject(request, [explicit2, first, collision]);

        var match = result.Pipeline[0].Match!;
        var names = match.And!
            .Select(c => c.Value!.Value.GetProperty("$var").GetString()!)
            .ToList();

        names[0].Should().Be("__oxql_x_2");
        names[1].Should().Be("__oxql_x");
        names[2].Should().Be("__oxql_x_3"); // NOT __oxql_x_2

        result.Variables!.GetValue("__oxql_x_2").Should().Be("explicit");
        result.Variables!.GetValue("__oxql_x").Should().Be("first");
        result.Variables!.GetValue("__oxql_x_3").Should().Be("collision");
    }
}
