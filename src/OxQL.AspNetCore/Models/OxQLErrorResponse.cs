using Microsoft.AspNetCore.Mvc;
using OxQL.Core.Engine;

namespace OxQL.AspNetCore.Models;

/// <summary>Turns a refusal into the response the host answers with: the envelope as the body, the status by class.</summary>
public static class RefusalResults
{
    /// <summary>An action result carrying the refusal envelope with its status.</summary>
    public static IActionResult ToActionResult(this Refusal refusal) => new ObjectResult(refusal) { StatusCode = refusal.Status };
}
