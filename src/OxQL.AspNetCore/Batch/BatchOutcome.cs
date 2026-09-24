using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Batch;

/// <summary>The outcome of a batch: one result per query, or a refusal of the batch as a whole (too many queries).</summary>
public abstract record BatchOutcome
{
    /// <summary>Every query answered, in order.</summary>
    public sealed record Success(BatchResponse Response) : BatchOutcome;

    /// <summary>The batch was refused whole.</summary>
    public sealed record Refused(Refusal Refusal) : BatchOutcome;
}
