using System.Globalization;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>What every row builder shares: the lab user, UTC instants, and the row factory.</summary>
internal static class RowKit
{
    /// <summary>The lab user id, for the audit members where nothing else is meant.</summary>
    public static readonly Guid LabUser = LabIdentity.User;

    /// <summary>A UTC instant from an ISO string.</summary>
    public static DateTime Dt(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// One row: the model the base builder makes for the organisation, the changes the row
    /// makes to it, and the raw storage the base and the row ask for.
    /// </summary>
    public static CorpusRow<T> Row<T>(
        string entityId, string space, Org org, int n, string key, string purpose,
        Func<Org, (T Model, RawStorage Raw)> baseOf, Action<T, RawStorage>? change = null) where T : class
    {
        var (model, raw) = baseOf(org);
        change?.Invoke(model, raw);

        var id = Ids.Of(space, org, n);
        Stamp(model, id, org);

        return new CorpusRow<T>(entityId, org, n, key, purpose, model, id, raw);
    }

    /// <summary>The seeder owns the identity and the scope: the row's id and organisation are set here, never by a builder.</summary>
    private static void Stamp(object model, Guid id, Org org)
    {
        var type = model.GetType();
        type.GetProperty("Id")?.SetValue(model, id);
        type.GetProperty("OrganizationId")?.SetValue(model, org.Id());
    }
}
