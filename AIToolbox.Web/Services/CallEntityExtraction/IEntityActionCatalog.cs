using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// What actions to offer for a detected entity. Deliberately a placeholder seam today
/// (StaticEntityActionCatalog, a small hardcoded per-entity-type list) -- the real P21-side
/// action-capability/mutation work (screen-state reporting, order-line intake) is being designed
/// separately. Swap the implementation registered in Program.cs when that lands; this interface
/// and the outbound notification payload shape don't need to change.
/// </summary>
public interface IEntityActionCatalog
{
    IReadOnlyList<ActionDescriptor> GetActions(DetectedEntityType entityType);
}
