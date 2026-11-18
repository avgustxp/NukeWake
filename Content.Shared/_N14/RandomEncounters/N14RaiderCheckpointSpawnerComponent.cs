using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// A map-editor marker that spawns a single raider checkpoint mob (boss or garrison
/// member) at its position when the map initializes, then deletes itself. Lets the
/// mapper lay out the garrison with markers instead of raw NPC entities.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderCheckpointSpawnerComponent : Component
{
    /// <summary>Checkpoint NPC prototype to spawn (e.g. N14MobRaiderBossCheckpoint).</summary>
    [DataField("prototype")]
    public string Prototype = string.Empty;
}