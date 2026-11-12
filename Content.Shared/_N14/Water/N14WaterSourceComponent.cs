using Content.Shared.Chemistry.Reagent;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._N14.Water;

/// <summary>
/// Marks an entity as an infinite source of a reagent, such as a water tile.
/// </summary>
/// <remarks>
/// This replaces a <see cref="Chemistry.Components.SolutionContainerManagerComponent"/> +
/// <see cref="Chemistry.Components.DrainableSolutionComponent"/> pair for entities that only need to hand out
/// reagents but never actually store them. Each solution container spawns an additional solution entity, so this
/// saves one entity per water tile (tens of thousands on the larger maps).
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class N14WaterSourceComponent : Component
{
    /// <summary>
    /// The reagent that this source provides.
    /// </summary>
    [DataField(required: true), AutoNetworkedField]
    public ProtoId<ReagentPrototype> Reagent = string.Empty;
}
