using Content.Server.Chat.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.OfferItem;
using Content.Server.Popups;
using Content.Shared.Chat;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.OfferItem;
using Content.Shared.Stacks;
using Content.Shared._N14.RandomEncounters;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Random;
using System.Linq;
using System.Numerics;

namespace Content.Server._N14.RandomEncounters;

/// <summary>
/// Drives raider checkpoints (see <see cref="N14RaiderCheckpointComponent"/>, <see cref="N14RaiderCheckpointMemberComponent"/>
/// and <see cref="N14RaiderCheckpointMarkerComponent"/>):
/// - the boss auto-accepts a stack of caps offered through the F key, takes the toll,
///   remembers the payer and resets the offer;
/// - anything else offered is refused;
/// - a player passing through a gate (the marker cluster) without having paid angers
///   every garrison member sharing the same <see cref="N14RaiderCheckpointMemberComponent.CheckpointId"/>;
///   paying the boss afterwards calms them back down. Simply approaching the gate does nothing.
/// </summary>
public sealed class N14RaiderCheckpointSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly OfferItemSystem _offerItemSystem = default!;
    [Dependency] private readonly SharedStackSystem _stackSystem = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly RotateToFaceSystem _rotateToFace = default!;
    [Dependency] private readonly NPCRetaliationSystem _retaliation = default!;

    /// <summary>Delay between checkpoint update ticks.</summary>
    private const float UpdateInterval = 0.5f;

    /// <summary>Players closer than this are greeted by the boss and turned to face.</summary>
    private const float GreetRange = 8f;

    /// <summary>Markers farther apart than this are treated as separate gates.</summary>
    private const float GateClusterRange = 4f;

    /// <summary>Users who already crossed a gate (hostile) per gate.</summary>
    private readonly Dictionary<(string Id, MapId Map, Box2 Box), HashSet<NetUserId>> _triggeredUsers = new();

    /// <summary>Last known side of the gate for each player, to detect actual crossings.</summary>
    private readonly Dictionary<(string Id, MapId Map, Box2 Box), Dictionary<NetUserId, bool>> _crossingState = new();

    private float _acc;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<N14RaiderCheckpointMemberComponent, DidUnequipEvent>(OnDidUnequip);
    }

    public override void Update(float frameTime)
    {
        _acc += frameTime;
        if (_acc < UpdateInterval)
            return;
        _acc = 0f;

        var checkpoints = new Dictionary<string, (EntityUid Boss, N14RaiderCheckpointComponent Comp)>();
        var garrisons = new Dictionary<string, List<EntityUid>>();

        var checkpointQuery = EntityQueryEnumerator<N14RaiderCheckpointComponent>();
        while (checkpointQuery.MoveNext(out var uid, out var comp))
        {
            if (comp.CheckpointId.Length == 0)
                continue;

            checkpoints[comp.CheckpointId] = (uid, comp);
        }

        var memberQuery = EntityQueryEnumerator<N14RaiderCheckpointMemberComponent>();
        while (memberQuery.MoveNext(out var uid, out var comp))
        {
            if (comp.CheckpointId.Length == 0)
                continue;

            if (!garrisons.TryGetValue(comp.CheckpointId, out var garrison))
                garrisons[comp.CheckpointId] = garrison = new List<EntityUid>();

            garrison.Add(uid);
        }

        // Markers sharing a checkpointId are clustered by proximity into gates; each gate
        // gets its own axis-aligned "doorway" box. Players anger the garrison only when they
        // cross into that box (i.e. pass the marker line), never by merely approaching it.
        var gates = new Dictionary<string, List<(MapId Map, Box2 Box)>>();
        var positionsByCheckpoint = new Dictionary<string, List<(MapId Map, Vector2 Pos)>>();

        var markerQuery = EntityQueryEnumerator<N14RaiderCheckpointMarkerComponent>();
        while (markerQuery.MoveNext(out var marker, out var comp))
        {
            if (comp.CheckpointId.Length == 0)
                continue;

            if (!positionsByCheckpoint.TryGetValue(comp.CheckpointId, out var positions))
                positionsByCheckpoint[comp.CheckpointId] = positions = new List<(MapId, Vector2)>();

            var coords = _transform.GetMapCoordinates(marker);
            positions.Add((coords.MapId, coords.Position));
        }

        foreach (var (id, positions) in positionsByCheckpoint)
        {
            var boxes = new List<(MapId Map, Box2 Box)>();
            foreach (var cluster in ClusterPositions(positions))
            {
                boxes.Add((cluster[0].Map, ComputeBox(cluster)));
            }

            gates[id] = boxes;
        }

        var seenUsers = new HashSet<NetUserId>();

        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player || !Exists(player))
                continue;

            // Ghosts, observers and other non-physical entities are invisible to the gate.
            if (!TryComp<MobStateComponent>(player, out _))
                continue;

            var playerPos = _transform.GetMapCoordinates(player);
            var userId = session.UserId;
            seenUsers.Add(userId);

            foreach (var (id, gateList) in gates)
            {
                garrisons.TryGetValue(id, out var garrison);
                checkpoints.TryGetValue(id, out var checkpoint);

                foreach (var (map, box) in gateList)
                {
                    if (map != playerPos.MapId)
                        continue;

                    // Crossing is tracked per gate and per player: unpaid players anger the
                    // garrison only when they move from outside the doorway box to inside it.
                    if (!_crossingState.TryGetValue((id, map, box), out var states))
                        _crossingState[(id, map, box)] = states = new Dictionary<NetUserId, bool>();

                    var firstSeen = !states.TryGetValue(userId, out var wasInside);
                    var inside = box.Contains(playerPos.Position);
                    states[userId] = inside;

                    if (!inside)
                        continue;

                    // First time we see this player for this gate: seed the state silently
                    // so people standing inside a gate (or dropped there) are not flagged.
                    if (firstSeen)
                        continue;

                    if (!_triggeredUsers.TryGetValue((id, map, box), out var triggered))
                        _triggeredUsers[(id, map, box)] = triggered = new HashSet<NetUserId>();

                    var paid = checkpoint.Comp != null && checkpoint.Comp.PaidUsers.Contains(userId);

                    if (paid)
                    {
                        // Player paid up since we angered the garrison: stand the guards down.
                        if (triggered.Remove(userId) && garrison != null)
                        {
                            foreach (var member in garrison)
                                _npcFaction.DeAggroEntity(member, player);
                        }
                    }
                    else if (!wasInside && triggered.Add(userId) && garrison != null)
                    {
                        _popup.PopupEntity(Loc.GetString("raider-checkpoint-warning"), player, player);

                        foreach (var member in garrison)
                            _npcFaction.AggroEntity(member, player);
                    }
                }
            }
        }

        // Forget players who disconnected or went observer, so a fresh appearance is a fresh crossing.
        PruneIsolatedUsers(seenUsers);

        foreach (var (boss, comp) in checkpoints.Values)
            HandleBoss(boss, comp);
    }

    private void PruneIsolatedUsers(HashSet<NetUserId> seenUsers)
    {
        foreach (var states in _crossingState.Values)
        {
            foreach (var user in states.Keys.ToArray())
            {
                if (!seenUsers.Contains(user))
                    states.Remove(user);
            }
        }

        foreach (var triggered in _triggeredUsers.Values)
        {
            foreach (var user in triggered.ToArray())
            {
                if (!seenUsers.Contains(user))
                    triggered.Remove(user);
            }
        }
    }

    /// <summary>
    /// Groups gate markers that are close together into separate crossing planes.
    /// </summary>
    private static List<List<(MapId Map, Vector2 Pos)>> ClusterPositions(List<(MapId Map, Vector2 Pos)> positions)
    {
        var clusters = new List<List<(MapId Map, Vector2 Pos)>>();
        foreach (var position in positions)
        {
            List<(MapId Map, Vector2 Pos)>? best = null;
            var bestDistSq = GateClusterRange * GateClusterRange;
            foreach (var cluster in clusters)
            {
                if (cluster[0].Map != position.Map)
                    continue;

                foreach (var other in cluster)
                {
                    var distSq = (other.Pos - position.Pos).LengthSquared();
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = cluster;
                    }
                }
            }

            if (best == null)
            {
                best = new List<(MapId Map, Vector2 Pos)>();
                clusters.Add(best);
            }

            best.Add(position);
        }

        return clusters;
    }

    /// <summary>
    /// Fits an axis-aligned box around the gate markers: the "doorway" the player must walk through.
    /// </summary>
    private static Box2 ComputeBox(List<(MapId Map, Vector2 Pos)> cluster)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var (_, pos) in cluster)
        {
            minX = MathF.Min(minX, pos.X);
            minY = MathF.Min(minY, pos.Y);
            maxX = MathF.Max(maxX, pos.X);
            maxY = MathF.Max(maxY, pos.Y);
        }

        return new Box2(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Runs the per-tick behaviour of a checkpoint boss: turns to face the nearest
    /// player (unless fighting) and calls the toll price out once on approach, then
    /// processes any pending caps offer.
    /// </summary>
    private void HandleBoss(EntityUid boss, N14RaiderCheckpointComponent comp)
    {
        TryHandleOffer(boss, comp);

        var bossPos = _transform.GetMapCoordinates(boss);
        EntityUid nearest = EntityUid.Invalid;
        var nearestDistSq = float.MaxValue;

        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player || !Exists(player))
                continue;

            if (!TryComp<MobStateComponent>(player, out _))
                continue;

            var playerPos = _transform.GetMapCoordinates(player);
            if (playerPos.MapId != bossPos.MapId)
                continue;

            var distSq = (playerPos.Position - bossPos.Position).LengthSquared();
            if (distSq <= GreetRange * GreetRange && distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearest = player;
            }
        }

        if (nearest == EntityUid.Invalid)
            return;

        // Skip while actively fighting so the HTN combat can steer the boss instead.
        if (!(TryComp<FactionExceptionComponent>(boss, out var factionException) &&
              factionException.Hostiles.Count > 0))
            _rotateToFace.TryFaceCoordinates(boss, _transform.GetMapCoordinates(nearest).Position);

        // The greeting is tracked per character pawn, so every new face at the gate
        // (even the same session re-logging in as another character) is told the toll.
        if (comp.GreetedCharacters.Count > 64)
            comp.GreetedCharacters.RemoveWhere(uid => !Exists(uid));

        if (comp.GreetedCharacters.Add(nearest))
            Say(boss, Loc.GetString($"raider-checkpoint-greeting-{_random.Next(1, 4)}"));
    }

    private void TryHandleOffer(EntityUid uid, N14RaiderCheckpointComponent comp)
    {
        if (!TryComp<OfferItemComponent>(uid, out var receiveComp) ||
            !receiveComp.IsInReceiveMode ||
            receiveComp.Target is not { } player)
        {
            comp.HandledOffer = null;
            return;
        }

        if (!TryComp<OfferItemComponent>(player, out var playerOffer) ||
            playerOffer.Item is not { } item)
            return;

        var offer = (player, item);
        if (comp.HandledOffer == offer)
            return;

        comp.HandledOffer = offer;

        if (TryComp<StackComponent>(item, out var stack) &&
            stack.StackTypeId == comp.CurrencyStackType &&
            stack.Count >= comp.PaymentAmount)
        {
            _stackSystem.Use(item, comp.PaymentAmount, stack);

            // Remember by session UserId so a respawned character is still considered paid.
            if (_playerManager.TryGetSessionByEntity(player, out var session))
                comp.PaidUsers.Add(session.UserId);

            Say(uid, Loc.GetString($"raider-checkpoint-thanks-{_random.Next(1, 4)}"));
        }
        else
        {
            Say(uid, Loc.GetString("raider-checkpoint-refuse"));
        }

        // Keeps the offered item with the player and resets both offer states.
        _offerItemSystem.Decline(uid);
    }

    /// <summary>
    /// Garrison members treat being stripped like being attacked: whoever removes
    /// their equipment gets retaliated against (same aggro mechanics as taking damage).
    /// Attackers that damage them are already handled by <see cref="NPCRetaliation"/>.
    /// </summary>
    private void OnDidUnequip(Entity<N14RaiderCheckpointMemberComponent> ent, ref DidUnequipEvent args)
    {
        if (!TryComp<NPCRetaliationComponent>(ent, out var retaliation))
            return;

        var memberPos = _transform.GetMapCoordinates(ent);
        EntityUid nearest = EntityUid.Invalid;
        var nearestDistSq = float.MaxValue;

        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player || !Exists(player))
                continue;

            if (!TryComp<MobStateComponent>(player, out _))
                continue;

            var playerPos = _transform.GetMapCoordinates(player);
            if (playerPos.MapId != memberPos.MapId)
                continue;

            var distSq = (playerPos.Position - memberPos.Position).LengthSquared();
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearest = player;
            }
        }

        if (nearest != EntityUid.Invalid && nearest != ent.Owner)
            _retaliation.TryRetaliate((ent, retaliation), nearest);
    }

    /// <summary>
    /// Speaks an in-game IC line from the boss.
    /// </summary>
    private void Say(EntityUid uid, string message)
    {
        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, false);
    }
}