using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

public sealed class StaticEntityActionCatalog : IEntityActionCatalog
{
    private static readonly IReadOnlyList<ActionDescriptor> ModelOrItemActions =
    [
        new ActionDescriptor { ActionId = "search-carrier-pic", Label = "Search Carrier PIC for this model", RequiresConfirmation = false }
    ];

    private static readonly IReadOnlyList<ActionDescriptor> SerialActions =
    [
        new ActionDescriptor { ActionId = "search-carrier-pic", Label = "Search Carrier PIC for this serial", RequiresConfirmation = false }
    ];

    public IReadOnlyList<ActionDescriptor> GetActions(IdentifierTypeGuess entityTypeGuess)
    {
        return entityTypeGuess == IdentifierTypeGuess.Serial ? SerialActions : ModelOrItemActions;
    }
}
