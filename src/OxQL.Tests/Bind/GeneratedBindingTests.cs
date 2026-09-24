using System.Text;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Model;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.Tests.Bind;

/// <summary>
/// The generated battery: for every entity of every vendored document, every path, every
/// operator that applies, a valid operand binds and an invalid one is refused with its code;
/// every path that cannot be filtered or sorted is refused with the right code.
/// </summary>
public class GeneratedBindingTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Documents => new[] { "erp.json", "hr.json", "logistics.json", "vehicle.json" }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Every_filterable_path_binds_every_applicable_operator_with_a_valid_operand(string document)
    {
        var model = BindHost.Vendored(document);
        var failures = new StringBuilder();
        var bound = 0;

        foreach (var entity in model.Entities.Values)
            foreach (var path in entity.Paths.Where(path => path.Filterable && !path.IsAddonRoot && !path.Wire.Contains('*')))
            {
                var kind = path.LeafKind;
                var leaf = path.Shape.Leaf;

                if (leaf.Representation.BsonType == MongoDB.Bson.BsonType.Document)
                    continue;

                foreach (var op in BindHost.Operators)
                {
                    if (!OperandCoercer.Applies(op, kind))
                        continue;

                    var operand = op switch
                    {
                        "in" or "nin" => "[" + BindHost.ValidOperand(kind, leaf) + "]",
                        "exists" => "true",
                        "regex" => "\"^a\"",
                        "contains" or "startsWith" or "endsWith" => "\"a\"",
                        _ => BindHost.ValidOperand(kind, leaf),
                    };

                    var outcome = await BindHost.BindAsync(model, BindHost.Request(entity.Id, $$"""[{ "match": { "{{path.Wire}}": { "{{op}}": {{operand}} } } }]"""));

                    if (outcome is BindOutcome.Bound)
                        bound++;
                    else
                        failures.AppendLine($"{entity.Id} {path.Wire} ({Kinds.NameOf(kind)}) {op} {operand}: {BindHost.Describe(outcome)}");
                }
            }

        output.WriteLine($"{document}: {bound} bindings");
        failures.ToString().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Every_filterable_path_refuses_an_operand_of_the_wrong_kind_with_its_code(string document)
    {
        var model = BindHost.Vendored(document);
        var failures = new StringBuilder();

        foreach (var entity in model.Entities.Values)
            foreach (var path in entity.Paths.Where(path => path.Filterable && !path.IsAddonRoot && !path.Wire.Contains('*')))
            {
                var kind = path.LeafKind;

                if (path.Shape.Leaf.Representation.BsonType == MongoDB.Bson.BsonType.Document)
                    continue;

                var (operand, code) = BindHost.InvalidOperand(kind);

                foreach (var (op, expectedOperand, expectedCode) in new[]
                {
                    ("eq", operand, code),
                    ("in", operand == "5" ? "\"x\"" : "5", Codes.OperandNotArray),
                    ("exists", "\"yes\"", Codes.InvalidOperand),
                    ("eq", "[1]", Codes.InvalidOperand),
                    ("eq", "{ \"a\": 1 }", Codes.InvalidOperand),
                    ("gt", "null", kind is Kind.Bool or Kind.Guid or Kind.Binary ? Codes.InvalidOperand : Codes.InvalidOperand),
                })
                {
                    if (!OperandCoercer.Applies(op, kind))
                        continue;

                    var outcome = await BindHost.BindAsync(model, BindHost.Request(entity.Id, $$"""[{ "match": { "{{path.Wire}}": { "{{op}}": {{expectedOperand}} } } }]"""));

                    if (outcome is not BindOutcome.Failed failed || failed.Refusal.Errors?.Any(error => error.Code == expectedCode) != true)
                        failures.AppendLine($"{entity.Id} {path.Wire} ({Kinds.NameOf(kind)}) {op} {expectedOperand}: expected {expectedCode}, got {BindHost.Describe(outcome)}");
                }
            }

        failures.ToString().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Every_path_that_cannot_be_filtered_is_refused_and_every_sort_is_decided_by_the_model(string document)
    {
        var model = BindHost.Vendored(document);
        var failures = new StringBuilder();

        foreach (var entity in model.Entities.Values)
        {
            foreach (var path in entity.Paths.Where(path => !path.Filterable && !path.Wire.Contains('*')))
            {
                var outcome = await BindHost.BindAsync(model, BindHost.Request(entity.Id, $$"""[{ "match": { "{{path.Wire}}": { "eq": "x" } } }]"""));
                var expected = path.Stored ? Codes.NotFilterable : Codes.NotStored;

                if (outcome is not BindOutcome.Failed failed || failed.Refusal.Errors?.Any(error => error.Code == expected) != true)
                    failures.AppendLine($"filter {entity.Id} {path.Wire}: expected {expected}, got {BindHost.Describe(outcome)}");
            }

            foreach (var path in entity.Paths.Where(path => !path.Wire.Contains('*')))
            {
                var outcome = await BindHost.BindAsync(model, BindHost.Request(entity.Id, $$"""[{ "sort": [{ "{{path.Wire}}": "asc" }] }]"""));
                var sortable = path.Sortable && path.Shape.Leaf.Representation.BsonType != MongoDB.Bson.BsonType.Document;

                if (sortable && outcome is not BindOutcome.Bound)
                    failures.AppendLine($"sort {entity.Id} {path.Wire}: expected bound, got {BindHost.Describe(outcome)}");

                if (!sortable && (outcome is not BindOutcome.Failed failed || failed.Refusal.Errors?.Any(error => error.Code is Codes.NotSortable or Codes.NotStored) != true))
                    failures.AppendLine($"sort {entity.Id} {path.Wire}: expected NOT_SORTABLE, got {BindHost.Describe(outcome)}");
            }
        }

        failures.ToString().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Every_path_can_be_projected(string document)
    {
        var model = BindHost.Vendored(document);
        var failures = new StringBuilder();

        foreach (var entity in model.Entities.Values)
            foreach (var path in entity.Paths.Where(path => !path.Wire.Contains('*')))
            {
                var outcome = await BindHost.BindAsync(model, BindHost.Request(entity.Id, $$"""[{ "project": { "{{path.Wire}}": 1 } }]"""));

                if (outcome is not BindOutcome.Bound)
                    failures.AppendLine($"project {entity.Id} {path.Wire}: {BindHost.Describe(outcome)}");
            }

        failures.ToString().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Every_entity_is_scoped_and_an_unknown_path_is_refused(string document)
    {
        var model = BindHost.Vendored(document);

        foreach (var entity in model.Entities.Values)
        {
            var bound = await BindHost.BoundAsync(model, entity.Id, "[]");

            bound.Scope.OrganisationStorage.Should().Be("OrganizationId", entity.Id);
            bound.Scope.Organisation.Should().Be(BindHost.Organisation);
            bound.Page.Limit.Should().Be(100, "the default page size applies without a page stage");

            var error = await BindHost.ErrorAsync(model, entity.Id, """[{ "match": { "zzzNotAFieldAnywhere": { "eq": 1 } } }]""", Codes.UnknownPath);

            error.Stage.Should().Be(0);
            error.Path.Should().Be("zzzNotAFieldAnywhere");
        }
    }
}
