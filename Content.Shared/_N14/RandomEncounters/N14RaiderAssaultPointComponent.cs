using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// Marks an attack point for the raider assault event. Raiders spawned by
/// <see cref="N14RaiderAssaultSpawnerComponent"/> markers walk to this point, then
/// wander within <see cref="WanderRadius"/> tiles of it until they die.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderAssaultPointComponent : Component
{
    /// <summary>
    /// Identifier shared with the spawners and the trigger that launch the raid.
    /// </summary>
    [DataField("attackPointId")]
    public string AttackPointId = "attack_point";

    /// <summary>
    /// Radius, in tiles, in which arrived raiders wander around the point.
    /// </summary>
    [DataField]
    public float WanderRadius = 7f;

    /// <summary>
    /// Maximum number of living raiders this point may have at once.
    /// </summary>
    [DataField]
    public int MaxAlive = 10;

    /// <summary>
    /// Radius, in tiles, around the sapper in which a wall to breach is searched.
    /// </summary>
    [DataField]
    public float BreachSearchRadius = 12f;

    /// <summary>
    /// Raider currently assigned to blow up <see cref="BreachWall"/>. Elected among
    /// the living attackers, so the "one in ten" rule keeps working when fewer survived.
    /// </summary>
    [ViewVariables]
    public EntityUid? Sapper;

    /// <summary>Wall the sapper is currently walking to / planting C4 on.</summary>
    [ViewVariables]
    public EntityUid? BreachWall;

    /// <summary>True once the sapper has started sticking C4 to <see cref="BreachWall"/>.</summary>
    [ViewVariables]
    public bool ChargePlanted;

    /// <summary>When the charge was planted, used to retry if the sapper died mid-plant.</summary>
    [ViewVariables]
    public TimeSpan ChargePlantedTime;

    /// <summary>Next time the direct path to this point is checked.</summary>
    [ViewVariables]
    public TimeSpan NextBreachCheck;

    /// <summary>True while an asynchronous pathfinding request is in flight.</summary>
    [ViewVariables]
    public bool PathCheckPending;
}
