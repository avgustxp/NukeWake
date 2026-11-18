using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// Marks a garrison member of a raider checkpoint. All members sharing a
/// <see cref="CheckpointId"/> turn hostile to anyone who crosses a checkpoint
/// marker without having paid the boss.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderCheckpointMemberComponent : Component
{
    [DataField("checkpointId")]
    public string CheckpointId = string.Empty;
}