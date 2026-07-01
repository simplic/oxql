using OxQL.Core.Models;
using OxQL.Core.Validation;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Core;

public class QueryValidatorTests
{
    private readonly OxQLOptions _options = new()
    {
        MaxPageSize = 500,
        DefaultPageSize = 50,
        MaxPipelineStages = 20,
        MaxLookupStages = 5,
        MaxUnwindStages = 5,
        MaxGroupFields = 20,
        MaxProjectionFields = 50,
        AllowedLookupSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "customers", "products" },
        AllowedResolveSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "crm.customer" },
        RegexMaxLength = 200
    };

    private QueryValidator CreateValidator() => new(_options);

    [Fact]
    public void Validate_ValidSimpleQuery_Succeeds()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        And =
                        [
                            new FilterCondition { Path = "attributes.amount", Op = "gte" }
                        ]
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyEntityType_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "",
            Pipeline = []
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_ENTITY_TYPE");
    }

    [Fact]
    public void Validate_PathWithDollarSign_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "$where", Op = "eq" }
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_PATH_DOLLAR");
    }

    [Fact]
    public void Validate_PathWithDoubleDot_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "attributes..amount", Op = "eq" }
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_PATH_TRAVERSAL");
    }

    [Fact]
    public void Validate_EmptyPathSegment_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = ".amount", Op = "eq" }
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "EMPTY_PATH_SEGMENT");
    }

    [Fact]
    public void Validate_UnknownOperator_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "eval" }
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_OPERATOR");
    }

    [Fact]
    public void Validate_DisallowedLookupSource_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Lookup = new LookupStage
                    {
                        From = "secrets",
                        LocalPath = "attributes.secretId",
                        ForeignPath = "id",
                        As = "secret"
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DISALLOWED_LOOKUP_SOURCE");
    }

    [Fact]
    public void Validate_AllowedLookupSource_Succeeds()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Lookup = new LookupStage
                    {
                        From = "customers",
                        LocalPath = "attributes.customerId",
                        ForeignPath = "id",
                        As = "customer"
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ExceedMaxPipelineStages_Fails()
    {
        var stages = Enumerable.Range(0, 25)
            .Select(_ => new PipelineStage
            {
                Match = new MatchStage
                {
                    Condition = new FilterCondition { Path = "a", Op = "eq" }
                }
            })
            .ToList();

        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline = stages
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MAX_PIPELINE_STAGES_EXCEEDED");
    }

    [Fact]
    public void Validate_ExceedPageSize_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Page = new PageStage { Limit = 1000 }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "PAGE_SIZE_EXCEEDED");
    }

    [Fact]
    public void Validate_InvalidSortDirection_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Sort = [new SortField { Path = "amount", Direction = "random" }]
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_SORT_DIRECTION");
    }

    [Fact]
    public void Validate_DisallowedResolveSource_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Resolve = new ResolveStage
                    {
                        Source = "external.unknown",
                        LocalPath = "attributes.id",
                        As = "external"
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DISALLOWED_RESOLVE_SOURCE");
    }

    [Fact]
    public void Validate_ExceedMaxProjectionFields_Fails()
    {
        var options = new OxQLOptions { MaxProjectionFields = 3 };
        var validator = new QueryValidator(options);

        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Project = new ProjectStage
                    {
                        Fields = new Dictionary<string, int> { ["a"] = 1, ["b"] = 1, ["c"] = 1, ["d"] = 1, ["e"] = 1 }
                    }
                }
            ]
        };

        var result = validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MAX_PROJECTION_FIELDS_EXCEEDED");
    }

    [Fact]
    public void Validate_MissingFilterOperator_Fails()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = null }
                    }
                }
            ]
        };

        var result = CreateValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_OPERATOR");
    }
}
