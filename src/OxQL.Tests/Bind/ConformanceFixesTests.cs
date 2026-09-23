using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// One case per defect the live conformance validation of 2026-09-18 proved against a running
/// lab (<c>.api/deep/oxql-lab/v2/FINDINGS-*.md</c>). Each pins the fixed behaviour, so the
/// defect coming back turns the case red rather than passing quietly; the finding id is in
/// every name and the reproduction is in every comment.
/// </summary>
public class ConformanceFixesTests
{
    private static EntityModel Model => BindHost.Probe;
    private const string Order = "probe.order";

    private static async Task<BoundPipeline> Bound(string pipeline, RequestContext? context = null) =>
        await BindHost.BoundAsync(Model, Order, pipeline, context);

    private static async Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        await BindHost.ErrorAsync(Model, Order, pipeline, code, context);

    private static BoundCondition.Leaf Leaf(BoundPipeline bound, int stage = 0) =>
        (BoundCondition.Leaf)((BoundStage.Match)bound.Stages[stage]).Condition;

    // ---- F2 / F3 / F11 · the decimal duality -------------------------------------------------

    /// <summary>
    /// F2. The engine returned <c>"125000.00"</c> for a string-stored decimal and built the
    /// operand for the same value as <c>"125000"</c>, so <c>eq</c> on a value read straight
    /// out of a row missed the row it came from — 14 of 15 rows on the lab's vehicle corpus —
    /// and <c>neq</c> returned it. The text alternative is a pattern over every scale now.
    /// </summary>
    [Theory]
    [InlineData("\"125000.00\"", "^125000(?:\\.0+)?$")]
    [InlineData("125000", "^125000(?:\\.0+)?$")]
    [InlineData("\"99999.99\"", "^99999\\.990*$")]
    [InlineData("\"-42.50\"", "^-42\\.50*$")]
    [InlineData("\"0.000001\"", "^0\\.0000010*$")]
    public async Task F2_a_decimal_operand_reaches_every_scale_the_driver_could_have_written(string operand, string pattern)
    {
        var bound = await Bound($$"""[{ "match": { "amount": { "eq": {{operand}} } } }]""");
        var alternatives = Leaf(bound).Operand.Should().BeOfType<BoundOperand.Tolerant>().Subject.Alternatives;

        alternatives[1].Should().Be(new BsonRegularExpression(pattern));
    }

    /// <summary>
    /// F2, the round trip both ways: the spelling the operand is built with is the spelling a
    /// row is read back in, whichever bracket the row came from. <c>G29</c> was neither — it
    /// dropped a trailing zero on the way in and left the text bracket verbatim on the way
    /// out, so one member read back in two spellings (F11) and neither found its own row.
    /// </summary>
    [Theory]
    [InlineData("125000.00", "125000")]
    [InlineData("1000.00", "1000")]
    [InlineData("99999.99", "99999.99")]
    [InlineData("0.0000001", "0.0000001")]
    [InlineData("-42.50", "-42.5")]
    public void F11_a_decimal_reads_back_in_one_spelling_from_either_bracket(string stored, string expected)
    {
        var money = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);

        DecimalText.Canonical(money).Should().Be(expected);

        // The typed bracket and the text bracket agree, which is what makes the value a
        // caller sends back the value the engine wrote.
        Mongo.WireEncoder.EncodeScalar(new BsonDecimal128(new Decimal128(money)), Kind.Decimal, null)!.ToString().Should().Be(expected);
        Mongo.WireEncoder.EncodeScalar(new BsonString(stored), Kind.Decimal, null)!.ToString().Should().Be(expected);

        // And the value that comes back matches the pattern the operand is built from.
        System.Text.RegularExpressions.Regex.IsMatch(stored, DecimalText.ScalePattern(money)).Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(expected, DecimalText.ScalePattern(money)).Should().BeTrue();
    }

    /// <summary>
    /// F2. Text that is not a decimal is not normalised: inventing a number for it would
    /// change what the row holds.
    /// </summary>
    [Fact]
    public void F11_text_that_is_not_a_decimal_stays_verbatim() =>
        Mongo.WireEncoder.EncodeScalar(new BsonString("n/a"), Kind.Decimal, null)!.ToString().Should().Be("n/a");

    /// <summary>
    /// F3. A range used to compare the text bracket as text: <c>lt "100000"</c> dropped
    /// <c>"99999.99"</c> and <c>gte "99999.99"</c> dropped <c>"125000.00"</c>, and the error
    /// was not even monotone. The text bracket is gone from an ordered comparison, and what
    /// it no longer reaches is said out loud.
    /// </summary>
    [Theory]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("lt")]
    [InlineData("lte")]
    public async Task F3_an_ordered_decimal_comparison_never_compares_text(string op)
    {
        var bound = await Bound($$"""[{ "match": { "amount": { "{{op}}": "100000" } } }]""");

        Leaf(bound).Operand.Should().BeOfType<BoundOperand.Single>()
            .Which.Value.Should().BeOfType<BsonDecimal128>();

        bound.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.DecimalTextExcluded && diagnostic.Path == "amount");
    }

    /// <summary>
    /// F3. Where the member's declared representation is <c>String</c> there is no typed
    /// bracket to fall back to, so nothing about an ordered comparison can be answered: a
    /// refusal beats a wrong row. Equality still works, by pattern.
    /// </summary>
    [Fact]
    public async Task F3_a_decimal_declared_as_text_refuses_a_range_and_still_matches_equality()
    {
        var refusal = await Error("""[{ "match": { "moneyText": { "lt": "100000" } } }]""", Codes.DecimalTextNotOrderable);

        refusal.Path.Should().Be("moneyText");
        refusal.Message.Should().Contain("orders by characters");

        var bound = await Bound("""[{ "match": { "moneyText": { "eq": "1.50" } } }]""");

        Leaf(bound).Operand.Should().BeOfType<BoundOperand.Single>()
            .Which.Value.Should().Be(new BsonRegularExpression("^1\\.50*$"));
    }

    // ---- F4 / F8 · the enum operand ----------------------------------------------------------

    /// <summary>
    /// F4. <c>TryInteger</c> accepts the operand as a <c>long</c>; the <c>checked((int))</c>
    /// cast behind it then threw <c>OverflowException</c> out of the binder and the caller got
    /// a bare 500 with no code, no path and no stage — while one parse earlier a value above
    /// <c>long.MaxValue</c> was correctly refused with a code.
    /// </summary>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("\"9223372036854775808\"")]
    public async Task F4_an_enum_number_outside_int32_is_a_coded_refusal_not_a_fault(string operand)
    {
        // The membership check settles it before the cast is reached, which is the answer the
        // value above long.MaxValue already got one parse earlier — so a number too large to
        // be a member and a number that is simply not one now read the same.
        var refusal = await Error($$"""[{ "match": { "state": { "eq": {{operand}} } } }]""", Codes.UnknownEnumMember);

        refusal.Path.Should().Be("state");
        refusal.Message.Should().Contain("is not a member of the enum");
    }

    /// <summary>
    /// F8. The membership lookup ran only for a string-represented enum, which is none of the
    /// enums in the fleet, so <c>eq 7</c> answered 200 with no rows and <c>eq 99</c> answered
    /// 200 with the one row that held an undeclared value — while <c>eq "Nope"</c> was
    /// correctly a 400. A name and a number for the same nonexistent member now agree.
    /// </summary>
    [Fact]
    public async Task F8_an_enum_number_is_validated_whatever_the_storage()
    {
        (await Error("""[{ "match": { "state": { "eq": 7 } } }]""", Codes.UnknownEnumMember))
            .Message.Should().Contain("is not a member of the enum");

        (await Error("""[{ "match": { "state": { "eq": "Nope" } } }]""", Codes.UnknownEnumMember))
            .Message.Should().Contain("is not a member of the enum");

        (await Error("""[{ "match": { "state": { "in": [0, 42] } } }]""", Codes.UnknownEnumMember))
            .Path.Should().Be("state");

        // A declared member still binds, by name and by number, to the stored form.
        Leaf(await Bound("""[{ "match": { "state": { "eq": 1 } } }]""")).Operand
            .Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonInt32(1));
        Leaf(await Bound("""[{ "match": { "wide": { "eq": 5000000000 } } }]""")).Operand
            .Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonInt64(5_000_000_000));
    }

    // ---- F13 · a char member -----------------------------------------------------------------

    /// <summary>
    /// F13. A <c>char</c> is published as a <c>string</c> and stored as its code point, so it
    /// passed the kind gate for the four text operators, the coercer handed the compiler a
    /// <c>BsonInt32</c>, and <c>.AsString</c> on it threw <c>InvalidCastException</c> into a
    /// bare 500. Latent on the fleet when it was found, live on the lab's conformance entity.
    /// </summary>
    [Theory]
    [InlineData("contains")]
    [InlineData("startsWith")]
    [InlineData("endsWith")]
    [InlineData("regex")]
    public async Task F13_a_text_operator_on_a_char_member_is_a_coded_refusal_not_a_fault(string op)
    {
        var refusal = await Error($$"""[{ "match": { "initial": { "{{op}}": "A" } } }]""", Codes.InvalidOperand);

        refusal.Path.Should().Be("initial");
        refusal.Message.Should().Contain("single character");
    }

    /// <summary>F13, the other half: comparing a char by its one value goes on working when the comparison is exact.</summary>
    [Fact]
    public async Task F13_a_char_member_still_compares_by_value()
    {
        Leaf(await Bound("""[{ "match": { "initial": { "eq": "a", "options": { "caseSensitive": true } } } }]""")).Operand
            .Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonInt32('a'));

        Leaf(await Bound("""[{ "match": { "initial": { "in": ["a", "b"], "options": { "caseSensitive": true } } } }]""")).Operand
            .Should().BeOfType<BoundOperand.Set>().Which.Values.Should().Equal(new BsonInt32('a'), new BsonInt32('b'));
    }

    // ---- F5 · the indefinite article ---------------------------------------------------------

    /// <summary>
    /// F5. These strings are the whole of what a dispatcher sees for a refused grid filter,
    /// and they read "a enum", "a int", "a object", "a array".
    /// </summary>
    [Fact]
    public async Task F5_refusal_messages_carry_the_right_article()
    {
        (await Error("""[{ "match": { "state": { "contains": "Op" } } }]""", Codes.InvalidOperand))
            .Message.Should().Contain("an enum").And.NotContain("a enum");

        (await Error("""[{ "match": { "count": { "startsWith": "6" } } }]""", Codes.InvalidOperand))
            .Message.Should().Contain("an int").And.NotContain("a int");

        (await Error("""[{ "match": { "shipTo": { "eq": null } } }]""", Codes.NotFilterable))
            .Message.Should().Contain("an object").And.NotContain("a object");

        (await Error("""[{ "match": { "items": { "eq": null } } }]""", Codes.NotFilterable))
            .Message.Should().Contain("an array").And.NotContain("a array");

        Kinds.WithArticle(Kind.String).Should().Be("a string");
        Kinds.WithArticle(Kind.Enum).Should().Be("an enum");
    }

    // ---- G6 · the timezone -------------------------------------------------------------------

    /// <summary>
    /// G6. <c>TryFindSystemTimeZoneById</c> is case-insensitive and Mongo's <c>$dateTrunc</c>
    /// is not, so <c>europe/berlin</c> passed validation, travelled verbatim and threw at
    /// execution: HTTP 500. The canonical id the lookup found is what goes on the wire now.
    /// </summary>
    [Theory]
    [InlineData("europe/berlin")]
    [InlineData("EUROPE/BERLIN")]
    [InlineData("Europe/berlin")]
    [InlineData(" Europe/Berlin ")]
    public async Task G6_a_case_variant_timezone_is_canonicalised_rather_than_executed_as_written(string timezone)
    {
        var bound = await Bound($$"""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day", "timezone": "{{timezone}}" }, "as": "d" }], "fields": {} } }]""");
        var key = ((BoundStage.Group)bound.Stages[0]).Keys[0];

        key.Trunc!.Timezone.Should().Be("Europe/Berlin");
    }

    /// <summary>G6. A zone that is not a zone is still refused, and a link name is still not a zone.</summary>
    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("CET")]
    public async Task G6_an_unknown_timezone_is_still_refused(string timezone) =>
        (await Error($$"""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day", "timezone": "{{timezone}}" }, "as": "d" }], "fields": {} } }]""", Codes.InvalidTimezone))
            .Message.Should().Contain("is not an IANA timezone");

    // ---- G3 · an unrecognised aggregate argument ---------------------------------------------

    /// <summary>
    /// G3. The argument reader fell through to "a literal object" for anything it did not
    /// recognise, Mongo evaluated that to missing, and <c>min</c>, <c>max</c>, <c>first</c>,
    /// <c>last</c> and <c>push</c> answered 200 with a column of nulls. A typo under
    /// <c>sum</c> was caught only because <c>sum</c> checks the argument's kind.
    /// </summary>
    [Theory]
    [InlineData("min", """{ "power": [{ "literal": 2 }, { "literal": 3 }] }""")]
    [InlineData("max", """{ "bogus": 1 }""")]
    [InlineData("push", """{ "bogus": 1 }""")]
    [InlineData("first", "{}")]
    [InlineData("last", "{}")]
    public async Task G3_an_unrecognised_argument_operator_is_refused(string function, string argument)
    {
        var refusal = await Error($$"""[{ "group": { "by": [], "fields": { "s": { "{{function}}": {{argument}} } } } }]""", Codes.InvalidAggregateArgument);

        refusal.Message.Should().ContainAny("is not an argument operator", "names no path");
    }

    /// <summary>G3. The arithmetic operators, a path, a literal and a variable all still bind.</summary>
    [Fact]
    public async Task G3_the_recognised_argument_forms_still_bind()
    {
        await Bound("""[{ "group": { "by": [], "fields": { "s": { "min": { "add": [{ "path": "ratio" }, { "literal": 1 }] } } } } }]""");
        await Bound("""[{ "group": { "by": [], "fields": { "s": { "min": "ratio" } } } }]""");
        await Bound("""[{ "group": { "by": [], "fields": { "s": { "min": { "literal": 2 } } } } }]""");
    }

    // ---- P1 / R12 / C13 · stage members the binder does not recognise ------------------------

    /// <summary>
    /// P1 and R12. A sort entry is a single-key object; the converter returned the first key
    /// and dropped the rest, so <c>{"a":"asc","b":"desc"}</c> answered 200 ordered by
    /// <c>a</c> alone with no diagnostic — a shape a deployment-file grid definition can
    /// produce, and one the converter's own error message calls invalid.
    /// </summary>
    [Fact]
    public async Task P1_a_sort_entry_naming_two_paths_is_refused()
    {
        var refusal = await Error("""[{ "sort": [{ "number": "asc", "amount": "desc" }] }]""", Codes.UnknownStageMember);

        refusal.Message.Should().Contain("A sort entry names one path");
        refusal.Path.Should().Be("number");

        // One object per key is the form, and it binds both.
        var bound = await Bound("""[{ "sort": [{ "number": "asc" }, { "amount": "desc" }] }]""");

        ((BoundStage.Sort)bound.Stages[0]).Fields.Should().HaveCount(2);
    }

    /// <summary>R12 and C13. An empty sort entry names no path at all.</summary>
    [Fact]
    public async Task R12_an_empty_sort_entry_is_refused() =>
        (await Error("""[{ "sort": [{}] }]""", Codes.UnknownStageMember))
            .Message.Should().Contain("one path and a direction");

    /// <summary>
    /// C13. <c>UNKNOWN_STAGE_MEMBER</c> reached <c>lookup</c> and <c>resolve</c> only. Every
    /// other stage was a plain record read by System.Text.Json, whose default is to skip an
    /// unmapped member, so a <c>page</c> carrying <c>skip</c> instead of <c>offset</c> took
    /// its page from the top and said nothing — while the contract promises that a v1 member
    /// on a lookup is refused rather than ignored.
    /// </summary>
    [Theory]
    [InlineData("""{ "page": { "limit": 5, "skip": 10 } }""", "skip")]
    [InlineData("""{ "page": { "limit": 5, "includeCount": true } }""", "includeCount")]
    [InlineData("""{ "unwind": { "path": "items", "preserveNulls": true } }""", "preserveNulls")]
    [InlineData("""{ "unwind": { "path": "items", "includeArrayIndex": "i" } }""", "includeArrayIndex")]
    [InlineData("""{ "group": { "by": [], "fields": {}, "having": {} } }""", "having")]
    [InlineData("""{ "group": { "by": [{ "path": "state", "as": "s", "trunc": {} }], "fields": {} } }""", "trunc")]
    public async Task C13_a_stage_member_the_engine_does_not_have_is_refused(string stage, string member)
    {
        var refusal = await Error($"[{stage}]", Codes.UnknownStageMember);

        refusal.Message.Should().Contain(member);
    }

    /// <summary>C13. The members each stage does have still bind, spelled correctly.</summary>
    [Fact]
    public async Task C13_the_members_each_stage_does_have_still_bind()
    {
        var page = await Bound("""[{ "page": { "limit": 5, "offset": 10, "includeTotalCount": true } }]""");

        page.Page.Limit.Should().Be(5);
        page.Page.Offset.Should().Be(10);
        page.Page.IncludeTotalCount.Should().BeTrue();

        var unwound = await Bound("""[{ "unwind": { "path": "items", "as": "line", "preserveNull": true, "includeIndex": "i" } }]""");
        var unwind = (BoundStage.Unwind)unwound.Stages[0];

        unwind.PreserveNull.Should().BeTrue();
        unwind.As.Should().Be("line");
        unwind.IncludeIndex.Should().Be("i");
    }

    // ---- R6 · a variable inside an `in` array ------------------------------------------------

    /// <summary>
    /// R6. The whole-array form of a variable worked and the per-element form did not: an
    /// element that was a <c>$var</c> wrapper reached the scalar coercion as a JSON object and
    /// came back <c>INVALID_OPERAND</c>, although the specification says the element form works.
    /// </summary>
    [Fact]
    public async Task R6_a_variable_binds_per_element_of_an_in_array()
    {
        var bound = await BindHost.BoundAsync(Model, Order,
            """[{ "match": { "number": { "in": ["A", { "$var": "second" }, { "$var": "third" }] } } }]""",
            variablesJson: """{ "second": "B", "third": "C" }""");

        Leaf(bound).Operand.Should().BeOfType<BoundOperand.Set>()
            .Which.Values.Should().Equal(new BsonString("A"), new BsonString("B"), new BsonString("C"));

        // The whole-array form is unchanged.
        var whole = await BindHost.BoundAsync(Model, Order,
            """[{ "match": { "number": { "in": { "$var": "all" } } } }]""",
            variablesJson: """{ "all": ["A", "B"] }""");

        Leaf(whole).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().HaveCount(2);
    }

    /// <summary>R6. An element naming a variable the request does not bind is a coded refusal.</summary>
    [Fact]
    public async Task R6_an_unbound_variable_in_an_in_array_is_refused() =>
        (await BindHost.ErrorAsync(Model, Order, """[{ "match": { "number": { "in": ["A", { "$var": "missing" }] } } }]""", Codes.UnboundVariable))
            .Message.Should().Contain("missing");

    // ---- SH5 · a retired addon definition ----------------------------------------------------

    /// <summary>
    /// SH5. <c>FirstOrDefault</c> took whichever definition the source listed first and the
    /// retired check behind it then refused the path, never looking for the live definition
    /// behind it. Retire-and-recreate is the ordinary life of an addon key and both rows
    /// survive, so the same key <c>/schema/addons</c> publishes as queryable answered
    /// <c>NOT_FILTERABLE</c> — measured on one of three lab services and not the other two.
    /// </summary>
    [Fact]
    public async Task SH5_a_live_addon_definition_wins_over_a_retired_one_of_the_same_path()
    {
        var retiredFirst = new TwoDefinitions("weight", retiredFirst: true);
        var liveFirst = new TwoDefinitions("weight", retiredFirst: false);

        foreach (var source in new[] { retiredFirst, liveFirst })
        {
            var bound = await Bound("""[{ "match": { "addon.weight": { "gte": 1000 } } }]""", BindHost.Context(addons: source));
            var leaf = Leaf(bound);

            leaf.Path.Kind.Should().Be(Kind.Decimal, "the live definition decides the kind, whichever order the source lists them in");
            leaf.Path.Filterable.Should().BeTrue();
        }
    }

    /// <summary>SH5. A key whose only definition is retired stays projectable-only.</summary>
    [Fact]
    public async Task SH5_a_key_with_only_a_retired_definition_is_still_not_filterable() =>
        (await Error("""[{ "match": { "addon.weight": { "eq": 1 } } }]""", Codes.NotFilterable, BindHost.Context(addons: new OneRetired("weight"))))
            .Path.Should().Be("addon.weight");

    // ---- F-ENT-003 · the remote resolve default select ---------------------------------------

    /// <summary>
    /// F-ENT-003. A remote resolve with no <c>select</c> sent the owner no projection and got
    /// whole documents — ~2 KB a row, <c>organizationId</c> included — where the local half of
    /// the same stage returns the target's key and display. The caller cannot name the owner's
    /// display member, so the reserved <c>$default</c> key asks the owner to apply the pair
    /// itself, and the two halves answer the same question.
    /// </summary>
    [Fact]
    public async Task F_ENT_003_the_default_select_key_expands_to_the_entitys_key_and_display()
    {
        var bound = await Bound("""[{ "project": { "$default": 1 } }]""");
        var project = (BoundStage.Project)bound.Stages[0];

        project.Inclusion.Should().BeTrue();
        project.Paths.Select(path => path.Wire).Should().BeEquivalentTo("id", "number");
    }

    /// <summary>F-ENT-003. It is the entity's own pair, so it means nothing after a group.</summary>
    [Fact]
    public async Task F_ENT_003_the_default_select_key_is_refused_where_the_shape_is_not_the_entitys() =>
        (await Error("""[{ "group": { "by": [{ "path": "state", "as": "s" }], "fields": {} } }, { "project": { "$default": 1 } }]""", Codes.UnknownPath))
            .Path.Should().Be("$default");

    // ---- fixtures ----------------------------------------------------------------------------

    /// <summary>One path defined twice, retired and live, in the order the test asks for.</summary>
    private sealed class TwoDefinitions(string path, bool retiredFirst) : IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
        {
            var retired = new AddonDefinition { Id = Guid.NewGuid(), Entity = entity, Path = path, Kind = AddonKind.Decimal, Retired = true };
            var live = new AddonDefinition { Id = Guid.NewGuid(), Entity = entity, Path = path, Kind = AddonKind.Decimal };

            return ValueTask.FromResult<IReadOnlyList<AddonDefinition>>(retiredFirst ? [retired, live] : [live, retired]);
        }
    }

    private sealed class OneRetired(string path) : IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AddonDefinition>>([new AddonDefinition { Id = Guid.NewGuid(), Entity = entity, Path = path, Kind = AddonKind.Decimal, Retired = true }]);
    }
}
