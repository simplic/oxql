using MongoDB.Bson;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// One row of the corpus: its entity, organisation, ordinal and key, what it is for, the model
/// object it was built from, and the deliberate storage overrides written on top of what the
/// driver makes of that object.
/// <para>
/// <see cref="Stored"/> is the document exactly as the seeder writes it, and it is what every
/// predicate reads. <see cref="Model"/> is only the starting point: where an override removes a
/// member or stores it in another form (a decimal as a string, say), the model object still
/// holds the typed value, so read such a member with <see cref="Corpus.ValueAt"/>, never off the
/// model. <see cref="Raw"/> names every such member.
/// </para>
/// </summary>
public abstract class CorpusRow
{
    private readonly Lazy<BsonDocument> stored;

    protected CorpusRow(string entityId, Org org, int n, string key, string purpose, RawStorage raw)
    {
        EntityId = entityId;
        Org = org;
        N = n;
        Key = key;
        Purpose = purpose;
        Raw = raw;
        stored = new Lazy<BsonDocument>(Store, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string EntityId { get; }

    public Org Org { get; }

    /// <summary>The ordinal: the last twelve digits of <see cref="Id"/>.</summary>
    public int N { get; }

    /// <summary>The row's name, unique per entity across both organisations: what a test asks for.</summary>
    public string Key { get; }

    /// <summary>Why the row exists: the storage state or edge it carries.</summary>
    public string Purpose { get; }

    /// <summary>The row's key as a Guid; <see cref="Guid.Empty"/> for an entity keyed on something else (<c>conformance.ref</c>).</summary>
    public abstract Guid Id { get; }

    /// <summary>The key as the wire spells it: the Guid's lower-case form, or the stored key of an entity keyed on a string.</summary>
    public abstract string WireId { get; }

    /// <summary>The model object the row was built from.</summary>
    public abstract object Model { get; }

    /// <summary>The CLR type of <see cref="Model"/>: the entity class.</summary>
    public abstract Type ModelType { get; }

    /// <summary>The deliberate storage forms of this row: members left out or written raw.</summary>
    public RawStorage Raw { get; }

    /// <summary>The document as the seeder writes it: the driver's serialization of the model, then the raw overrides.</summary>
    public BsonDocument Stored => stored.Value;

    /// <summary>Whether a raw override touches <paramref name="wirePath"/> or a member above or below it.</summary>
    public bool IsRaw(string wirePath) => Raw.Touches(wirePath);

    protected abstract BsonDocument Serialize();

    private BsonDocument Store()
    {
        var document = Serialize();

        Raw.ApplyTo(document, ModelType);

        return document;
    }

    public override string ToString() => $"{EntityId}/{Key} ({WireId})";
}

/// <summary>A row whose model object is typed.</summary>
public sealed class CorpusRow<T> : CorpusRow where T : class
{
    public CorpusRow(string entityId, Org org, int n, string key, string purpose, T model, Guid id, RawStorage raw, string? wireId = null)
        : base(entityId, org, n, key, purpose, raw)
    {
        Doc = model;
        Id = id;
        WireId = wireId ?? id.ToString("D");
    }

    /// <summary>The typed model object. A member <see cref="CorpusRow.Raw"/> names is not what storage holds.</summary>
    public T Doc { get; }

    public override Guid Id { get; }

    public override string WireId { get; }

    public override object Model => Doc;

    public override Type ModelType => typeof(T);

    protected override BsonDocument Serialize() => Doc.ToBsonDocument(typeof(T));
}

/// <summary>
/// The deliberate storage forms of one row, written over the driver's serialization of its model:
/// a member left out (missing, which is not null), or a member stored in a form the model type
/// cannot produce (a decimal as a string, a value no enum member names, a decimal beyond
/// <see cref="decimal"/>'s range). Addressed by wire path; a path through a collection applies to
/// every element. Every entry carries the reason, so a reader of a row knows the storage state is
/// on purpose.
/// </summary>
public sealed class RawStorage
{
    private readonly List<Entry> entries = [];

    /// <summary>A row with no raw storage.</summary>
    public static RawStorage None => new();

    public IReadOnlyList<Entry> Entries => entries;

    /// <summary>Leaves the member at <paramref name="wirePath"/> out of storage.</summary>
    public RawStorage Unset(string wirePath, string why)
    {
        entries.Add(new Entry(wirePath, null, why));
        return this;
    }

    /// <summary>Stores <paramref name="value"/> at <paramref name="wirePath"/>, whatever the model says.</summary>
    public RawStorage Set(string wirePath, BsonValue value, string why)
    {
        entries.Add(new Entry(wirePath, value, why));
        return this;
    }

    /// <summary>Stores a decimal as its string form: a row written before the driver stored decimals as Decimal128.</summary>
    public RawStorage DecimalAsString(string wirePath, string text) =>
        Set(wirePath, new BsonString(text), "a decimal stored as a string, as rows written before Decimal128 hold it");

    public bool Touches(string wirePath) => entries.Any(entry =>
        entry.WirePath == wirePath ||
        wirePath.StartsWith(entry.WirePath + ".", StringComparison.Ordinal) ||
        entry.WirePath.StartsWith(wirePath + ".", StringComparison.Ordinal));

    internal void ApplyTo(BsonDocument document, Type root)
    {
        foreach (var entry in entries)
        {
            if (entry.Value is null)
                StoragePath.Unset(document, root, entry.WirePath);
            else
                StoragePath.Set(document, root, entry.WirePath, entry.Value);
        }
    }

    /// <summary>One override: <see cref="Value"/> null means the member is left out.</summary>
    public sealed record Entry(string WirePath, BsonValue? Value, string Why);
}
