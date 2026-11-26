using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Announcements.Systems;
using Content.Server.Destructible;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Popups;
using Content.Server.Sticky.Components;
using Content.Server.Sticky.Systems;
using Content.Server._Forge.NPC;
using Content.Shared._N14.RandomEncounters;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._N14.RandomEncounters;

/// <summary>
/// Drives the raider assault event:
/// - a <see cref="N14RaiderAssaultTriggerComponent"/> item, used in hand, broadcasts an
///   announcement naming the attack point and spawns raiders from every
///   <see cref="N14RaiderAssaultSpawnerComponent"/> marker bound to that point;
/// - spawned raiders walk to the point (fighting any hostile they meet on the way);
/// - if the point has no reachable path, one living raider is elected as a sapper and
///   sticks C4 to a destructible wall blocking the way (any wall whose prototype id
///   does not contain "Indestructible"). The election happens among the living
///   attackers, so it also works when fewer than <see cref="N14RaiderAssaultPointComponent.MaxAlive"/>
///   survived;
/// - on arrival they wander within <see cref="N14RaiderAssaultPointComponent.WanderRadius"/>
///   tiles of it until they die;
/// - no more than <see cref="N14RaiderAssaultPointComponent.MaxAlive"/> raiders are alive
///   for a point at once.
/// </summary>
public sealed class N14RaiderAssaultSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly ProximityNPCSystem _proximity = default!;
    [Dependency] private readonly AnnouncerSystem _announcer = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly StickySystem _sticky = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>Delay between assault update ticks.</summary>
    private const float UpdateInterval = 0.5f;

    /// <summary>Distance to the point at which a raider is considered to have arrived.</summary>
    private const float ArriveRange = 2f;

    /// <summary>
    /// If pathfinding to the point fails for this long (unreachable marker, walls),
    /// start wandering anyway instead of walking into a wall forever.
    /// </summary>
    private const float MaxTravelTime = 300f;

    private const float MinWanderDelay = 4f;
    private const float MaxWanderDelay = 10f;

    /// <summary>How often the direct path to an attack point is checked.</summary>
    private const float BreachCheckInterval = 6f;

    /// <summary>Grace period after spawning before a raider may be elected as a sapper.</summary>
    private const float BreachGraceTime = 8f;

    /// <summary>Distance from the wall centre at which the sapper plants the charge.</summary>
    private const float PlantRange = 2.5f;

    /// <summary>If the charge never goes off (sapper died mid-plant), retry after this.</summary>
    private const float ChargeRetryTimeout = 40f;

    /// <summary>How far the sapper retreats from the charge before it blows.</summary>
    private const float RetreatDistance = 6f;

    private const string BreachChargePrototype = "C4";

    /// <summary>Announcement prototype id; also drives the locale key.</summary>
    private const string AnnouncementId = "N14RaiderAssault";

    private float _acc;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<N14RaiderAssaultTriggerComponent, UseInHandEvent>(OnUseInHand);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _acc += frameTime;
        if (_acc < UpdateInterval)
            return;

        _acc = 0f;
        UpdateMembers();
    }

    private void OnUseInHand(Entity<N14RaiderAssaultTriggerComponent> ent, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        var pointId = ent.Comp.AttackPointId;
        if (!TryGetAttackPoint(pointId, out var pointUid, out var point))
        {
            _popup.PopupEntity(Loc.GetString("n14-raider-assault-no-point"), ent, args.User);
            return;
        }

        var pointName = MetaData(pointUid).EntityName;

        _announcer.SendAnnouncement(
            _announcer.GetAnnouncementId(AnnouncementId),
            Filter.Broadcast(),
            _announcer.GetEventLocaleString(_announcer.GetAnnouncementId(AnnouncementId)),
            colorOverride: Color.Gold,
            localeArgs: new (string, object)[] { ("point", pointName) });

        var spawned = SpawnRaiders(pointId, pointUid);

        if (spawned == 0)
        {
            if (!HasSpawners(pointId))
                _popup.PopupEntity(Loc.GetString("n14-raider-assault-no-spawners"), ent, args.User);
            else if (CountAlive(pointId) >= point.MaxAlive)
                _popup.PopupEntity(Loc.GetString("n14-raider-assault-cap-reached"), ent, args.User);

            return;
        }

        _popup.PopupEntity(
            Loc.GetString("n14-raider-assault-triggered", ("point", pointName)),
            ent,
            args.User);
    }

    /// <summary>
    /// Spawns raiders from every marker bound to <paramref name="pointId"/> until the
    /// point's living-raider cap is reached. Returns how many were spawned.
    /// </summary>
    private int SpawnRaiders(string pointId, EntityUid pointUid)
    {
        TryComp<N14RaiderAssaultPointComponent>(pointUid, out var point);
        var cap = point?.MaxAlive ?? 10;

        var alive = CountAlive(pointId);
        var spawned = 0;

        if (point != null)
        {
            // A new raid starts with a clean breach state.
            point.Sapper = null;
            point.BreachWall = null;
            point.ChargePlanted = false;
            point.PathCheckPending = false;
            point.NextBreachCheck = _timing.CurTime + TimeSpan.FromSeconds(BreachGraceTime);
        }

        var query = EntityQueryEnumerator<N14RaiderAssaultSpawnerComponent>();
        while (query.MoveNext(out var spawnerUid, out var spawner))
        {
            if (!string.Equals(spawner.AttackPointId, pointId, StringComparison.Ordinal))
                continue;

            if (spawner.Prototype.Length == 0)
            {
                Log.Error($"N14RaiderAssaultSpawner {ToPrettyString(spawnerUid)} has no prototype set");
                continue;
            }

            if (alive >= cap)
                break;

            var spawnCoords = _transform.GetMapCoordinates(spawnerUid);

            for (var i = 0; i < spawner.Count && alive < cap; i++)
            {
                var offset = new Vector2(
                    _random.NextFloat(-0.5f, 0.5f),
                    _random.NextFloat(-0.5f, 0.5f));

                var raider = Spawn(spawner.Prototype,
                    new MapCoordinates(spawnCoords.Position + offset, spawnCoords.MapId));

                var member = EnsureComp<N14RaiderAssaultMemberComponent>(raider);
                member.AttackPointId = pointId;
                member.Point = pointUid;
                member.SpawnTime = _timing.CurTime;
                member.NextWanderTime = _timing.CurTime;

                if (HasComp<HTNComponent>(raider))
                    _npc.SetBlackboard(raider, NPCBlackboard.FollowTarget, Transform(pointUid).Coordinates);

                alive++;
                spawned++;
            }
        }

        return spawned;
    }

    /// <summary>
    /// Returns true when at least one spawner marker is bound to <paramref name="pointId"/>.
    /// </summary>
    private bool HasSpawners(string pointId)
    {
        var query = EntityQueryEnumerator<N14RaiderAssaultSpawnerComponent>();
        while (query.MoveNext(out _, out var spawner))
        {
            if (string.Equals(spawner.AttackPointId, pointId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Counts living raiders already assaulting <paramref name="pointId"/>.
    /// </summary>
    private int CountAlive(string pointId)
    {
        var count = 0;
        var query = EntityQueryEnumerator<N14RaiderAssaultMemberComponent>();

        while (query.MoveNext(out var uid, out var member))
        {
            if (!string.Equals(member.AttackPointId, pointId, StringComparison.Ordinal))
                continue;

            if (!IsAlive(uid))
                continue;

            count++;
        }

        return count;
    }

    /// <summary>
    /// Steers assault raiders: walk to the point until arrival, then pick wander
    /// targets inside the point radius. Dead raiders are left alone.
    /// </summary>
    private void UpdateMembers()
    {
        var now = _timing.CurTime;
        var wakeUntil = now + TimeSpan.FromSeconds(UpdateInterval * 2);
        var points = new HashSet<EntityUid>();

        var query = EntityQueryEnumerator<N14RaiderAssaultMemberComponent>();
        while (query.MoveNext(out var uid, out var member))
        {
            if (member.Point is not { } pointUid || !Exists(pointUid))
                continue;

            if (!IsAlive(uid))
                continue;

            if (!TryComp<N14RaiderAssaultPointComponent>(pointUid, out var point))
                continue;

            var memberPos = _transform.GetMapCoordinates(uid);
            var pointPos = _transform.GetMapCoordinates(pointUid);

            if (memberPos.MapId != pointPos.MapId)
                continue;

            points.Add(pointUid);

            // NPCs far from players are put to sleep by ProximityNPC; keep assault
            // raiders awake so they keep walking and wandering.
            _proximity.WakeForRetaliation(uid, wakeUntil);

            if (member.Arrived)
            {
                if (now < member.NextWanderTime)
                    continue;

                PickWanderTarget(uid, pointUid, point);
                member.NextWanderTime = now + TimeSpan.FromSeconds(
                    _random.NextFloat(MinWanderDelay, MaxWanderDelay));
                continue;
            }

            var distance = (memberPos.Position - pointPos.Position).Length();
            if (distance <= ArriveRange)
            {
                member.Arrived = true;
                member.NextWanderTime = now;
                continue;
            }

            if (point.Sapper == uid)
            {
                if (point.BreachWall is { } wall &&
                    Exists(wall) &&
                    !TerminatingOrDeleted(wall) &&
                    IsValidBreachWall(wall))
                {
                    if (point.ChargePlanted)
                    {
                        // Stay still while the sticky do-after runs, then back away
                        // from the blast before the timer expires.
                        if (now < member.FreezeUntil)
                            SetFollowTarget(uid, Transform(uid).Coordinates);
                        else
                            SetFollowTarget(uid, GetRetreatCoords(wall, pointUid));

                        continue;
                    }

                    var wallPos = _transform.GetMapCoordinates(wall);
                    var wallDistance = (memberPos.Position - wallPos.Position).Length();

                    if (wallDistance <= PlantRange)
                    {
                        PlantCharge(uid, member, point, wall, now);
                        continue;
                    }

                    SetFollowTarget(uid, GetWallApproachCoords(wall, uid));
                    continue;
                }

                // No wall to blow up (any more): release the sapper role.
                point.Sapper = null;
            }

            // While a wall is being breached, let the rest of the group advance
            // towards it instead of standing still at the unreachable path.
            if (point.BreachWall is { } breachWall &&
                Exists(breachWall) &&
                !TerminatingOrDeleted(breachWall))
            {
                var breachPos = _transform.GetMapCoordinates(breachWall);
                var distanceToWall = (memberPos.Position - breachPos.Position).Length();
                if (distanceToWall > 5f)
                {
                    SetFollowTarget(uid, GetWallApproachCoords(breachWall, uid, 4f));
                    continue;
                }
            }

            if (now - member.SpawnTime < TimeSpan.FromSeconds(MaxTravelTime))
            {
                // Re-issued every tick: the HTN walk branch does the actual
                // pathfinding and steering towards the stored position.
                SetFollowTarget(uid, Transform(pointUid).Coordinates);
                continue;
            }

            member.Arrived = true;
            member.NextWanderTime = now;
        }

        foreach (var pointUid in points)
        {
            if (TryComp<N14RaiderAssaultPointComponent>(pointUid, out var point))
                UpdatePointBreach(pointUid, point, now);
        }
    }

    /// <summary>
    /// Keeps the breach state per attack point: elects a sapper when the direct path
    /// to the point is blocked, drops dead sappers, and retries after failed charges.
    /// </summary>
    private void UpdatePointBreach(EntityUid pointUid, N14RaiderAssaultPointComponent point, TimeSpan now)
    {
        if (point.Sapper is { } oldSapper && !IsAlive(oldSapper))
        {
            point.Sapper = null;
            point.ChargePlanted = false;
        }

        if (point.BreachWall is { } wall)
        {
            if (!Exists(wall) || TerminatingOrDeleted(wall) || !IsValidBreachWall(wall))
            {
                // The wall was destroyed (or otherwise stopped blocking): re-check
                // whether the point can be reached now.
                point.BreachWall = null;
                point.ChargePlanted = false;
                point.PathCheckPending = false;
                point.NextBreachCheck = now;
            }
            else
            {
                if (point.ChargePlanted &&
                    now - point.ChargePlantedTime > TimeSpan.FromSeconds(ChargeRetryTimeout))
                {
                    // The charge never went off, for example because the sapper died
                    // before the sticky do-after finished. Try again.
                    point.ChargePlanted = false;
                    point.BreachWall = null;
                    point.Sapper = null;
                    point.NextBreachCheck = now;
                }

                if (point.Sapper is null && !point.ChargePlanted)
                    point.Sapper = FindSapper(pointUid, now);

                return;
            }
        }

        if (point.PathCheckPending || now < point.NextBreachCheck)
            return;

        var sapper = point.Sapper ?? FindSapper(pointUid, now);
        if (sapper is not { } sapperUid)
            return;

        point.Sapper = sapperUid;
        point.PathCheckPending = true;
        point.NextBreachCheck = now + TimeSpan.FromSeconds(BreachCheckInterval);
        _ = CheckBreachPathAsync(pointUid, sapperUid);
    }

    /// <summary>
    /// Pathfinds from the sapper to the point. If there is no direct path, picks a
    /// destructible wall blocking the way and assigns it to the sapper.
    /// </summary>
    private async Task CheckBreachPathAsync(EntityUid pointUid, EntityUid sapperUid)
    {
        PathResultEvent? path = null;

        try
        {
            if (TryComp<TransformComponent>(sapperUid, out var sapperXform) &&
                TryComp<TransformComponent>(pointUid, out var pointXform))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                path = await _pathfinding.GetPathSafe(
                    sapperUid,
                    sapperXform.Coordinates,
                    pointXform.Coordinates,
                    ArriveRange,
                    cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Error($"N14RaiderAssault path check to {ToPrettyString(pointUid)} failed: {e}");
        }

        if (!Exists(pointUid) ||
            TerminatingOrDeleted(pointUid) ||
            !TryComp<N14RaiderAssaultPointComponent>(pointUid, out var point))
        {
            return;
        }

        point.PathCheckPending = false;

        if (path is not { } result)
            return;

        if (result.Result == PathResult.Path)
        {
            // Direct path exists: nobody has to breach anything.
            point.Sapper = null;
            point.BreachWall = null;
            point.ChargePlanted = false;
            return;
        }

        if (point.BreachWall is not null)
            return;

        if (!IsAlive(sapperUid) ||
            !TryComp<N14RaiderAssaultMemberComponent>(sapperUid, out var member) ||
            member.Arrived)
        {
            return;
        }

        if (!TryFindBreachWall(sapperUid, pointUid, point, out var wall))
            return;

        point.BreachWall = wall;
        point.Sapper = sapperUid;
        point.ChargePlanted = false;
    }

    /// <summary>
    /// Picks the living attacker closest to the point as the sapper. Election is
    /// dynamic, so a dead sapper is replaced by any surviving raider.
    /// </summary>
    private EntityUid? FindSapper(EntityUid pointUid, TimeSpan now)
    {
        var grace = TimeSpan.FromSeconds(BreachGraceTime);
        var pointPos = _transform.GetMapCoordinates(pointUid);

        EntityUid? best = null;
        var bestDistance = float.MaxValue;

        var query = EntityQueryEnumerator<N14RaiderAssaultMemberComponent>();
        while (query.MoveNext(out var uid, out var member))
        {
            if (member.Point != pointUid || member.Arrived)
                continue;

            if (!IsAlive(uid))
                continue;

            // Give the pathfinding graph a moment to catch up after spawning.
            if (now - member.SpawnTime < grace)
                continue;

            var memberPos = _transform.GetMapCoordinates(uid);
            if (memberPos.MapId != pointPos.MapId)
                continue;

            var distance = (memberPos.Position - pointPos.Position).LengthSquared();
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = uid;
        }

        return best;
    }

    /// <summary>
    /// Finds a destructible, non-indestructible wall between the sapper and the point.
    /// First tries a ray cast along the straight line, then scans around the sapper.
    /// </summary>
    private bool TryFindBreachWall(
        EntityUid sapper,
        EntityUid pointUid,
        N14RaiderAssaultPointComponent point,
        out EntityUid wall)
    {
        wall = EntityUid.Invalid;

        var sapperPos = _transform.GetMapCoordinates(sapper);
        var pointPos = _transform.GetMapCoordinates(pointUid);

        if (sapperPos.MapId != pointPos.MapId)
            return false;

        var toPoint = pointPos.Position - sapperPos.Position;
        if (toPoint.LengthSquared() < 0.01f)
            return false;

        var direction = Vector2.Normalize(toPoint);

        // Nearest destructible wall directly in the way.
        var ray = new CollisionRay(sapperPos.Position, direction, (int) CollisionGroup.Impassable);
        var hits = _physics.IntersectRayWithPredicate(
            sapperPos.MapId,
            ray,
            toPoint.Length() + 1f,
            e => e == sapper,
            false);

        var bestRayDistance = float.MaxValue;
        foreach (var hit in hits)
        {
            if (hit.Distance >= bestRayDistance || !IsValidBreachWall(hit.HitEntity))
                continue;

            bestRayDistance = hit.Distance;
            wall = hit.HitEntity;
        }

        if (wall != EntityUid.Invalid)
            return true;

        // Fallback for a point around a corner: scan nearby walls roughly in the
        // direction of the point, preferring the closest one.
        var radius = MathF.Max(2f, point.BreachSearchRadius);
        var candidates = _lookup.GetEntitiesInRange(sapperPos, radius, LookupFlags.Static);
        var perpendicularAxis = new Vector2(-direction.Y, direction.X);
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!IsValidBreachWall(candidate))
                continue;

            var candidatePos = _transform.GetMapCoordinates(candidate);
            if (candidatePos.MapId != sapperPos.MapId)
                continue;

            var toWall = candidatePos.Position - sapperPos.Position;
            var along = Vector2.Dot(toWall, direction);
            var perpendicular = MathF.Abs(Vector2.Dot(toWall, perpendicularAxis));

            if (along <= 0.25f || along > toPoint.Length() + 2f || perpendicular > 3f)
                continue;

            var score = along + perpendicular * 1.5f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            wall = candidate;
        }

        if (wall != EntityUid.Invalid)
            return true;

        // Last resort: any destructible wall near the sapper, even if it is not on
        // the straight line to the point (e.g. the point is behind a corner).
        var nearest = float.MaxValue;
        foreach (var candidate in candidates)
        {
            if (!IsValidBreachWall(candidate))
                continue;

            var candidatePos = _transform.GetMapCoordinates(candidate);
            if (candidatePos.MapId != sapperPos.MapId)
                continue;

            var distance = (candidatePos.Position - sapperPos.Position).LengthSquared();
            if (distance >= nearest)
                continue;

            nearest = distance;
            wall = candidate;
        }

        return wall != EntityUid.Invalid;
    }

    /// <summary>
    /// A wall can be breached when it blocks movement and is destructible, unless its
    /// prototype id is marked as indestructible.
    /// </summary>
    private bool IsValidBreachWall(EntityUid uid)
    {
        if (!Exists(uid) || TerminatingOrDeleted(uid))
            return false;

        if (!TryComp<MetaDataComponent>(uid, out var meta))
            return false;

        var prototypeId = meta.EntityPrototype?.ID;
        if (prototypeId != null &&
            prototypeId.Contains("Indestructible", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasComp<MobStateComponent>(uid))
            return false;

        if (!HasComp<DestructibleComponent>(uid))
            return false;

        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return false;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if ((fixture.CollisionLayer & (int) CollisionGroup.Impassable) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Spawns C4 on the sapper and starts the standard sticky do-after on the wall.
    /// The <c>startOnStick</c> trigger then arms and detonates the charge, opening a hole.
    /// </summary>
    private void PlantCharge(
        EntityUid sapper,
        N14RaiderAssaultMemberComponent member,
        N14RaiderAssaultPointComponent point,
        EntityUid wall,
        TimeSpan now)
    {
        point.ChargePlanted = true;
        point.ChargePlantedTime = now;

        var charge = Spawn(BreachChargePrototype, _transform.GetMapCoordinates(sapper));

        if (!TryComp<StickyComponent>(charge, out var sticky) ||
            !_sticky.TryStartSticking(charge, sapper, wall, sticky, needHand: false))
        {
            // Could not start planting; release the wall so another attempt can happen.
            point.ChargePlanted = false;
            point.BreachWall = null;
            point.Sapper = null;
            point.NextBreachCheck = now + TimeSpan.FromSeconds(BreachCheckInterval);

            if (Exists(charge) && !TerminatingOrDeleted(charge))
                QueueDel(charge);

            return;
        }

        var stickDelay = (float) sticky.StickDelay.TotalSeconds;
        member.FreezeUntil = now + TimeSpan.FromSeconds(stickDelay + 1f);
    }

    /// <summary>
    /// Sends the raider to a point inside the attack point's wander radius.
    /// Re-using the <see cref="NPCBlackboard.FollowTarget"/> key means the normal
    /// <c>N14CaravanWalkCompound</c> walk branch does the movement.
    /// </summary>
    private void PickWanderTarget(EntityUid uid, EntityUid pointUid, N14RaiderAssaultPointComponent point)
    {
        if (!HasComp<HTNComponent>(uid))
            return;

        var radius = MathF.Max(0.5f, point.WanderRadius);
        var angle = _random.NextFloat(0f, MathF.PI * 2f);
        // sqrt keeps the distribution uniform over the disc instead of clustered
        // around the centre.
        var distance = MathF.Sqrt(_random.NextFloat()) * radius;
        var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, new EntityCoordinates(pointUid, offset));
    }

    /// <summary>
    /// Coordinates next to the wall on the sapper's side, so the NPC can reach the
    /// wall tile itself (its centre is not walkable).
    /// </summary>
    private EntityCoordinates GetWallApproachCoords(EntityUid wall, EntityUid sapper, float distance = 0.95f)
    {
        var wallXform = Transform(wall);
        var wallPos = _transform.GetMapCoordinates(wall);
        var sapperPos = _transform.GetMapCoordinates(sapper);

        var direction = sapperPos.Position - wallPos.Position;
        if (direction.LengthSquared() < 0.01f)
            direction = new Vector2(0f, 1f);

        direction = Vector2.Normalize(direction);
        var target = new MapCoordinates(wallPos.Position + direction * distance, wallPos.MapId);
        return _transform.ToCoordinates((wall, wallXform), target);
    }

    /// <summary>
    /// Coordinates away from the attack point, used to move the sapper out of the blast.
    /// </summary>
    private EntityCoordinates GetRetreatCoords(EntityUid wall, EntityUid pointUid)
    {
        var wallXform = Transform(wall);
        var wallPos = _transform.GetMapCoordinates(wall);
        var pointPos = _transform.GetMapCoordinates(pointUid);

        var away = wallPos.Position - pointPos.Position;
        if (away.LengthSquared() < 0.01f)
            away = new Vector2(0f, 1f);

        away = Vector2.Normalize(away);
        var target = new MapCoordinates(wallPos.Position + away * RetreatDistance, wallPos.MapId);
        return _transform.ToCoordinates((wall, wallXform), target);
    }

    private void SetFollowTarget(EntityUid uid, EntityCoordinates coordinates)
    {
        if (HasComp<HTNComponent>(uid))
            _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, coordinates);
    }

    private bool IsAlive(EntityUid uid)
    {
        if (!Exists(uid) || TerminatingOrDeleted(uid))
            return false;

        return !TryComp<MobStateComponent>(uid, out var mob) ||
               mob.CurrentState == MobState.Alive;
    }

    private bool TryGetAttackPoint(string pointId, out EntityUid pointUid, out N14RaiderAssaultPointComponent point)
    {
        var query = EntityQueryEnumerator<N14RaiderAssaultPointComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!string.Equals(comp.AttackPointId, pointId, StringComparison.Ordinal))
                continue;

            pointUid = uid;
            point = comp;
            return true;
        }

        pointUid = EntityUid.Invalid;
        point = default!;
        return false;
    }
}
