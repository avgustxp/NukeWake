using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// Added to every raider spawned by <see cref="N14RaiderAssaultSystem"/>. Tracks
/// which attack point the raider belongs to and whether it has already reached
/// the point, after which it wanders around it until death.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderAssaultMemberComponent : Component
{
    /// <summary>Attack point this raider is assaulting.</summary>
    [DataField("attackPointId")]
    public string AttackPointId = string.Empty;

    /// <summary>Cached attack point entity.</summary>
    [ViewVariables]
    public EntityUid? Point;

    /// <summary>True once the raider has reached the point and started wandering.</summary>
    [ViewVariables]
    public bool Arrived;

    /// <summary>Game time at which the raider was spawned (used as a travel timeout).</summary>
    [ViewVariables]
    public TimeSpan SpawnTime;

    /// <summary>Game time at which to pick the next wander target.</summary>
    [ViewVariables]
    public TimeSpan NextWanderTime;

    /// <summary>
    /// While this time is in the future the sapper stands still, so the sticky
    /// do-after (planting C4) is not interrupted.
    /// </summary>
    [ViewVariables]
    public TimeSpan FreezeUntil;
}
