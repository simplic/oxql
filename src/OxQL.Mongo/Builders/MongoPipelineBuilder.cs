using OxQL.Core.Models;
using MongoDB.Bson;

namespace OxQL.Mongo.Builders;

/// <summary>
/// Builds a complete MongoDB aggregation pipeline from a QueryPlan.
/// </summary>
public sealed class MongoPipelineBuilder
{
    private readonly QueryVariables? _variables;
    private readonly MongoFilterBuilder _filterBuilder;
    private readonly MongoExpressionBuilder _expressionBuilder;
    private readonly MongoCursorPagingBuilder _cursorBuilder;

    public MongoPipelineBuilder(QueryVariables? variables)
    {
        _variables = variables;
        _filterBuilder = new MongoFilterBuilder(variables);
        _expressionBuilder = new MongoExpressionBuilder(variables);
        _cursorBuilder = new MongoCursorPagingBuilder();
    }

    /// <summary>
    /// Builds the MongoDB aggregation pipeline stages.
    /// </summary>
    public List<BsonDocument> Build(QueryPlan plan, CursorPayload? cursor = null)
    {
        var pipeline = new List<BsonDocument>();

        foreach (var stage in plan.Pipeline)
        {
            if (stage.Match is not null)
                pipeline.Add(BuildMatch(stage.Match));

            if (stage.Lookup is not null)
                pipeline.Add(BuildLookup(stage.Lookup));

            if (stage.Unwind is not null)
                pipeline.AddRange(BuildUnwind(stage.Unwind));

            if (stage.Group is not null)
                pipeline.AddRange(BuildGroup(stage.Group));

            if (stage.Project is not null)
                pipeline.Add(BuildProject(stage.Project));

            if (stage.Sort is not null)
                pipeline.Add(BuildSort(stage.Sort));

            if (stage.Page is not null)
            {
                if (cursor is not null)
                    pipeline.Add(new BsonDocument("$match", _cursorBuilder.BuildCursorFilter(cursor)));

                pipeline.Add(new BsonDocument("$limit", stage.Page.Limit + 1)); // +1 to detect hasNextPage
            }
        }

        return pipeline;
    }

    /// <summary>
    /// Builds a count pipeline (uses same match/lookup/unwind/group stages but ends with $count).
    /// </summary>
    public List<BsonDocument> BuildCountPipeline(QueryPlan plan)
    {
        var pipeline = new List<BsonDocument>();

        foreach (var stage in plan.Pipeline)
        {
            if (stage.Match is not null)
                pipeline.Add(BuildMatch(stage.Match));

            if (stage.Lookup is not null)
                pipeline.Add(BuildLookup(stage.Lookup));

            if (stage.Unwind is not null)
                pipeline.AddRange(BuildUnwind(stage.Unwind));

            if (stage.Group is not null)
                pipeline.AddRange(BuildGroup(stage.Group));

            // Skip project, sort, and page for count
        }

        pipeline.Add(new BsonDocument("$count", "totalCount"));
        return pipeline;
    }

    private BsonDocument BuildMatch(MatchStage match)
    {
        return new BsonDocument("$match", _filterBuilder.Build(match));
    }

    private static BsonDocument BuildLookup(LookupStage lookup)
    {
        return new BsonDocument("$lookup", new BsonDocument
        {
            ["from"] = lookup.From,
            ["localField"] = TranslatePath(lookup.LocalPath),
            ["foreignField"] = TranslatePath(lookup.ForeignPath),
            ["as"] = lookup.As
        });
    }

    private static List<BsonDocument> BuildUnwind(UnwindStage unwind)
    {
        var stages = new List<BsonDocument>();

        var unwindDoc = new BsonDocument
        {
            ["path"] = $"${TranslatePath(unwind.Path)}",
            ["preserveNullAndEmptyArrays"] = unwind.PreserveNull
        };

        if (unwind.IncludeIndex is not null)
        {
            unwindDoc["includeArrayIndex"] = unwind.IncludeIndex;
        }

        stages.Add(new BsonDocument("$unwind", unwindDoc));

        // If "as" alias is specified, add a $set stage to alias the unwound element
        if (unwind.As is not null)
        {
            stages.Add(new BsonDocument("$set", new BsonDocument(
                unwind.As, $"${TranslatePath(unwind.Path)}")));
        }

        return stages;
    }

    private List<BsonDocument> BuildGroup(GroupStage group)
    {
        var stages = new List<BsonDocument>();
        var groupDoc = new BsonDocument
        {
            ["_id"] = _expressionBuilder.BuildGroupId(group.By)
        };

        var countDistinctFields = new List<(string name, BsonValue expr)>();

        foreach (var (fieldName, aggExpr) in group.Fields)
        {
            if (string.Equals(aggExpr.Function, "countDistinct", StringComparison.OrdinalIgnoreCase))
            {
                // Use $addToSet in group, then $size in subsequent $addFields
                groupDoc[fieldName] = _expressionBuilder.BuildAccumulator(aggExpr);
                countDistinctFields.Add((fieldName, new BsonDocument("$size", $"${fieldName}")));
            }
            else
            {
                groupDoc[fieldName] = _expressionBuilder.BuildAccumulator(aggExpr);
            }
        }

        stages.Add(new BsonDocument("$group", groupDoc));

        // Add $addFields for countDistinct to convert sets to counts
        if (countDistinctFields.Count > 0)
        {
            var addFieldsDoc = new BsonDocument();
            foreach (var (name, expr) in countDistinctFields)
            {
                addFieldsDoc[name] = expr;
            }
            stages.Add(new BsonDocument("$addFields", addFieldsDoc));
        }

        // Reshape grouped results to use the "as" aliases
        var reshapeDoc = new BsonDocument();
        foreach (var byField in group.By)
        {
            if (group.By.Count == 1)
                reshapeDoc[byField.As] = "$_id";
            else
                reshapeDoc[byField.As] = $"$_id.{byField.As}";
        }

        foreach (var (fieldName, _) in group.Fields)
        {
            reshapeDoc[fieldName] = $"${fieldName}";
        }

        reshapeDoc["_id"] = 0;
        stages.Add(new BsonDocument("$project", reshapeDoc));

        return stages;
    }

    private static BsonDocument BuildProject(ProjectStage project)
    {
        var projectDoc = new BsonDocument();
        foreach (var path in project.Include)
        {
            projectDoc[TranslatePath(path)] = 1;
        }
        return new BsonDocument("$project", projectDoc);
    }

    private static BsonDocument BuildSort(IReadOnlyList<SortField> sortFields)
    {
        var sortDoc = new BsonDocument();
        foreach (var field in sortFields)
        {
            sortDoc[TranslatePath(field.Path)] = field.IsAscending ? 1 : -1;
        }
        return new BsonDocument("$sort", sortDoc);
    }

    private static string TranslatePath(string path)
    {
        if (path == "id") return "_id";
        return path;
    }
}
