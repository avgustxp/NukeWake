using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// A map-editor marker that spawns raiders when the matching
/// <see cref="N14RaiderAssaultTriggerComponent"/> item is used. Unlike the
/// checkpoint spawner the marker is kept, so the same point can be attacked again.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderAssaultSpawnerComponent : Component
{
    /// <summary>
    /// Attack point this spawner belongs to (see <see cref="N14RaiderAssaultPointComponent.AttackPointId"/>).
    /// </summary>
    [DataField("attackPointId")]
    public string AttackPointId = "attack_point";

    /// <summary>Raider prototype to spawn.</summary>
    [DataField("prototype")]
    public string Prototype = string.Empty;

    /// <summary>How many raiders to spawn from this marker per raid.</summary>
    [DataField]
    public int Count = 5;
}
