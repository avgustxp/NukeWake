using Content.Server.Chat.Systems;
using Content.Server.Chemistry.Containers.EntitySystems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.OfferItem;
using Content.Shared.Chat;
using Content.Shared.Chemistry.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.OfferItem;
using Content.Shared._N14.RandomEncounters;
using Robust.Server.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._N14.RandomEncounters;

/// <summary>
/// Drives <see cref="N14SurvivorComponent"/>:
/// - greets the first player who gets close enough;
/// - auto-accepts a clean-water drink offered through the F key, says thanks and
///   destroys the drink after a short delay (he "drinks it and throws it away");
/// - politely refuses anything that is not clean water;
/// - faces the nearest player while they are nearby;
/// - retaliates against whoever damages it (via <see cref="NPCRetaliation"/> + HTN)
///   and against anyone who strips equipment off it;
/// - removes the survivor once no player is within view for a grace period,
///   or after a long timeout if nobody ever showed up.
/// </summary>
public sealed class N14SurvivorSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly OfferItemSystem _offerItemSystem = default!;
    [Dependency] private readonly SolutionContainerSystem _solutions = default!;
    [Dependency] private readonly RotateToFaceSystem _rotateToFace = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NPCRetaliationSystem _retaliation = default!;

    /// <summary>How close a player has to be before the survivor greets them.</summary>
    private const float GreetRange = 8f;

    /// <summary>Players further away than this are considered "out of view".</summary>
    private const float DespawnRange = 16f;

    /// <summary>How long after the last player leaves view before the survivor despawns.</summary>
    private const float DespawnGraceSeconds = 5f;

    /// <summary>If nobody ever shows up, the survivor still despawns after this long.</summary>
    private const float NeverSeenLifetimeSeconds = 600f;

    /// <summary>Delay between Open-Space ticks of the survivor update.</summary>
    private const float UpdateInterval = 0.5f;

    private readonly Dictionary<EntityUid, TimeSpan> _lastSeenPlayer = new();
    private readonly Dictionary<EntityUid, TimeSpan> _spawnedAt = new();
    private float _acc;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<N14SurvivorComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<N14SurvivorComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<N14SurvivorComponent, DidUnequipEvent>(OnDidUnequip);
    }

    public override void Update(float frameTime)
    {
        _acc += frameTime;
        if (_acc < UpdateInterval)
            return;
        _acc = 0f;

        var query = EntityQueryEnumerator<N14SurvivorComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var survivorPos = _transform.GetMapCoordinates(uid);
            var hasPlayer = false;
            var greetPlayer = false;
            EntityUid nearestPlayer = EntityUid.Invalid;
            var nearestDistSq = float.MaxValue;

            foreach (var session in _playerManager.Sessions)
            {
                if (session.AttachedEntity is not { Valid: true } player || !Exists(player))
                    continue;

                // Ghosts, observers and other non-physical entities are invisible to the survivor.
                if (!TryComp<MobStateComponent>(player, out _))
                    continue;

                var playerPos = _transform.GetMapCoordinates(player);
                if (playerPos.MapId != survivorPos.MapId)
                    continue;

                var distSq = (playerPos.Position - survivorPos.Position).LengthSquared();

                if (distSq <= DespawnRange * DespawnRange)
                {
                    hasPlayer = true;
                    _lastSeenPlayer[uid] = _timing.CurTime;

                    if (distSq <= GreetRange * GreetRange)
                        greetPlayer = true;

                    if (distSq < nearestDistSq)
                    {
                        nearestDistSq = distSq;
                        nearestPlayer = player;
                    }
                }
            }

            var now = _timing.CurTime;
            if (!hasPlayer)
            {
                // Don't despawn until the survivor has received water,
                // unless nobody has ever shown up (safety timeout).
                if (!comp.HasReceivedWater)
                {
                    if (!comp.EverSeen ||
                        !_spawnedAt.TryGetValue(uid, out var spawned) ||
                        now - spawned <= TimeSpan.FromSeconds(NeverSeenLifetimeSeconds))
                        continue;
                }

                // If a player has been seen before, despawn once they leave view.
                if (comp.EverSeen &&
                    _lastSeenPlayer.TryGetValue(uid, out var lastSeen) &&
                    now - lastSeen > TimeSpan.FromSeconds(DespawnGraceSeconds))
                {
                    QueueDel(uid);
                    continue;
                }
            }
            else if (!comp.EverSeen)
            {
                comp.EverSeen = true;
            }

            if (greetPlayer && !comp.Greeted)
            {
                comp.Greeted = true;
                Say(uid, Loc.GetString($"survivor-greeting-{_random.Next(1, 4)}"));
            }

            // Face the nearest player so the survivor looks at who they're talking to.
            // Skip while actively fighting so the HTN combat can steer the survivor instead.
            if (nearestPlayer != EntityUid.Invalid &&
                !(TryComp<FactionExceptionComponent>(uid, out var factionException) &&
                  factionException.Hostiles.Count > 0))
                _rotateToFace.TryFaceCoordinates(uid, _transform.GetMapCoordinates(nearestPlayer).Position);

            TryHandleOffer(uid, comp);
        }
    }

    private void TryHandleOffer(EntityUid uid, N14SurvivorComponent comp)
    {
        if (!TryComp<OfferItemComponent>(uid, out var receiveComp) ||
            !receiveComp.IsInReceiveMode ||
            receiveComp.Target == null)
        {
            comp.HandledOffer = null;
            return;
        }

        if (!TryComp<OfferItemComponent>(receiveComp.Target.Value, out var playerOffer) ||
            playerOffer.Item == null)
            return;

        var offer = (receiveComp.Target.Value, playerOffer.Item.Value);
        if (comp.HandledOffer == offer)
            return;

        comp.HandledOffer = offer;

        var item = playerOffer.Item.Value;
        if (IsCleanWater(item))
        {
            _offerItemSystem.Receive(uid, receiveComp);
            comp.HasReceivedWater = true;
            Say(uid, Loc.GetString($"survivor-thanks-{_random.Next(1, 4)}"));

            // He drinks it and throws the empty container away shortly after.
            Timer.Spawn(TimeSpan.FromSeconds(2), () =>
            {
                if (Exists(item))
                    QueueDel(item);
            });
        }
        else
        {
            Say(uid, Loc.GetString("survivor-refuse"));
            _offerItemSystem.Decline(uid);
        }
    }

    private bool IsCleanWater(EntityUid item)
    {
        if (!TryComp<DrinkComponent>(item, out var drink))
            return false;

        if (!_solutions.TryGetSolution(item, drink.Solution, out _, out var solution))
            return false;

        return solution.GetTotalPrototypeQuantity("Water") > 0 ||
               solution.GetTotalPrototypeQuantity("WaterFiltered") > 0;
    }

    /// <summary>
    /// Speaks an in-game IC line from the survivor.
    /// </summary>
    private void Say(EntityUid uid, string message)
    {
        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, false);
    }

    private void OnInit(Entity<N14SurvivorComponent> ent, ref ComponentInit args)
    {
        _spawnedAt[ent] = _timing.CurTime;
    }

    private void OnShutdown(Entity<N14SurvivorComponent> ent, ref ComponentShutdown args)
    {
        _lastSeenPlayer.Remove(ent);
        _spawnedAt.Remove(ent);
    }

    /// <summary>
    /// The survivor reacts to being stripped: whoever removes its equipment gets
    /// treated like an attacker (same aggro mechanics as taking damage).
    /// We retaliate against the nearest player in range.
    /// </summary>
    private void OnDidUnequip(EntityUid uid, N14SurvivorComponent comp, ref DidUnequipEvent args)
    {
        if (!TryComp<NPCRetaliationComponent>(uid, out var retaliation))
            return;

        var survivorPos = _transform.GetMapCoordinates(uid);
        EntityUid nearestPlayer = EntityUid.Invalid;
        var nearestDistSq = float.MaxValue;

        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player || !Exists(player))
                continue;

            if (!TryComp<MobStateComponent>(player, out _))
                continue;

            var playerPos = _transform.GetMapCoordinates(player);
            if (playerPos.MapId != survivorPos.MapId)
                continue;

            var distSq = (playerPos.Position - survivorPos.Position).LengthSquared();
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestPlayer = player;
            }
        }

        if (nearestPlayer != EntityUid.Invalid && nearestPlayer != uid)
            _retaliation.TryRetaliate((uid, retaliation), nearestPlayer);
    }
}