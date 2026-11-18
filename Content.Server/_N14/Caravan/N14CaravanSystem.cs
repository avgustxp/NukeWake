using System.Linq;
using System.Numerics;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._N14.Caravan;

/// <summary>
/// Drives a caravan: spawns it at a <see cref="N14CaravanSpawnMarkerComponent"/>,
/// walks all of its members along the same numbered <see cref="N14CaravanRoutePointComponent"/>
/// markers (order is set by the digits in the marker name) and removes it at the
/// optional <see cref="N14CaravanDespawnMarkerComponent"/> or after the last route point.
/// Guards share retaliation: when any member is harmed the rest of the caravan aggroes the attacker.
/// If the leading guard dies the caravan simply stops in place instead of being removed.
/// </summary>
public sealed class N14CaravanSystem : EntitySystem
{
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float UpdateInterval = 0.25f;
    private const float RetaliationMemorySeconds = 30f;

    /// <summary>
    /// Maximum route distance handed to the pathfinder as a single hop. On big
    /// maps consecutive waypoints can be hundreds of tiles apart, which makes the
    /// steering A* time-slice across many ticks (the column visibly "thinks" for
    /// seconds at each point) and eventually blow the per-request node budget and
    /// permanently wedge the caravan. Splitting each long leg into short hops
    /// keeps every pathfind fast, reliable and cheap.
    /// </summary>
    private const float MaxHopDistance = 40f;

    /// <summary>
    /// Hard cap on how long a caravan will wait for a straggler at a waypoint
    /// before advancing anyway. Guards are fast and usually keep up, but a
    /// member that gets stuck (e.g. wedged on geometry) must not freeze the
    /// whole column forever.
    /// </summary>
    private const float StragglerGraceSeconds = 5f;

    /// <summary>
    /// If nobody has reached the current waypoint after this long AND at least one
    /// member is stuck in a NoPath state, treat the waypoint as unreachable and
    /// skip it. This keeps a column from freezing forever on a bad hop or a
    /// genuinely unreachable marker.
    /// </summary>
    private const float StallSkipSeconds = 20f;

    private float _accumulator;
    private readonly Dictionary<(EntityUid Member, EntityUid Attacker), TimeSpan> _retaliationMemories = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<N14CaravanSpawnMarkerComponent, MapInitEvent>(OnSpawnMarkerMapInit);
        SubscribeLocalEvent<N14CaravanMemberComponent, DamageChangedEvent>(OnMemberDamaged);
    }

    private void OnSpawnMarkerMapInit(EntityUid uid, N14CaravanSpawnMarkerComponent comp, MapInitEvent args)
    {
        // Defer a moment so all route markers from the map load are initialized.
        Timer.Spawn(500, () =>
        {
            if (TerminatingOrDeleted(uid))
                return;
            TryStartCaravan(uid);
        });
    }

    private void TryStartCaravan(EntityUid spawnMarker)
    {
        var spawnXform = Transform(spawnMarker);
        var gridUid = spawnXform.GridUid;
        if (gridUid == null)
            return;

        // Gather route markers on the same grid, ordered by the digits in their name.
        var routeSpots = new List<(int Order, EntityCoordinates Coords)>();
        var routeQuery = EntityQueryEnumerator<N14CaravanRoutePointComponent, TransformComponent>();
        while (routeQuery.MoveNext(out var pointUid, out _, out var pointXform))
        {
            if (pointXform.GridUid != gridUid)
                continue;

            var name = MetaData(pointUid).EntityName;
            routeSpots.Add((ParseOrder(name), pointXform.Coordinates));
        }

        routeSpots.Sort((a, b) => a.Order.CompareTo(b.Order));

        // Build the route and, on big maps, split long legs into short hops so the
        // steering pathfinder never has to chew through hundreds of tiles in one pass.
        var origin = spawnXform.Coordinates;
        var route = new List<EntityCoordinates>();
        for (var i = 0; i < routeSpots.Count; i++)
        {
            if (i == 0)
            {
                route.Add(routeSpots[0].Coords);
                continue;
            }

            var from = route[^1];
            var to = routeSpots[i].Coords;
            var hops = HopCount(from.Position, to.Position);
            route.AddRange(ChunkLeg(from, to, hops));
        }

        // Optional removal marker on the same grid. Fold it into the route as the
        // final (chunked) leg so the column walks it like any other waypoint and
        // the caravan is then removed.
        var despawn = (EntityCoordinates?)null;
        var despawnQuery = EntityQueryEnumerator<N14CaravanDespawnMarkerComponent, TransformComponent>();
        while (despawnQuery.MoveNext(out var despawnUid, out _, out var despawnXform))
        {
            if (despawnXform.GridUid == gridUid)
            {
                despawn = despawnXform.Coordinates;
                break;
            }
        }

        if (despawn is { } dp)
        {
            var last = route.Count > 0 ? route[^1] : origin;
            var hops = HopCount(last.Position, dp.Position);
            route.AddRange(ChunkLeg(last, dp, hops));
            despawn = null;
        }

        // Spawn the caravan: merc leader, pack brahmin, merc rear guard.
        var leader = Spawn("N14MobCaravanGuard", origin);
        var brahmin = Spawn("N14MobCaravanBrahmin", Offset(origin, new Vector2(-1f, 0f)));
        var rear = Spawn("N14MobCaravanGuard", Offset(origin, new Vector2(-2f, 0f)));

        var comp = EnsureComp<N14CaravanComponent>(leader);
        comp.Route = route;
        comp.DespawnPoint = despawn;
        comp.Members.Add(leader);
        comp.Members.Add(brahmin);
        comp.Members.Add(rear);
        comp.Brahmin = brahmin;

        foreach (var member in comp.Members)
        {
            EnsureComp<N14CaravanMemberComponent>(member).Caravan = leader;
        }

        // Everyone walks the same route together.
        var firstTarget = route.Count > 0 ? route[0] : despawn ?? origin;
        foreach (var member in comp.Members)
            _npc.SetBlackboard(member, NPCBlackboard.FollowTarget, firstTarget);

        comp.LastAdvanceTime = _timing.CurTime;
    }

    /// <summary>How many hops a leg of the given length is split into.</summary>
    private static int HopCount(Vector2 from, Vector2 to)
    {
        var distance = (to - from).Length();
        return Math.Max(1, (int)MathF.Ceiling(distance / MaxHopDistance));
    }

    /// <summary>
    /// Interpolates evenly-spaced waypoints along a leg, ending exactly on <paramref name="to"/>.
    /// The caller seeds the route with the leg start, so only the hop waypoints are returned.
    /// </summary>
    private static List<EntityCoordinates> ChunkLeg(EntityCoordinates from, EntityCoordinates to, int hops)
    {
        var waypoints = new List<EntityCoordinates>(hops);
        for (var i = 1; i <= hops; i++)
        {
            var t = i / (float)hops;
            var position = Vector2.Lerp(from.Position, to.Position, t);
            waypoints.Add(new EntityCoordinates(to.EntityId, position));
        }

        return waypoints;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < UpdateInterval)
            return;
        _accumulator = 0f;

        ExpireRetaliation();

        var query = EntityQueryEnumerator<N14CaravanComponent>();
        while (query.MoveNext(out var leader, out var comp))
        {
            if (comp.Removing)
                continue;

            // Drop dead members. If the leader dies the whole caravan halts but stays put.
            var leaderDead = false;
            for (var i = comp.Members.Count - 1; i >= 0; i--)
            {
                var member = comp.Members[i];
                if (!Exists(member))
                {
                    comp.Members.RemoveAt(i);
                    continue;
                }

                if (TryComp<MobStateComponent>(member, out var mobState) &&
                    mobState.CurrentState == MobState.Dead)
                {
                    if (i == 0)
                    {
                        leaderDead = true;
                        break;
                    }

                    comp.Members.RemoveAt(i);
                }
            }

            if (leaderDead)
            {
                comp.Stopped = true;
            }
            else
            {
                // Leader deleted some other way (grid wipe, admin) — treat the caravan as halted.
                if (comp.Members.Count == 0 || comp.Members[0] != leader || !Exists(leader))
                {
                    comp.Stopped = true;
                }
            }

            // Whenever the caravan is halted, every member idles in place so the brahmin
            // stops with the guards. Once the caravan resumes (Stopped cleared below) the
            // follow targets are re-driven from the current route point.
            if (comp.Stopped)
            {
                foreach (var member in comp.Members)
                {
                    if (Exists(member) && !TerminatingOrDeleted(member))
                        _npc.SetBlackboard(member, NPCBlackboard.FollowTarget,
                            Transform(member).Coordinates);
                }
                continue;
            }

            // While any guard is actively fighting, only the brahmin holds in place
            // so it doesn't walk ahead while the guards stop to shoot. The guards and
            // the waypoint advance are never frozen — their combat branch keeps them
            // engaged until the threat leaves, then everyone resumes the shared route.
            var brahminPaused = AnyGuardInCombat(comp);
            if (brahminPaused)
            {
                if (comp.Brahmin is { } brahmin && Exists(brahmin) && !TerminatingOrDeleted(brahmin))
                    _npc.SetBlackboard(brahmin, NPCBlackboard.FollowTarget,
                        Transform(brahmin).Coordinates);
            }

            // Whole column shares one waypoint: everyone heads to the current route point,
            // or to the despawn marker once every route point has been visited.
            var target = comp.NextPoint < comp.Route.Count ? comp.Route[comp.NextPoint] : comp.DespawnPoint;

            if (comp.NextPoint >= comp.Route.Count && comp.DespawnPoint == null)
            {
                // Route finished with no despawn marker: dismantle the caravan.
                RemoveCaravan(leader, comp);
                continue;
            }

            if (target == null)
                continue;

            foreach (var member in comp.Members)
            {
                // A fighting guard still gets the waypoint (its combat branch takes priority) but
                // the paused brahmin keeps standing still until no guard is fighting anymore.
                if (brahminPaused && member == comp.Brahmin)
                    continue;

                _npc.SetBlackboard(member, NPCBlackboard.FollowTarget, target.Value);
            }

            // Advance the shared waypoint once the whole living column is at it.
            // This keeps everyone walking the same route together: no member can
            // outrun the pack and end up "following" / being followed by another.
            var targetPos = _transform.ToMapCoordinates(target.Value).Position;
            var countArrived = 0;
            var countAlive = 0;
            foreach (var member in comp.Members)
            {
                // A paused brahmin is intentionally held back during combat; it does
                // not block the column from advancing once the guards reconverge.
                if (brahminPaused && member == comp.Brahmin)
                    continue;

                if (!Exists(member) || TerminatingOrDeleted(member))
                    continue;

                countAlive++;
                var memberPos = _transform.GetMapCoordinates(member).Position;
                if ((targetPos - memberPos).Length() <= comp.ArriveRange)
                    countArrived++;
            }

            if (countArrived >= countAlive)
            {
                // Whole column is at the waypoint — move on.
                comp.LastAdvanceTime = _timing.CurTime;
                comp.WaitingForStragglersSince = null;
            }
            else if (countArrived > 0)
            {
                // At least one member made it; the rest are still catching up. If a
                // straggler is wedged on geometry, don't let it freeze the column
                // forever — advance anyway after the grace window.
                comp.WaitingForStragglersSince ??= _timing.CurTime;
                var waiting = _timing.CurTime - comp.WaitingForStragglersSince.Value;
                if (waiting < TimeSpan.FromSeconds(StragglerGraceSeconds))
                    continue;
            }
            else
            {
                // Nobody is here yet — they are still walking. No timeout while in motion.
                comp.WaitingForStragglersSince = null;

                // Safety net: if a long far-away leg fails to pathfind at all (steering
                // NoPath on a big map), no member can ever reach the waypoint and the
                // column would freeze here forever. If a member is genuinely stuck and
                // enough time passed since the last advance, skip this waypoint.
                if (_timing.CurTime - comp.LastAdvanceTime > TimeSpan.FromSeconds(StallSkipSeconds) &&
                    AnyMemberStuckNoPath(comp))
                {
                    comp.LastAdvanceTime = _timing.CurTime;
                }
                else
                {
                    continue;
                }
            }

            if (comp.NextPoint < comp.Route.Count)
            {
                comp.NextPoint++;
            }
            else
            {
                // Reached the despawn marker: dismantle the caravan.
                RemoveCaravan(leader, comp);
            }
        }
    }

    /// <summary>True if any living member's steering is wedged in <see cref="SteeringStatus.NoPath"/>.</summary>
    private bool AnyMemberStuckNoPath(N14CaravanComponent comp)
    {
        foreach (var member in comp.Members)
        {
            if (!Exists(member) || TerminatingOrDeleted(member))
                continue;

            if (TryComp<NPCSteeringComponent>(member, out var steering) &&
                steering.Status == SteeringStatus.NoPath)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInCombat(EntityUid member)
    {
        return HasComp<NPCRangedCombatComponent>(member) || HasComp<NPCMeleeCombatComponent>(member);
    }

    private bool AnyGuardInCombat(N14CaravanComponent comp)
    {
        foreach (var member in comp.Members)
        {
            if (member != comp.Brahmin && !TerminatingOrDeleted(member) && IsInCombat(member))
                return true;
        }

        return false;
    }

    private void OnMemberDamaged(Entity<N14CaravanMemberComponent> member, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } origin)
            return;

        // Don't aggro on inanimate objects, mirroring NPCRetaliation.
        if (!HasComp<MobStateComponent>(origin))
            return;

        // Retaliate together: every other living member aggros the attacker.
        if (CompOrNull<N14CaravanComponent>(member.Comp.Caravan) is not { } comp)
            return;

        // Never turn on the caravan itself: friendly fire must not make the
        // column shoot its own members (bullets fired at a passing ally froze in mid-air).
        if (comp.Members.Contains(origin))
            return;

        foreach (var other in comp.Members)
        {
            if (other == member.Owner || !Exists(other) || TerminatingOrDeleted(other))
                continue;

            // Skip non-hostile factions, mirroring NPCRetaliation.
            if (_npcFaction.IsEntityFriendly(other, origin))
                continue;

            _npcFaction.AggroEntity(other, origin);
            _retaliationMemories[(other, origin)] = _timing.CurTime + TimeSpan.FromSeconds(RetaliationMemorySeconds);
        }
    }

    private void ExpireRetaliation()
    {
        if (_retaliationMemories.Count == 0)
            return;

        foreach (var ((member, attacker), expiry) in _retaliationMemories.ToList())
        {
            if (_timing.CurTime >= expiry)
            {
                _npcFaction.DeAggroEntity(member, attacker);
                _retaliationMemories.Remove((member, attacker));
            }
        }
    }

    private void RemoveCaravan(EntityUid leader, N14CaravanComponent comp)
    {
        if (comp.Removing)
            return;

        comp.Removing = true;
        foreach (var member in comp.Members)
        {
            if (Exists(member) && !TerminatingOrDeleted(member))
                QueueDel(member);
        }

        comp.Members.Clear();
        RemComp<N14CaravanComponent>(leader);
    }

    private static EntityCoordinates Offset(EntityCoordinates origin, Vector2 offset)
    {
        return new EntityCoordinates(origin.EntityId, origin.Position + offset);
    }

    /// <summary>Parses the route order from the digits in the marker name.</summary>
    private static int ParseOrder(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        var digits = new string(name.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var order) ? order : 0;
    }
}