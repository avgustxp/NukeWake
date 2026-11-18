using Robust.Shared.Map;

namespace Content.Server._N14.Caravan;

/// <summary>
/// Caravan runtime state attached to the leading guard.
/// All <see cref="Members"/> head to the same shared waypoint; the waypoint only
/// advances once every living member has reached it, so the whole column walks
/// the route together without anyone following another member.
/// </summary>
[RegisterComponent, Access(typeof(N14CaravanSystem))]
public sealed partial class N14CaravanComponent : Component
{
    /// <summary>Waypoints in route order.</summary>
    [ViewVariables]
    public List<EntityCoordinates> Route = new();

    /// <summary>Index into <see cref="Route"/> the column is currently heading to.</summary>
    [ViewVariables]
    public int NextPoint;

    /// <summary>Optional final removal point.</summary>
    [ViewVariables]
    public EntityCoordinates? DespawnPoint;

    /// <summary>All caravan members in the same order they were spawned, leader first.</summary>
    [ViewVariables]
    public List<EntityUid> Members = new();

    /// <summary>The pack brahmin (if still alive). Not used for combat/retaliation.</summary>
    [ViewVariables]
    public EntityUid? Brahmin;

    /// <summary>Arrival range to consider a waypoint/despawn reached by a member.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float ArriveRange = 1.5f;

    /// <summary>Game time of the last waypoint advance.</summary>
    [ViewVariables]
    public TimeSpan LastAdvanceTime;

    /// <summary>Game time the column started waiting for a straggler, or null
    /// while nobody has reached the current waypoint yet (they are still walking).</summary>
    [ViewVariables]
    public TimeSpan? WaitingForStragglersSince;

    /// <summary>True once removal has been started.</summary>
    [ViewVariables]
    public bool Removing;

    /// <summary>True once the leader has died: the caravan halts in place and no longer routes.</summary>
    [ViewVariables]
    public bool Stopped;
}