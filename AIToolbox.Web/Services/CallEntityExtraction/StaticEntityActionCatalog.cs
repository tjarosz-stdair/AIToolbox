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

    // OrderNumber/PurchaseOrderNumber/JobName are unvalidated regex guesses (no P21 round-trip) --
    // detection-only for now, so no actions until/unless real P21 validation is built for them.
    private static readonly IReadOnlyList<ActionDescriptor> NoActions = [];

    public IReadOnlyList<ActionDescriptor> GetActions(DetectedEntityType entityType)
    {
        return entityType switch
        {
            DetectedEntityType.PartNumber => PartActions,
            DetectedEntityType.OrderNumber or DetectedEntityType.PurchaseOrderNumber or DetectedEntityType.JobName => NoActions,
            _ => ModelOrSerialActions
        };
    }
}
