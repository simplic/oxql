using System.Globalization;
using System.Text.Json;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.AspNetCore.Compat;

/// <summary>What the compat rewrite did to a contract 1 request, for the log line and the engine.</summary>
public sealed record CompatRewrite
{
    /// <summary>The request in contract 2 spelling; the engine binds it like any other.</summary>
    public required QueryRequest Request { get; init; }

    /// <summary>The first path the caller wrote in storage or CLR spelling, or the first path carrying a type hint; null when the request needed no translation.</summary>
    public string? FirstLegacyPath { get; init; }

    /// <summary>How many paths were translated from storage or CLR spelling.</summary>
    public int LegacyPaths { get; init; }

    /// <summary>How many operands carried a v1 type hint.</summary>
    public int TypeHints { get; init; }

    /// <summary>A refusal the rewrite raised: a v1 <c>lookup</c> or <c>resolve</c> stage.</summary>
    public Refusal? Refusal { get; init; }
}

/// <summary>
/// Contract 1 compatibility (design §12): a request without the contract header, while
/// <c>Compat:Enabled</c>, may spell paths as the driver stores them (<c>MatchCode</c>,
/// <c>Status.Name</c>, <c>_id</c>) or as the CLR declares them, and may wrap operands in
/// the v1 type hints (<c>$date</c>, <c>$uuid</c>, <c>$uuid3</c>, <c>$long</c>, <c>$decimal</c>,
/// <c>$oid</c>, <c>$regex</c>, <c>$null</c>). The rewrite resolves every path relative to the
/// shape folded through the caller's stages, alias roots included, onto its wire spelling,
/// unwraps the hints into the wire encoding, and hands the engine a contract 2 request. A v1
/// <c>lookup</c> or <c>resolve</c> stage has no v2 equivalent and is refused with
/// <c>LEGACY_STAGE_UNSUPPORTED</c>. Rows of a contract 1 request are rendered by the v1
/// converter in the engine, so <c>_id</c> and the storage names come back as they did.
/// </summary>
public sealed class CompatBinder
{
    private static readonly IReadOnlyDictionary<string, string> OperatorsByLowerCase =
        OperandCoercer.Operators.ToDictionary(op => op.ToLowerInvariant(), op => op, StringComparer.Ordinal);

    private readonly EntityModel model;

    public CompatBinder(EntityModel model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>Rewrites one contract 1 request into contract 2 spelling.</summary>
    public CompatRewrite Rewrite(QueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = new Session(model, request);

        return session.Run();
    }

    /// <summary>One rewrite: the stage loop with the alias roots folded through it.</summary>
    private sealed class Session(EntityModel model, QueryRequest request)
    {
        /// <summary>The roots a path may start from: the implicit root, then every alias; null for a root with no type to translate against.</summary>
        private readonly Dictionary<string, TypeDef?> roots = new(StringComparer.Ordinal);
        private string? firstLegacyPath;
        private int legacyPaths;
        private int typeHints;

        public CompatRewrite Run()
        {
            if (!string.IsNullOrWhiteSpace(request.EntityType) && model.TryResolve(request.EntityType, out var entity, out _))
                roots[Shape.ImplicitRoot] = entity.Root;
            else
                roots[Shape.ImplicitRoot] = null;

            var pipeline = request.Pipeline ?? [];
            var rewritten = new List<PipelineStage>(pipeline.Count);

            for (var index = 0; index < pipeline.Count; index++)
            {
                var stage = pipeline[index];

                // A null stage, like a stage whose body is null below, is the caller's error and
                // not this rewrite's to judge: it travels on untouched and the binder refuses it
                // with its code.
                if (stage is null)
                {
                    rewritten.Add(stage!);
                    continue;
                }

                switch (stage.Kind)
                {
                    case "lookup":
                    case "resolve":
                        return new CompatRewrite
                        {
                            Request = request,
                            FirstLegacyPath = firstLegacyPath,
                            LegacyPaths = legacyPaths,
                            TypeHints = typeHints,
                            Refusal = Refusal.Validation([new QueryValidationError
                            {
                                Code = Codes.LegacyStageUnsupported,
                                Message = $"The v1 '{stage.Kind}' stage has no equivalent under contract 1; send the request under contract 2 (X-OxQL-Contract: 2) with the v2 '{stage.Kind}' form.",
                                Stage = index,
                            }]),
                        };

                    case "match" when stage.Match is not null:
                        rewritten.Add(stage with { Match = RewriteMatch(stage.Match) });
                        break;

                    case "unwind" when stage.Unwind is not null:
                        rewritten.Add(stage with { Unwind = RewriteUnwind(stage.Unwind) });
                        break;

                    case "group" when stage.Group is not null:
                        rewritten.Add(stage with { Group = RewriteGroup(stage.Group) });
                        break;

                    case "project" when stage.Project is not null:
                        rewritten.Add(stage with { Project = RewriteProject(stage.Project) });
                        break;

                    case "sort" when stage.Sort is not null:
                        rewritten.Add(stage with { Sort = RewriteSort(stage.Sort) });
                        break;

                    default:
                        rewritten.Add(stage);
                        break;
                }
            }

            var variables = RewriteVariables(request.Variables);

            return new CompatRewrite
            {
                Request = request with { Pipeline = rewritten, Variables = variables },
                FirstLegacyPath = firstLegacyPath,
                LegacyPaths = legacyPaths,
                TypeHints = typeHints,
            };
        }

        // ---- stages --------------------------------------------------------------------------

        private MatchStage RewriteMatch(MatchStage match) =>
            match.Condition is null ? match : match with { Condition = RewriteCondition(match.Condition, roots) };

        private FilterCondition RewriteCondition(FilterCondition condition, IReadOnlyDictionary<string, TypeDef?> at)
        {
            if (condition.And is not null)
                return condition with { And = condition.And.Select(inner => RewriteCondition(inner, at)).ToList() };

            if (condition.Or is not null)
                return condition with { Or = condition.Or.Select(inner => RewriteCondition(inner, at)).ToList() };

            if (condition.Not is not null)
                return condition with { Not = RewriteCondition(condition.Not, at) };

            if (condition.Path is null)
                return condition;

            var path = Translate(condition.Path, at);

            if (condition.Any is not null)
            {
                // The inner condition is relative to one element of the collection.
                var element = ElementType(path.Shape);
                var inner = RewriteCondition(condition.Any, new Dictionary<string, TypeDef?>(StringComparer.Ordinal) { [Shape.ImplicitRoot] = element });

                return condition with { Path = path.Wire, Any = inner };
            }

            var op = condition.Op ?? "eq";

            if (!OperandCoercer.Operators.Contains(op) && OperatorsByLowerCase.TryGetValue(op.ToLowerInvariant(), out var canonical))
                op = canonical;

            var (value, hinted, regex) = Unwrap(condition.Value);

            if (hinted)
                NoteHint(condition.Path);

            if (regex is not null)
            {
                // v1 accepted a regex as a value under any operator; the v2 form is the operator.
                op = "regex";
                value = JsonSerializer.SerializeToElement(regex);
            }

            return condition with { Path = path.Wire, Op = op, Value = value };
        }

        private UnwindStage RewriteUnwind(UnwindStage unwind)
        {
            var path = Translate(unwind.Path, roots);

            if (unwind.As is not null)
                roots[unwind.As] = ElementType(path.Shape);

            if (unwind.IncludeIndex is not null)
                roots[unwind.IncludeIndex] = null;

            return unwind with { Path = path.Wire };
        }

        private GroupStage RewriteGroup(GroupStage group)
        {
            var by = group.By.Select(key => key is null ? key! : key with
            {
                Path = key.Path is null ? null : Translate(key.Path, roots).Wire,
                DateTrunc = key.DateTrunc is null ? null : key.DateTrunc with { Path = Translate(key.DateTrunc.Path, roots).Wire },
            }).ToList();

            var fields = new Dictionary<string, AggregationExpression>(StringComparer.Ordinal);

            foreach (var (alias, aggregate) in group.Fields)
                fields[alias] = aggregate?.Argument is null ? aggregate! : aggregate with { Argument = RewriteExpression(aggregate.Argument) };

            // After the group, the roots are the outputs, verbatim.
            roots.Clear();

            foreach (var key in by)
                if (key?.As is not null)
                    roots[key.As] = null;

            foreach (var alias in fields.Keys)
                roots[alias] = null;

            return group with { By = by, Fields = fields };
        }

        private QueryExpression RewriteExpression(QueryExpression expression)
        {
            if (expression.IsPath)
                return expression with { Path = Translate(expression.Path!, roots).Wire };

            if (expression.IsArithmetic)
                return expression with { Operands = (expression.Operands ?? []).Select(RewriteExpression).ToList() };

            return expression;
        }

        private ProjectStage RewriteProject(ProjectStage project)
        {
            var fields = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (wire, value) in project.Fields)
                fields[Translate(wire, roots).Wire] = value;

            return project with { Fields = fields };
        }

        private IReadOnlyList<SortField> RewriteSort(IReadOnlyList<SortField> fields) =>
            fields.Select(field => field is null ? field! : field with
            {
                Path = Translate(field.Path, roots).Wire,
                Direction = field.Direction.ToLowerInvariant() is "asc" or "desc" ? field.Direction.ToLowerInvariant() : field.Direction,
            }).ToList();

        private QueryVariables? RewriteVariables(QueryVariables? variables)
        {
            if (variables is null || variables.Values.Count == 0)
                return variables;

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            foreach (var (name, raw) in variables.Values)
            {
                if (raw is JsonElement element)
                {
                    var (value, hinted, regex) = Unwrap(element);

                    if (hinted)
                    {
                        NoteHint("$var:" + name);
                        changed = true;
                    }

                    // A regex hint inside a variable cannot change the operator; the pattern travels as a string.
                    values[name] = regex is not null ? JsonSerializer.SerializeToElement(regex) : value;
                }
                else
                {
                    values[name] = raw;
                }
            }

            return changed ? new QueryVariables { Values = values } : variables;
        }

        // ---- paths ---------------------------------------------------------------------------

        private readonly record struct Translated(string Wire, ShapeDef? Shape);

        /// <summary>
        /// Resolves a path written in wire, storage or CLR spelling onto its wire spelling at
        /// the current roots. Segments the model does not know stay as written, so the binder
        /// refuses them with the code it would have used anyway.
        /// </summary>
        private Translated Translate(string path, IReadOnlyDictionary<string, TypeDef?> at)
        {
            if (string.IsNullOrEmpty(path))
                return new Translated(path, null);

            var segments = path.Split('.');
            var output = new string[segments.Length];
            var start = 0;
            TypeDef? type;

            if (segments[0] != Shape.ImplicitRoot && at.TryGetValue(segments[0], out var aliased))
            {
                output[0] = segments[0];
                start = 1;
                type = aliased;
            }
            else
            {
                type = at.GetValueOrDefault(Shape.ImplicitRoot);
            }

            ShapeDef? shape = null;
            var legacy = false;
            var dictionaryKeyNext = false;

            for (var index = start; index < segments.Length; index++)
            {
                var segment = segments[index];

                if (dictionaryKeyNext)
                {
                    // The key of a dictionary is the caller's literal.
                    output[index] = segment;
                    shape = shape?.Value;
                    type = Descend(shape, out dictionaryKeyNext);
                    continue;
                }

                if (type is null)
                {
                    output[index] = segment;
                    shape = null;
                    continue;
                }

                var member = type.Member(segment)
                    ?? type.Members.FirstOrDefault(candidate => candidate.Stored && string.Equals(candidate.StorageName, segment, StringComparison.Ordinal))
                    ?? type.Members.FirstOrDefault(candidate => string.Equals(candidate.ClrName, segment, StringComparison.Ordinal));

                if (member is null)
                {
                    // Unknown here: the rest stays as written.
                    for (var rest = index; rest < segments.Length; rest++)
                        output[rest] = segments[rest];

                    shape = null;
                    break;
                }

                if (!string.Equals(member.WireName, segment, StringComparison.Ordinal))
                    legacy = true;

                output[index] = member.WireName;
                shape = member;
                type = Descend(shape, out dictionaryKeyNext);
            }

            var wire = string.Join('.', output);

            if (legacy)
            {
                legacyPaths++;
                firstLegacyPath ??= path;
            }

            return new Translated(wire, shape);
        }

        /// <summary>Steps through arrays to the type the next segment is a member of; a dictionary makes the next segment a key.</summary>
        private static TypeDef? Descend(ShapeDef? shape, out bool dictionaryKeyNext)
        {
            dictionaryKeyNext = false;
            var current = shape;

            while (current is { Kind: Kind.Array, Of: not null })
                current = current.Of;

            if (current is { Kind: Kind.Dictionary })
            {
                dictionaryKeyNext = true;
                return null;
            }

            return current is { Kind: Kind.Object } ? current.Type : null;
        }

        /// <summary>The type of one element of a collection shape, or null when the elements are not objects.</summary>
        private static TypeDef? ElementType(ShapeDef? shape)
        {
            var current = shape;

            if (current is { Kind: Kind.Array, Of: not null })
                current = current.Of;
            else if (current is { Kind: Kind.Dictionary, Value: not null })
                current = current.Value;

            return current is { Kind: Kind.Object } ? current.Type : null;
        }

        // ---- operands ------------------------------------------------------------------------

        private void NoteHint(string path)
        {
            typeHints++;
            firstLegacyPath ??= path;
        }

        /// <summary>
        /// Unwraps a v1 type hint into the wire encoding the contract 2 coercer reads: the
        /// value itself for guids, longs, decimals and object ids; an ISO instant with
        /// <c>Z</c> for <c>$date</c>; null for <c>$null</c>. A <c>$regex</c> hint is returned
        /// apart, because it changes the operator. Arrays are unwrapped element by element.
        /// </summary>
        private static (JsonElement? Value, bool Hinted, string? Regex) Unwrap(JsonElement? raw)
        {
            if (raw is not { } element)
                return (raw, false, null);

            if (element.ValueKind == JsonValueKind.Array)
            {
                var items = new List<JsonElement>();
                var hinted = false;

                foreach (var item in element.EnumerateArray())
                {
                    var (value, itemHinted, regex) = Unwrap(item);

                    hinted |= itemHinted;
                    items.Add(regex is not null ? JsonSerializer.SerializeToElement(regex) : value ?? JsonSerializer.SerializeToElement((object?)null));
                }

                return hinted ? (JsonSerializer.SerializeToElement(items), true, null) : (raw, false, null);
            }

            if (element.ValueKind != JsonValueKind.Object)
                return (raw, false, null);

            string? hint = null;
            JsonElement inner = default;
            var count = 0;

            foreach (var property in element.EnumerateObject())
            {
                count++;
                hint = property.Name;
                inner = property.Value;
            }

            if (count != 1 || hint is null || hint.Length < 2 || hint[0] != '$' || hint == "$var")
                return (raw, false, null);

            switch (hint)
            {
                case "$uuid":
                case "$uuid3":
                case "$oid":
                case "$long":
                case "$decimal":
                    return (inner.ValueKind == JsonValueKind.String ? inner : JsonSerializer.SerializeToElement(inner.GetRawText()), true, null);

                case "$date":
                    return (JsonSerializer.SerializeToElement(NormaliseDate(inner)), true, null);

                case "$regex":
                    return (null, true, inner.ValueKind == JsonValueKind.String ? inner.GetString() : inner.GetRawText());

                case "$null":
                    return (JsonSerializer.SerializeToElement((object?)null), true, null);

                default:
                    return (raw, false, null);
            }
        }

        /// <summary>v1 read a date without an offset as UTC; the contract 2 coercer wants the offset spelled, so it is added here.</summary>
        private static string NormaliseDate(JsonElement inner)
        {
            var text = inner.ValueKind == JsonValueKind.String ? inner.GetString() ?? "" : inner.GetRawText();

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
                return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

            return text;
        }
    }
}
