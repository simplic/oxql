using System.Security.Cryptography;
using System.Text;

namespace OxQL.Model.Build;

/// <summary>A reference a builder found on a member, resolved against the finished entity set.</summary>
internal sealed record PendingReference(MemberDef Member, string OwnerLabel, string TargetEntity, string? TargetField, ReferenceSource Source);

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

        foreach (var pending in references)
        {
            var target = WireNames.NormalizeEntityId(pending.TargetEntity);

            if (entities.TryGetValue(target, out var entity))
            {
                pending.Member.Reference = new ReferenceDef
                {
                    TargetEntity = target,
                    TargetField = pending.TargetField ?? entity.Key?.Wire ?? WireNames.IdWire,
                    DeclaredBy = pending.Source,
                    IsRemote = false,
                };

                continue;
            }

            var targetNamespace = target.Split('.')[0];

            if (namespaces.Contains(targetNamespace))
            {
                findings.Add(new BuildFinding(
                    BuildCodes.ReferenceTargetUnknown,
                    pending.OwnerLabel,
                    $"The reference names '{target}', which is not an entity of this host, so no reference is emitted."));

                continue;
            }

            // A local target's key is right there to read, and the local branch above reads
            // it. A remote target's is not, and defaulting to "id" is a guess: asked for the
            // ids of an owner keyed on something else, the owner answers rows keyed by a
            // member that happens to be called `id` and every row of the join is wrong, with
            // a 200 and no diagnostic. A declaration this host cannot complete is a finding,
            // and no reference is emitted, so the resolve is refused instead of answered
            // wrongly. The fix is one argument on the attribute: [OxQLReference(target, field)].
            if (pending.TargetField is null)
            {
                findings.Add(new BuildFinding(
                    BuildCodes.ReferenceTargetFieldUnknown,
                    pending.OwnerLabel,
                    $"The reference names '{target}' in another service and no target field, and this host cannot read that entity's key, so no reference is emitted. Declare the field the reference points at.",
                    target));

                continue;
            }

            pending.Member.Reference = new ReferenceDef
            {
                TargetEntity = target,
                TargetField = pending.TargetField,
                DeclaredBy = pending.Source,
                IsRemote = true,
            };
        }
    }

    /// <summary>
    /// A SHA-256 over one line per entity, path and pooled type; the same model text-serialises to
    /// the same lines. A path's <c>onlyFor</c> and a type's variants and discriminator are appended
    /// only where present, so a model without polymorphism keeps the fingerprint it had before
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
