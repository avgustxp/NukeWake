using Content.Server.Mind;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Nuclear14.FactionReinforcement;
using Content.Shared.Access.Systems;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Popups;
using Content.Shared.Power.Components;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Nuclear14.FactionReinforcement;

public sealed class FactionReinforcementSystem : EntitySystem
{
    private const int ReinforcementDamageThreshold = 200;

    /// <summary>
    /// Commander job -> faction department.
    /// </summary>
    private static readonly Dictionary<string, string> CommanderDepartments = new()
    {
        ["CaesarLegionCenturion"] = "CaesarLegion",
        ["NCRLT"] = "NCR",
        ["BoSMidPaladinCommander"] = "BrotherhoodMidwest",
        ["BoSWestElderCommander"] = "BrotherhoodWest",
    };

    /// <summary>
    /// Commander entity prototype -> commander job.
    /// </summary>
    private static readonly Dictionary<string, string> CommanderPrototypes = new()
    {
        ["N14MobHumanCaesarLegionCenturion"] = "CaesarLegionCenturion",
        ["N14MobHumanNCRLT"] = "NCRLT",
        ["BoSMidPaladinCommander"] = "BoSMidPaladinCommander",
        ["N14BoSWestElderCommander"] = "BoSWestElderCommander",
    };

    [Dependency] private MindSystem _mind = default!;
    [Dependency] private StationJobsSystem _stationJobs = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedIdCardSystem _idCard = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IGameTiming _timing = default!;

    /// <summary>
    /// Players that have taken at least <see cref="ReinforcementDamageThreshold"/> damage this round.
    /// </summary>
    private readonly HashSet<NetUserId> _damagedPlayers = new();

    /// <summary>
    /// Players that have been handcuffed at least once this round.
    /// </summary>
    private readonly HashSet<NetUserId> _cuffedPlayers = new();

    /// <summary>
    /// Factions (station + department) that already used their single reinforcement this round.
    /// </summary>
    private readonly HashSet<(EntityUid Station, string Department)> _usedReinforcements = new();

    /// <summary>
    /// Terminals with an open UI, so their status can be refreshed as players die.
    /// </summary>
    private readonly Dictionary<EntityUid, (EntityUid Actor, string Department)> _openTerminals = new();

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FactionReinforcementComponent, MapInitEvent>(OnTerminalMapInit);
        SubscribeLocalEvent<FactionReinforcementComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<FactionReinforcementComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<FactionReinforcementComponent, FactionReinforcementRequestMessage>(OnReinforcementRequested);
        SubscribeLocalEvent<MindContainerComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<CuffableComponent, CuffedStateChangeEvent>(OnCuffedStateChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    /// <summary>
    /// The reinforcement terminal is meant to work without a connection to the power grid.
    /// Power components are inherited from the base terminal prototype, so they are removed
    /// here instead of affecting every other terminal that shares the same parent.
    /// </summary>
    private void OnTerminalMapInit(EntityUid uid, FactionReinforcementComponent component, MapInitEvent args)
    {
        RemCompDeferred<ActivatableUIRequiresPowerComponent>(uid);
        RemCompDeferred<ApcPowerReceiverComponent>(uid);
        RemCompDeferred<ExtensionCableReceiverComponent>(uid);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _damagedPlayers.Clear();
        _cuffedPlayers.Clear();
        _usedReinforcements.Clear();
    }

    private void OnCuffedStateChanged(EntityUid uid, CuffableComponent component, ref CuffedStateChangeEvent args)
    {
        if (component.CuffedHandCount <= 0)
            return;

        if (!_mind.TryGetMind(uid, out _, out var mind) || mind.UserId is not { } userId)
            return;

        _cuffedPlayers.Add(userId);
    }

    private void OnDamageChanged(EntityUid uid, MindContainerComponent component, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        if (args.Damageable.TotalDamage >= ReinforcementDamageThreshold)
            MarkDamaged(uid);
    }

    private void MarkDamaged(EntityUid uid)
    {
        if (!_mind.TryGetMind(uid, out _, out var mind) || mind.UserId is not { } userId)
            return;

        _damagedPlayers.Add(userId);
    }

    private void OnUiOpened(EntityUid uid, FactionReinforcementComponent component, BoundUIOpenedEvent args)
    {
        if (!args.UiKey.Equals(FactionReinforcementUiKey.Key))
            return;

        if (!TryGetCommanderDepartment(args.Actor, out var department, out _))
        {
            _popup.PopupEntity(Loc.GetString("faction-reinforcement-no-access"), uid, args.Actor, PopupType.MediumCaution);
            _ui.CloseUi(uid, FactionReinforcementUiKey.Key, args.Actor);
            return;
        }

        _openTerminals[uid] = (args.Actor, department);
        _ui.SetUiState(uid, FactionReinforcementUiKey.Key, BuildState(uid, department));
    }

    private void OnUiClosed(EntityUid uid, FactionReinforcementComponent component, BoundUIClosedEvent args)
    {
        if (!args.UiKey.Equals(FactionReinforcementUiKey.Key))
            return;

        if (_openTerminals.TryGetValue(uid, out var open) && open.Actor == args.Actor)
            _openTerminals.Remove(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_openTerminals.Count == 0)
            return;

        var now = _timing.CurTime;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + TimeSpan.FromSeconds(1);

        List<EntityUid>? stale = null;

        foreach (var (terminal, open) in _openTerminals)
        {
            if (TerminatingOrDeleted(terminal) || TerminatingOrDeleted(open.Actor))
            {
                (stale ??= new List<EntityUid>()).Add(terminal);
                continue;
            }

            _ui.SetUiState(terminal, FactionReinforcementUiKey.Key, BuildState(terminal, open.Department));
        }

        if (stale == null)
            return;

        foreach (var terminal in stale)
            _openTerminals.Remove(terminal);
    }

    private void OnReinforcementRequested(EntityUid uid, FactionReinforcementComponent component,
        FactionReinforcementRequestMessage args)
    {
        var actor = args.Actor;

        if (!TryGetCommanderDepartment(actor, out var department, out _))
        {
            _popup.PopupEntity(Loc.GetString("faction-reinforcement-no-access"), uid, actor, PopupType.MediumCaution);
            _ui.CloseUi(uid, FactionReinforcementUiKey.Key, actor);
            return;
        }

        if (!_proto.TryIndex<DepartmentPrototype>(department, out var departmentProto) ||
            !departmentProto.Roles.Contains(args.Job))
        {
            RefreshUi(uid, department);
            return;
        }

        if (!_proto.TryIndex<JobPrototype>(args.Job, out var job))
        {
            RefreshUi(uid, department);
            return;
        }

        // Roles that can use the terminal cannot request reinforcements for themselves.
        if (CommanderDepartments.ContainsKey(job.ID))
        {
            RefreshUi(uid, department);
            return;
        }

        var station = _station.GetOwningStation(uid);
        if (station is not { } stationUid || !TryComp<StationJobsComponent>(stationUid, out var stationJobs))
        {
            _popup.PopupEntity(Loc.GetString("faction-reinforcement-no-station"), uid, actor, PopupType.MediumCaution);
            RefreshUi(uid, department);
            return;
        }

        if (IsFactionLocked(stationUid, department))
        {
            _popup.PopupEntity(Loc.GetString("faction-reinforcement-denied"), uid, actor, PopupType.MediumCaution);
            RefreshUi(uid, department);
            return;
        }

        var status = GetReinforcementStatus(stationUid, department, job);
        if (status != FactionReinforcementStatus.Available)
        {
            _popup.PopupEntity(Loc.GetString("faction-reinforcement-denied"), uid, actor, PopupType.MediumCaution);
            RefreshUi(uid, department);
            return;
        }

        if (!_stationJobs.TryAdjustJobSlot(stationUid, job, 1, false, false, stationJobs))
        {
            _popup.PopupEntity(Loc.GetString("faction-reinforcement-denied"), uid, actor, PopupType.MediumCaution);
            RefreshUi(uid, department);
            return;
        }

        _usedReinforcements.Add((stationUid, department));

        _popup.PopupEntity(Loc.GetString("faction-reinforcement-added", ("job", job.LocalizedName)),
            uid, actor, PopupType.Medium);
        RefreshUi(uid, department);
    }

    private bool IsFactionLocked(EntityUid station, string department)
    {
        return _usedReinforcements.Contains((station, department));
    }

    private void RefreshUi(EntityUid uid, string department)
    {
        _ui.SetUiState(uid, FactionReinforcementUiKey.Key, BuildState(uid, department));
    }

    private FactionReinforcementBoundUserInterfaceState BuildState(EntityUid uid, string department)
    {
        var state = new FactionReinforcementBoundUserInterfaceState
        {
            DepartmentId = department,
        };

        var station = _station.GetOwningStation(uid);
        if (station is not { } stationUid ||
            !_proto.TryIndex<DepartmentPrototype>(department, out var departmentProto))
        {
            return state;
        }

        foreach (var role in departmentProto.Roles)
        {
            if (!_proto.TryIndex<JobPrototype>(role, out var job))
                continue;

            // Roles with access to this terminal are not shown: their holder cannot
            // request a reinforcement for themselves.
            if (CommanderDepartments.ContainsKey(job.ID))
                continue;

            if (!_stationJobs.TryGetJobSlot(stationUid, job.ID, out _))
                continue;

            state.Jobs.Add(new FactionReinforcementJobEntry
            {
                Job = job.ID,
                Status = GetReinforcementStatus(stationUid, department, job),
            });
        }

        return state;
    }

    private FactionReinforcementStatus GetReinforcementStatus(EntityUid station, string department, JobPrototype job)
    {
        if (!_stationJobs.TryGetJobSlot(station, job, out var freeSlots))
            return FactionReinforcementStatus.NotInStation;

        if (IsFactionLocked(station, department))
            return FactionReinforcementStatus.Unavailable;

        if (freeSlots == null)
            return FactionReinforcementStatus.Unlimited;

        if (freeSlots.Value > 0)
            return FactionReinforcementStatus.HasFreeSlots;

        var holders = 0;
        var anyDamaged = false;
        var anyEligible = false;
        var anyWearingId = false;

        foreach (var userId in _stationJobs.GetPlayersWithJob(station, job.ID))
        {
            holders++;

            // Receiving at least 200 damage is the mandatory gateway. It does not unlock
            // the reinforcement by itself -- the ID card / handcuff conditions must also pass.
            if (!IsHolderDamaged(userId))
                continue;

            anyDamaged = true;

            var cuffed = IsHolderCuffed(userId);
            var wearingId = HolderWearsJobId(userId, job);

            if (wearingId)
                anyWearingId = true;

            if (cuffed || !wearingId)
                anyEligible = true;
        }

        if (holders == 0)
            return FactionReinforcementStatus.NoHolders;

        if (!anyDamaged)
            return FactionReinforcementStatus.NotFallen;

        if (anyEligible)
            return FactionReinforcementStatus.Available;

        return anyWearingId
            ? FactionReinforcementStatus.HolderHasId
            : FactionReinforcementStatus.HolderAlive;
    }

    private bool IsHolderCuffed(NetUserId userId)
    {
        if (_cuffedPlayers.Contains(userId))
            return true;

        if (!_mind.TryGetMind(userId, out _, out var mind))
            return false;

        var body = GetHolderBody(mind);
        return body is { } ent &&
               !TerminatingOrDeleted(ent) &&
               TryComp<CuffableComponent>(ent, out var cuffable) &&
               cuffable.CuffedHandCount > 0;
    }

    private bool IsHolderDamaged(NetUserId userId)
    {
        if (_damagedPlayers.Contains(userId))
            return true;

        if (!_mind.TryGetMind(userId, out _, out var mind))
            return false;

        var body = GetHolderBody(mind);
        if (body is not { } ent || TerminatingOrDeleted(ent))
            return false;

        if (!TryComp<DamageableComponent>(ent, out var damageable) ||
            damageable.TotalDamage < ReinforcementDamageThreshold)
        {
            return false;
        }

        _damagedPlayers.Add(userId);
        return true;
    }

    /// <summary>
    /// Returns the body the player originally had on this role, not whatever entity their
    /// mind is currently attached to (a ghost, or a new character after respawning).
    /// </summary>
    private EntityUid? GetHolderBody(MindComponent mind)
    {
        if (mind.OriginalOwnedEntity is { } originalNet)
        {
            var original = GetEntity(originalNet);
            if (!TerminatingOrDeleted(original) && IsRealBody(original))
                return original;
        }

        if (mind.OwnedEntity is { } owned && !TerminatingOrDeleted(owned) && IsRealBody(owned))
            return owned;

        return null;
    }

    private bool IsRealBody(EntityUid uid)
    {
        return HasComp<MobStateComponent>(uid) && !HasComp<GhostComponent>(uid);
    }

    private bool HolderWearsJobId(NetUserId userId, JobPrototype job)
    {
        if (!_mind.TryGetMind(userId, out _, out var mind))
            return false;

        return WearsJobId(GetHolderBody(mind), job);
    }

    private bool WearsJobId(EntityUid? entity, JobPrototype job)
    {
        if (entity is not { } ent || TerminatingOrDeleted(ent))
            return false;

        if (!_idCard.TryFindIdCard(ent, out var idCard))
            return false;

        var card = idCard.Comp;
        return card.LocalizedJobTitle == job.LocalizedName || card.JobTitle?.Id == job.Name;
    }

    private bool TryGetCommanderDepartment(EntityUid actor, out string department, out string? jobId)
    {
        department = string.Empty;
        jobId = null;

        var prototypeId = MetaData(actor).EntityPrototype?.ID;
        if (prototypeId != null && CommanderPrototypes.TryGetValue(prototypeId, out var prototypeJob))
        {
            jobId = prototypeJob;
            if (CommanderDepartments.TryGetValue(prototypeJob, out var prototypeDepartment))
            {
                department = prototypeDepartment;
                return true;
            }
        }

        jobId = GetAssignedJob(actor);
        if (jobId != null && CommanderDepartments.TryGetValue(jobId, out var jobDepartment))
        {
            department = jobDepartment;
            return true;
        }

        return false;
    }

    private string? GetAssignedJob(EntityUid actor)
    {
        var station = _station.GetOwningStation(actor);
        if (station is not { } stationUid)
            return null;

        if (!_mind.TryGetMind(actor, out _, out var mind) || mind.UserId is not { } userId)
            return null;

        if (!_stationJobs.TryGetPlayerJobs(stationUid, userId, out var jobs) || jobs.Count == 0)
            return null;

        return jobs[0].Id;
    }

}
