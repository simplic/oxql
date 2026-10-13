using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using OxQL.Core.Engine;

namespace OxQL.AspNetCore.OpenApi;

/// <summary>The request headers an OxQL route reads beside its body.</summary>
[Flags]
public enum OxQLHeaders
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>X-OxQL-Contract</c>: the contract the request is written for.</summary>
    Contract = 1,

    /// <summary><c>X-OxQL-Timing</c>: asks for the breakdown of where the request's time went.</summary>
    Timing = 2,
}

/// <summary>
/// What an OxQL action says of itself to whoever describes the API (the API explorer, and through it
/// an OpenAPI document), where the action's own signature does not: the model that describes its
/// body on the wire, the headers it reads, and the models of a contract 1 request and answer.
/// It changes nothing about how a request is bound, validated or answered.
/// <para>
/// The engine's body models are read by converters of their own (a stage is one of several
/// objects, a condition is free-form), so a description generated from them would not be the wire.
/// <see cref="Body"/> names the model that is. <see cref="OxQLApiDescriptionProvider"/>, which
/// <c>AddOxQLAspNetCore</c> registers, applies it to the action's API description and adds the
/// headers as parameters. A host reads the attribute from the endpoint's metadata for whatever
/// its own document needs beyond that (the contract 1 models as alternatives, say).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OxQLOperationAttribute : Attribute
{
    /// <summary>The model that describes the request body as it is written on the wire under contract 2; null leaves the body as it is.</summary>
    public Type? Body { get; init; }

    /// <summary>The headers the route reads, each described as a header parameter (<see cref="OxQLRequestHeaders"/>).</summary>
    public OxQLHeaders Headers { get; init; }

    /// <summary>The model of the body a caller without the contract header writes (contract 1, legacy); null when the route has none of its own.</summary>
    public Type? Contract1Body { get; init; }

    /// <summary>The model of the answer a caller without the contract header reads (contract 1, legacy); null when it is the contract 2 answer.</summary>
    public Type? Contract1Answer { get; init; }
}

/// <summary>
/// The headers of an OxQL request, as a description names them. No request is bound into this
/// model: the engine reads the headers where it needs them. It exists so that each header has a
/// name, a kind and a description an API document can carry.
/// </summary>
public sealed class OxQLRequestHeaders
{
    /// <summary>
    /// The contract the request is written for. `2` is the current contract. Absent, empty or `1`
    /// is contract 1 (legacy): another request language and another answer. Any other value is
    /// refused with 400 on the query, batch and explain routes.
    /// </summary>
    [FromHeader(Name = OxQLQueryService.ContractHeader)]
    public string? Contract { get; init; }

    /// <summary>
    /// `1` or `true` asks for the `timing` member of the answer: where the request's time went
    /// (the database commands, the owners asked, this host's own share). Contract 2 only.
    /// </summary>
    [FromHeader(Name = QueryTiming.Header)]
    public string? Timing { get; init; }
}

/// <summary>
/// Makes the API explorer describe an OxQL action as <see cref="OxQLOperationAttribute"/> says: the
/// body by the model of the wire, and the headers the route reads as header parameters. It runs
/// after the framework's own provider and touches only actions that carry the attribute; binding
/// and validation do not read API descriptions, so a request is handled as before.
/// </summary>
public sealed class OxQLApiDescriptionProvider(IModelMetadataProvider metadata) : IApiDescriptionProvider
{
    private readonly IModelMetadataProvider metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    /// <summary>After the framework's provider (-1000), which creates the descriptions this one completes.</summary>
    public int Order => 0;

    /// <inheritdoc/>
    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var description in context.Results)
        {
            if (description.ActionDescriptor.EndpointMetadata.OfType<OxQLOperationAttribute>().LastOrDefault() is not { } operation)
                continue;

            if (operation.Body is { } body)
                foreach (var parameter in description.ParameterDescriptions.Where(parameter => parameter.Source == BindingSource.Body))
                {
                    parameter.Type = body;
                    parameter.ModelMetadata = metadata.GetMetadataForType(body);
                }

            if (operation.Headers.HasFlag(OxQLHeaders.Contract))
                Add(description, OxQLQueryService.ContractHeader, nameof(OxQLRequestHeaders.Contract));

            if (operation.Headers.HasFlag(OxQLHeaders.Timing))
                Add(description, QueryTiming.Header, nameof(OxQLRequestHeaders.Timing));
        }
    }

    /// <inheritdoc/>
    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    /// <summary>An optional header parameter, described by the property of <see cref="OxQLRequestHeaders"/> that stands for it.</summary>
    private void Add(ApiDescription description, string header, string property)
    {
        if (description.ParameterDescriptions.Any(parameter => parameter.Source == BindingSource.Header && string.Equals(parameter.Name, header, StringComparison.OrdinalIgnoreCase)))
            return;

        description.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = header,
            Source = BindingSource.Header,
            Type = typeof(string),
            IsRequired = false,
            ModelMetadata = metadata.GetMetadataForProperty(typeof(OxQLRequestHeaders), property),
            ParameterDescriptor = new ParameterDescriptor
            {
                Name = header,
                ParameterType = typeof(string),
                BindingInfo = new BindingInfo { BindingSource = BindingSource.Header, BinderModelName = header },
            },
        });
    }
}
