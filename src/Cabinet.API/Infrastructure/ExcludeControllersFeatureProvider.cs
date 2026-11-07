using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Removes controllers belonging to an extension that is turned off, so their routes don't exist.
/// </summary>
public sealed class ExcludeControllersFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private readonly HashSet<TypeInfo> _excluded;

    public ExcludeControllersFeatureProvider(params Type[] controllers)
    {
        _excluded = controllers.Select(controller => controller.GetTypeInfo()).ToHashSet();
    }

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in _excluded)
        {
            feature.Controllers.Remove(controller);
        }
    }
}
