using System.Security.Cryptography;
using System.Text;

namespace OxQL.Model.Build;

/// <summary>One target of a pending reference case as declared: the entity id, the item path and the field, unresolved.</summary>
internal sealed record PendingTarget(string Entity, string? Field, string? Item)
{
    /// <summary>A target spelled <c>entity</c> or <c>entity#itemPath</c>.</summary>
    public static PendingTarget Parse(string spelled, string? field)
    {
        var hash = spelled.IndexOf('#', StringComparison.Ordinal);

        return hash < 0
            ? new PendingTarget(spelled.Trim(), field, null)
            : new PendingTarget(spelled[..hash].Trim(), field, NullIfEmpty(spelled[(hash + 1)..]));
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>
/// One reference case a builder found on a member, resolved against the finished entity set.
/// The cases of one member are pending in declaration order.
/// </summary>
internal sealed record PendingReference(
    MemberDef Member,
    string OwnerLabel,
    IReadOnlyList<PendingTarget> Targets,
    ReferenceSource Source,
    ReferenceCondition? When = null,
    KeyAs KeyAs = KeyAs.None)
{
    /// <summary>An unconditional one-target case: <c>[OxQLReference]</c>, <c>[ReferenceId]</c> or a document's <c>references</c>.</summary>
    public PendingReference(MemberDef member, string ownerLabel, string targetEntity, string? targetField, ReferenceSource source, string? item = null, KeyAs keyAs = KeyAs.None)
        : this(member, ownerLabel, [new PendingTarget(targetEntity, targetField, item)], source, null, keyAs)
    {
    }

    /// <summary>Whether the case is what a 1.0 declaration could say: unconditional, one target.</summary>
    public bool Plain => When is null && Targets.Count == 1;
}

/// <summary>
/// The steps both builders share once the type graph is built: the path index per entity, the
/// key and display paths, the retired-id table, reference resolution and the fingerprint.
/// </summary>
internal static class ModelAssembler
{
    public static EntityModel Finish(
        IReadOnlyList<EntityDef> entities,
        IEnumerable<TypeDef> pooledTypes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> retiredByCurrent,
        IReadOnlyList<PendingReference> references,
        List<BuildFinding> findings)
    {
        var entityIndex = new SortedDictionary<string, EntityDef>(StringComparer.Ordinal);

        foreach (var entity in entities)
            entityIndex[entity.Id] = entity;

        var pool = new SortedDictionary<string, TypeDef>(StringComparer.Ordinal);

        foreach (var type in pooledTypes)
            pool[type.PoolId] = type;

        foreach (var entity in entities)
        {
            PathIndexer.Index(entity, findings);

            entity.Key = entity.Paths.FirstOrDefault(path => path.Depth == 0 && path.Storage == WireNames.IdStorage);
            entity.Display = DisplayOf(entity);

            if (entity.Key is null)
                findings.Add(new BuildFinding(
                    BuildCodes.EntityKeyMissing,
                    entity.Id,
                    "No root member is stored under '_id', so the entity has no key."));
        }

        var retired = RetiredIds(entityIndex, retiredByCurrent, findings);

        foreach (var entity in entities)
            entity.RetiredIds = retired
                .Where(pair => pair.Value == entity.Id)
                .Select(pair => pair.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

        ResolveReferences(entityIndex, references, findings);

        return new EntityModel(
            entityIndex,
            retired,
            pool,
            findings,
            DateTimeOffset.UtcNow,
            Fingerprint(entityIndex.Values, pool.Values));
    }

    /// <summary>The first display candidate the entity has as a root string member, or null.</summary>
    private static PathDef? DisplayOf(EntityDef entity)
    {
        foreach (var candidate in WireNames.DisplayCandidates)
            if (entity.PathIndex.TryGetValue(candidate, out var path) && path.Depth == 0 && path.Kind == Kind.String)
                return path;

        return null;
    }

    private static Dictionary<string, string> RetiredIds(
        IReadOnlyDictionary<string, EntityDef> entities,
        IReadOnlyDictionary<string, IReadOnlyList<string>> retiredByCurrent,
        List<BuildFinding> findings)
    {
        var retired = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (declaredCurrent, ids) in retiredByCurrent.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var current = WireNames.NormalizeEntityId(declaredCurrent);

            if (!entities.ContainsKey(current))
                continue;

            foreach (var declaredId in ids)
            {
                var id = WireNames.NormalizeEntityId(declaredId);

                if (entities.ContainsKey(id))
                {
                    findings.Add(new BuildFinding(
                        BuildCodes.RetiredIdAmbiguous,
                        id,
                        $"The id is retired in favour of '{current}' but is also a live entity, so it stays the live one."));

                    continue;
                }

                if (retired.TryGetValue(id, out var other) && other != current)
                {
                    findings.Add(new BuildFinding(
                        BuildCodes.RetiredIdAmbiguous,
                        id,
                        $"Both '{other}' and '{current}' claim this retired id, so it resolves to neither."));

                    ambiguous.Add(id);
                    continue;
                }

                retired[id] = current;
            }
        }

        foreach (var id in ambiguous)
            retired.Remove(id);

        return retired;
    }

    private static void ResolveReferences(
        IReadOnlyDictionary<string, EntityDef> entities,
        IReadOnlyList<PendingReference> references,
        List<BuildFinding> findings)
    {
        var namespaces = new HashSet<string>(entities.Values.Select(entity => entity.Namespace), StringComparer.Ordinal);
        var resolved = new Dictionary<MemberDef, List<ReferenceDef>>(ReferenceEqualityComparer.Instance);
        var order = new List<MemberDef>();

        foreach (var pending in references)
        {
            if (!resolved.TryGetValue(pending.Member, out var cases))
            {
                resolved[pending.Member] = cases = [];
                order.Add(pending.Member);
            }

            var targets = new List<ReferenceTarget>(pending.Targets.Count);

            foreach (var declared in pending.Targets)
            {
                if (ResolveTarget(entities, namespaces, pending, declared, findings) is not { } target)
                {
                    // One target the host cannot complete drops the whole case: a union that
                    // silently lost a member answers some rows wrongly with a 200.
                    targets = null;
                    break;
                }

                targets.Add(target);
            }

            if (targets is not null)
                cases.Add(new ReferenceDef
                {
                    Targets = targets,
                    When = pending.When,
                    KeyAs = pending.KeyAs,
                    DeclaredBy = pending.Source,
                });
        }

        foreach (var member in order)
        {
            var cases = resolved[member];

            member.References = cases;
            member.Reference = cases is [{ IsSimple: true } simple] ? simple : null;
        }
    }

    /// <summary>
    /// One declared target resolved against the entity set: the local entity (and its keyed item
    /// collection) or a remote one, with the field defaulted to the key where this host can read
    /// it; null with a finding when the host cannot complete it.
    /// </summary>
    private static ReferenceTarget? ResolveTarget(
        IReadOnlyDictionary<string, EntityDef> entities,
        HashSet<string> namespaces,
        PendingReference pending,
        PendingTarget declared,
        List<BuildFinding> findings)
    {
        var target = WireNames.NormalizeEntityId(declared.Entity);
        var memberKind = pending.Member.LeafKind;

        if (entities.TryGetValue(target, out var entity))
        {
            string field;
            bool fieldIsKey;
            PathDef? fieldPath;

            if (declared.Item is null)
            {
                field = declared.Field ?? entity.Key?.Wire ?? WireNames.IdWire;
                fieldIsKey = entity.Key is not null && entity.Key.Wire == field;
                fieldPath = entity.Path(field);
            }
            else
            {
                var item = entity.Path(declared.Item);
                var element = item is { Kind: Kind.Array, Shape.Of: { Kind: Kind.Object, Type: { } elementType } } ? elementType : null;
                var key = element?.Members.FirstOrDefault(member => member.StorageName == WireNames.IdStorage);

                if (key is null)
                {
                    findings.Add(new BuildFinding(
                        BuildCodes.ReferenceItemUnknown,
                        pending.OwnerLabel,
                        $"The reference names the items '{declared.Item}' of '{target}', which is not a collection of keyed objects there, so the case is dropped.",
                        $"{target}#{declared.Item}"));

                    return null;
                }

                field = declared.Field ?? key.WireName;
                fieldIsKey = field == key.WireName;
                fieldPath = entity.Path($"{declared.Item}.{field}");

                if (fieldPath is null)
                {
                    findings.Add(new BuildFinding(
                        BuildCodes.ReferenceItemUnknown,
                        pending.OwnerLabel,
                        $"The items '{declared.Item}' of '{target}' have no member '{field}', so the case is dropped.",
                        $"{target}#{declared.Item}"));

                    return null;
                }
            }

            if (KeyKindMismatch(pending, memberKind, fieldPath?.LeafKind) is { } mismatch)
            {
                findings.Add(new BuildFinding(BuildCodes.ReferenceKeyKindMismatch, pending.OwnerLabel, mismatch, $"{target}#{field}"));
                return null;
            }

            return new ReferenceTarget(target, field, declared.Item, IsRemote: false, fieldIsKey);
        }

        if (namespaces.Contains(target.Split('.')[0]))
        {
            findings.Add(pending.Plain && declared.Item is null
                ? new BuildFinding(
                    BuildCodes.ReferenceTargetUnknown,
                    pending.OwnerLabel,
                    $"The reference names '{target}', which is not an entity of this host, so no reference is emitted.")
                : new BuildFinding(
                    BuildCodes.ReferenceCaseTargetUnknown,
                    pending.OwnerLabel,
                    $"A case of the reference names '{target}', which is not an entity of this host, so the case is dropped.",
                    target));

            return null;
        }

        // A local target's key is right there to read, and the local branch above reads
        // it. A remote target's is not, and defaulting to "id" is a guess: asked for the
        // ids of an owner keyed on something else, the owner answers rows keyed by a
        // member that happens to be called `id` and every row of the join is wrong, with
        // a 200 and no diagnostic. A declaration this host cannot complete is a finding,
        // and no reference is emitted, so the resolve is refused instead of answered
        // wrongly. The fix is one argument on the attribute: [OxQLReference(target, field)].
        if (declared.Field is null)
        {
            findings.Add(new BuildFinding(
                BuildCodes.ReferenceTargetFieldUnknown,
                pending.OwnerLabel,
                $"The reference names '{target}' in another service and no target field, and this host cannot read that entity's key, so no reference is emitted. Declare the field the reference points at.",
                target));

            return null;
        }

        if (KeyKindMismatch(pending, memberKind, targetKind: null) is { } remoteMismatch)
        {
            findings.Add(new BuildFinding(BuildCodes.ReferenceKeyKindMismatch, pending.OwnerLabel, remoteMismatch, $"{target}#{declared.Field}"));
            return null;
        }

        // A remote target's key is `id` by the fleet's convention; this host cannot read it.
        return new ReferenceTarget(target, declared.Field, declared.Item, IsRemote: true, FieldIsKey: declared.Field == WireNames.IdWire);
    }

    /// <summary>
    /// Why the member's stored kind cannot match the target field, or null: a guid conversion
    /// needs a string member (and a guid field where the field is known); a string member naming
    /// a known guid field needs the conversion. A remote field's kind is not known here.
    /// </summary>
    private static string? KeyKindMismatch(PendingReference pending, Kind memberKind, Kind? targetKind)
    {
        if (pending.KeyAs == KeyAs.Guid)
        {
            if (memberKind != Kind.String)
                return $"KeyAs = Guid converts a string member, and the member is {Kinds.NameOf(memberKind)}, so the case is dropped.";

            if (targetKind is { } kind && kind != Kind.Guid)
                return $"KeyAs = Guid names a guid field, and the target field is {Kinds.NameOf(kind)}, so the case is dropped.";

            return null;
        }

        return memberKind == Kind.String && targetKind == Kind.Guid
            ? "A string member cannot name a guid field as stored; declare KeyAs = Guid when it holds a guid. The case is dropped."
            : null;
    }

    /// <summary>
    /// A SHA-256 over one line per entity, path and pooled type; the same model text-serialises to
    /// the same lines. A path's <c>onlyFor</c>, its reference cases other than a simple reference, and a
    /// type's variants and discriminator are appended only where present, so a model without them keeps the fingerprint it had before
    /// they existed. Descriptions stay out.
    /// </summary>
    private static string Fingerprint(IEnumerable<EntityDef> entities, IEnumerable<TypeDef> pool)
    {
        var text = new StringBuilder();

        foreach (var entity in entities)
        {
            text.Append("entity\t").Append(entity.Id).Append('\t').Append(entity.Collection).Append('\t')
                .Append(entity.Database).Append('\t').Append(entity.Extendable).Append('\t')
                .Append(entity.Key?.Wire).Append('\t').Append(entity.Display?.Wire).Append('\t')
                .Append(string.Join(",", entity.RetiredIds)).Append('\n');

            foreach (var path in entity.Paths)
            {
                text.Append("path\t").Append(path.Wire).Append('\t').Append(path.Storage).Append('\t')
                    .Append(Kinds.NameOf(path.Kind)).Append('\t').Append(Kinds.NameOf(path.LeafKind)).Append('\t')
                    .Append(path.Shape.Leaf.Representation).Append('\t').Append(path.Member.Nullable).Append('\t')
                    .Append(path.Depth).Append('\t').Append(path.CollectionAncestors).Append('\t')
                    .Append(path.Filterable).Append('\t').Append(path.Sortable).Append('\t')
                    .Append(path.Shape.Type?.PoolId).Append('\t')
                    .Append(path.Reference is { } reference ? $"{reference.TargetEntity}#{reference.TargetField}#{reference.IsRemote}" : "");

                if (path.Member.OnlyFor is { } onlyFor)
                    text.Append("\tonlyFor=").Append(string.Join(",", onlyFor));

                if (path.Member.Reference is null && path.Member.References.Count > 0)
                    text.Append("\treferenceCases=").Append(string.Join(";", path.Member.References));

                text.Append('\n');
            }
        }

        foreach (var type in pool)
        {
            text.Append("type\t").Append(type.PoolId).Append('\t').Append(type.IsEnum).Append('\t').Append(type.EnumFlags);

            if (type.Variants.Count > 0)
                text.Append("\tdiscriminator=").Append(type.DiscriminatorElement).Append('/').Append(type.DiscriminatorForm)
                    .Append("\tvariants=").Append(string.Join(",", type.Variants.Select(variant => $"{variant.Name}:{variant.Discriminator}:{variant.Type.PoolId}")));

            text.Append('\n');

            foreach (var value in type.EnumValues)
                text.Append("enum\t").Append(value.Name).Append('\t').Append(value.Value).Append('\t').Append(value.Active).Append('\n');
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
