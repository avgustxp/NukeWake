using Robust.Shared.GameObjects;
using Robust.Shared.Map;
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

    /// <summary>
    /// Position of the spawn marker this member was placed at (captured on MapInit).
    /// Once combat is over the guard walks back here.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? Home;

    /// <summary>Game time of the last moment this member was actively fighting.</summary>
    [ViewVariables]
    public TimeSpan LastCombatTime;

    /// <summary>Game time when the current walk back to <see cref="Home"/> started.</summary>
    [ViewVariables]
    public TimeSpan ReturnStartTime;

    /// <summary>True while the member is walking back to <see cref="Home"/>.</summary>
    [ViewVariables]
    public bool ReturningHome;
}
