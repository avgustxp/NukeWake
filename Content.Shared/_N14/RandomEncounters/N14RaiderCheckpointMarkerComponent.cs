using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// An invisible gate marker of a raider checkpoint. Markers sharing a
/// <see cref="CheckpointId"/> are clustered into a gate; a player who passes
/// through that gate without being in the boss's <see cref="N14RaiderCheckpointComponent.PaidUsers"/>
/// turns the whole linked garrison hostile. If the player pays afterwards, the garrison calms down again.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderCheckpointMarkerComponent : Component
{
    /// <summary>Links this marker to a boss and its garrison.</summary>
    [DataField("checkpointId")]
    public string CheckpointId = string.Empty;
}