namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// Who the fleet is queried as. Organisation B exists so tenant isolation is observable: its
/// rows mirror organisation A's, so a scope that does not fire shows up as extra rows.
/// </summary>
public static class LabIdentity
{
    public static readonly Guid OrganisationA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid OrganisationB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid User = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The header the test scope provider reads the organisation from; the name a real service forwards it under.</summary>
    public const string OrganisationHeader = "OrganizationId";

    /// <summary>The header the user id travels under.</summary>
    public const string UserHeader = "UserId";

    /// <summary>The header the correlation id travels under.</summary>
    public const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>The contract header the engine reads.</summary>
    public const string ContractHeader = OxQL.AspNetCore.OxQLQueryService.ContractHeader;
}
