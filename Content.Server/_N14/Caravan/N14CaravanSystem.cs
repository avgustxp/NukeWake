using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.Destructible;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Weapons.Melee;
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
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
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
    private const float StragglerGraceSeconds = 3f;

    /// <summary>
    /// If nobody has reached the current waypoint after this long AND at least one
    /// member is stuck in a NoPath state, treat the waypoint as unreachable and
    /// skip it. This keeps a column from freezing forever on a bad hop or a
    /// genuinely unreachable marker.
    /// </summary>
    private const float StallSkipSeconds = 10f;

    /// <summary>How long a member can fail to move toward the waypoint before we force re-path.</summary>
    private const float StuckThresholdSeconds = 5f;

    /// <summary>Minimum distance (units) a member must move in StuckThresholdSeconds to not be considered stuck.</summary>
    private const float StuckMoveThreshold = 1f;

    /// <summary>Max different evasion offsets to try before giving up on a stuck member.</summary>
    private const int MaxStuckAttempts = 4;

    private float _accumulator;

    /// <summary>Last known position and time per caravan member, for stuck detection.</summary>
    private readonly Dictionary<EntityUid, (Vector2 Position, TimeSpan Time)> _memberLastPos = new();

    /// <summary>How many evasion offsets we've already tried for a stuck member (0..MaxStuckAttempts-1).</summary>
    private readonly Dictionary<EntityUid, int> _memberStuckAttempts = new();

    /// <summary>Last time a member smashed an obstacle, to avoid spamming attacks every tick.</summary>
    private readonly Dictionary<EntityUid, TimeSpan> _memberLastSmash = new();

    private readonly Dictionary<(EntityUid Member, EntityUid Attacker), TimeSpan> _retaliationMemories = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<N14CaravanSpawnMarkerComponent, MapInitEvent>(OnSpawnMarkerMapInit);
        SubscribeLocalEvent<N14CaravanMemberComponent, DamageChangedEvent>(OnMemberDamaged);
    }

    private void OnSpawnMarkerMapInit(EntityUid uid, N14CaravanSpawnMarkerComponent comp, MapInitEvent args)
    {
        // Defer a moment so all markers from the map load are initialized.
        Timer.Spawn(500, async () =>
        {
            if (TerminatingOrDeleted(uid))
                return;
            await TryStartCaravanAsync(uid);
        });
    }

    /// <summary>
    /// Spawns a caravan at the given marker and computes an optimal path to the
    /// despawn marker (if any) using <see cref="PathfindingSystem"/>. No intermediate
    /// route-point markers are required.
    /// </summary>
    private async Task TryStartCaravanAsync(EntityUid spawnMarker)
    {
        var spawnXform = Transform(spawnMarker);
        var gridUid = spawnXform.GridUid;
        if (gridUid == null)
            return;

        var origin = spawnXform.Coordinates;

        // Optional removal marker on the same grid — the final destination.
        EntityCoordinates? despawn = null;
        var despawnQuery = EntityQueryEnumerator<N14CaravanDespawnMarkerComponent, TransformComponent>();
        while (despawnQuery.MoveNext(out var despawnUid, out _, out var despawnXform))
        {
            if (despawnXform.GridUid == gridUid)
            {
                despawn = despawnXform.Coordinates;
                break;
            }
        }

        // Build the route: ask the pathfinder for an optimal path from spawn to despawn.
        var route = new List<EntityCoordinates>();
        if (despawn is { } dp)
        {
            var cancel = new System.Threading.CancellationTokenSource();
            var pathResult = await _pathfinding.GetPathSafe(
                spawnMarker,
                origin,
                dp,
                1.5f, // arrive within this distance of the target
                cancel.Token);

            if (pathResult.Result == PathResult.NoPath || pathResult.Path == null || pathResult.Path.Count == 0)
            {
                Log.Error($"Caravan at {spawnMarker}: no path to despawn marker, spawning at origin without route.");
                route.Add(origin);
            }
            else
            {
                // Extract waypoints from path polys (each poly center is a navigation node).
                foreach (var poly in pathResult.Path)
                {
                    route.Add(poly.Coordinates);
                }

                // Ensure the route starts exactly at the spawn origin.
                if (!route[0].Equals(origin))
                    route.Insert(0, origin);
            }
        }
        else
        {
            // No despawn marker — just spawn at origin with a trivial route.
            route.Add(origin);
        }

        // Split long legs into short hops so the steering pathfinder never has to
        // chew through hundreds of tiles in one pass on big maps.
        var finalRoute = new List<EntityCoordinates>();
        for (var i = 0; i < route.Count; i++)
        {
            if (i == 0)
            {
                finalRoute.Add(route[0]);
                continue;
            }

            var from = finalRoute[^1];
            var to = route[i];
            var hops = HopCount(from.Position, to.Position);
            finalRoute.AddRange(ChunkLeg(from, to, hops));
        }

        // Spawn the caravan: merc leader, pack brahmin, merc rear guard.
        var leader = Spawn("N14MobCaravanGuard", origin);
        var brahmin = Spawn("N14MobCaravanBrahmin", Offset(origin, new Vector2(-1f, 0f)));
        var rear = Spawn("N14MobCaravanGuard", Offset(origin, new Vector2(-2f, 0f)));

        var comp = EnsureComp<N14CaravanComponent>(leader);
        comp.Route = finalRoute;
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
        var firstTarget = finalRoute.Count > 0 ? finalRoute[0] : origin;
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
                    _memberLastPos.Remove(member);
                    _memberStuckAttempts.Remove(member);
                    _memberLastSmash.Remove(member);
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
                    _memberLastPos.Remove(member);
                    _memberStuckAttempts.Remove(member);
                    _memberLastSmash.Remove(member);
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

            // Stuck detection and proactive re-pathing: if a member hasn't moved
            // toward the waypoint for StuckThresholdSeconds, cancel their current
            // pathfind and re-issue the target (with an evasion offset on repeats)
            // so the NPC steering system re-calculates a route around whatever
            // is blocking them.
            var now = _timing.CurTime;
            foreach (var member in comp.Members)
            {
                if (brahminPaused && member == comp.Brahmin)
                    continue;
                if (!Exists(member) || TerminatingOrDeleted(member))
                {
                    _memberLastPos.Remove(member);
                    _memberStuckAttempts.Remove(member);
                    continue;
                }

                var memberPos = _transform.GetMapCoordinates(member).Position;

                if (_memberLastPos.TryGetValue(member, out var last))
                {
                    var timeStuck = now - last.Time;
                    var distMoved = (memberPos - last.Position).Length();

                    if (timeStuck > TimeSpan.FromSeconds(StuckThresholdSeconds) && distMoved < StuckMoveThreshold)
                    {
                        // Free the stuck NPC: cancel any in-flight pathfind so the
                        // steering system is forced to request a fresh path next tick.
                        if (TryComp<NPCSteeringComponent>(member, out var steering))
                        {
                            steering.PathfindToken?.Cancel();
                            steering.PathfindToken = null;
                            steering.CurrentPath.Clear();
                            steering.FailedPathCount = 0;
                            steering.Status = SteeringStatus.Moving;
                        }

                        // On repeated stuck events, try to smash a nearby destructible obstacle
                        // (walls, crates, etc.) so the column can push through. Only smash on
                        // repeat attempts — first we try evasion, then we break what blocks us.
                        if (_memberStuckAttempts.TryGetValue(member, out var attempts) && attempts > 0)
                        {
                            TrySmashObstacle(member);
                        }

                        // On repeated stuck events, offset the target so the NPC
                        // tries to walk around the obstacle instead of into it.
                        var evasionTarget = target.Value;
                        if (_memberStuckAttempts.TryGetValue(member, out var _) && attempts > 0)
                        {
                            var radians = MathF.PI * attempts / 2f; // 90, 180, 270 degrees
                            var offset = new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * 2.5f;
                            evasionTarget = new EntityCoordinates(target.Value.EntityId, target.Value.Position + offset);
                        }

                        _npc.SetBlackboard(member, NPCBlackboard.FollowTarget, evasionTarget);

                        if (!_memberStuckAttempts.ContainsKey(member))
                            _memberStuckAttempts[member] = 0;
                        _memberStuckAttempts[member] = (_memberStuckAttempts[member] + 1) % MaxStuckAttempts;
                    }
                    else
                    {
                        // Moving normally — reset stuck attempt counter.
                        _memberStuckAttempts.Remove(member);
                    }
                }

                _memberLastPos[member] = (memberPos, now);
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
                    continue; // <-- don't fall through to NextPoint++ without waiting
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

    /// <summary>
    /// Attempts to smash a nearby destructible obstacle (wall, crate, etc.) so the
    /// stuck caravan member can push through. Only deals 100 structural damage —
    /// enough to break most light obstacles but not wipe everything in sight.
    /// </summary>
    private void TrySmashObstacle(EntityUid member)
    {
        // Cooldown: don't smash more than once every 2 seconds per member.
        if (_memberLastSmash.TryGetValue(member, out var lastSmash) &&
            _timing.CurTime - lastSmash < TimeSpan.FromSeconds(2f))
            return;

        if (!_melee.TryGetWeapon(member, out _, out var meleeWeapon))
            return;

        var memberPos = _transform.GetMapCoordinates(member).Position;
        var mapId = _transform.GetMapCoordinates(member).MapId;

        // Find the nearest destructible obstacle within smashing range.
        EntityUid? nearest = null;
        var nearestDistSq = float.MaxValue;

        var destructibleQuery = EntityQueryEnumerator<DestructibleComponent, TransformComponent>();
        while (destructibleQuery.MoveNext(out var obstacle, out _, out var obsXform))
        {
            if (!Exists(obstacle) || obstacle == member)
                continue;

            if (_transform.GetMapId(obstacle) != mapId)
                continue;

            var distSq = (obsXform.Coordinates.Position - memberPos).LengthSquared();
            if (distSq <= 9f && distSq < nearestDistSq) // 3 tiles radius
            {
                nearestDistSq = distSq;
                nearest = obstacle;
            }
        }

        if (nearest is not { } obstacleEnt)
            return;

        _memberLastSmash[member] = _timing.CurTime;

        // Deal damage to break the obstacle (weapon damage applied via melee system).
        _melee.AttemptLightAttack(member, member, meleeWeapon, obstacleEnt);
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

            _memberLastPos.Remove(member);
            _memberStuckAttempts.Remove(member);
            _memberLastSmash.Remove(member);
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
