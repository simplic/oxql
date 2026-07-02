using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using OxQL.AspNetCore.Controllers;

namespace OxQL.AspNetCore.Authorization;

/// <summary>
/// Application-model convention that optionally enforces authorization on the
/// <see cref="OxQLController"/> based on <see cref="OxQLEndpointOptions"/>.
/// <para>
/// This keeps authorization <i>configurable</i>: the controller itself carries no
/// <c>[Authorize]</c> attribute, and protection is added at startup only when
/// <see cref="OxQLEndpointOptions.RequireAuthorization"/> is enabled — optionally with a
/// named <see cref="OxQLEndpointOptions.AuthorizationPolicy"/>. Actions decorated with
/// <c>[AllowAnonymous]</c> (such as the health probe) are left untouched.
/// </para>
/// </summary>
internal sealed class OxQLAuthorizationConvention : IControllerModelConvention
{
    private readonly OxQLEndpointOptions _options;

    public OxQLAuthorizationConvention(OxQLEndpointOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Apply(ControllerModel controller)
    {
        // Only touch the OxQL controller, and only when protection is requested.
        if (controller.ControllerType != typeof(OxQLController))
            return;

        if (!_options.RequireAuthorization)
            return;

        var authorizeData = string.IsNullOrWhiteSpace(_options.AuthorizationPolicy)
            ? new AuthorizeAttribute()
            : new AuthorizeAttribute { Policy = _options.AuthorizationPolicy };

        // Apply at the controller level; [AllowAnonymous] on individual actions still wins.
        controller.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter([authorizeData]));
    }
}
