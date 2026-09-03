using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Model Number and Serial Number share the same three actions; Part Number drops
/// "Open in PIC Search" -- PIC is a model/equipment catalog, not a supplier-part lookup.
/// </summary>
public sealed class StaticEntityActionCatalog : IEntityActionCatalog
{
    private static readonly IReadOnlyList<ActionDescriptor> ModelOrSerialActions =
    [
        new ActionDescriptor { ActionId = "add-to-order", Label = "Add to Order", RequiresConfirmation = false },
        new ActionDescriptor { ActionId = "search-carrier-pic", Label = "Open in PIC Search", RequiresConfirmation = false },
        new ActionDescriptor { ActionId = "check-stock", Label = "Check Stock", RequiresConfirmation = false }
    ];

    private static readonly IReadOnlyList<ActionDescriptor> PartActions =
    [
        new ActionDescriptor { ActionId = "add-to-order", Label = "Add to Order", RequiresConfirmation = false },
        new ActionDescriptor { ActionId = "check-stock", Label = "Check Stock", RequiresConfirmation = false }
    ];

    public IReadOnlyList<ActionDescriptor> GetActions(DetectedEntityType entityType)
    {
        return entityType == DetectedEntityType.PartNumber ? PartActions : ModelOrSerialActions;
    }
}
