using Content.Server.Announcements.Systems;
using Content.Server.GameTicking;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.StationEvents.Components;
using Content.Server.StationEvents.Events;
using Content.Server.Warps;
using Content.Shared.Database;
using Content.Shared.GameTicking.Components;
using Content.Shared.Maps;
using System.Numerics;
using Robust.Shared.Audio.Systems;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._Forge.WorldEvents;

/// <summary>
///     Система крушения вертибёрда. Правило добавляется пресетом в начале раунда,
///     само назначает случайный момент крушения и выполняет его: звук подлёта,
///     загрузка грида обломков, серия взрывов и broadcast-анонс.
///     Также может быть запущено принудительно триггер-предметом.
/// </summary>
public sealed class N14VertibirdCrashRuleSystem : StationEventSystem<N14VertibirdCrashRuleComponent>
{
    /// <summary>
    ///     Минимальная площадь (в тайлах) для грида-кандидата в фолбэке,
    ///     чтобы не выбрать мелкий мусорный грид.
    /// </summary>
    private const float MinAnchorGridTiles = 100f;

    [Dependency] private readonly AnnouncerSystem _announcer = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    protected override void Started(EntityUid uid, N14VertibirdCrashRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (component.Crashed)
            return;

        // Назначаем случайный момент крушения (если ещё не назначен триггером).
        if (component.CrashAt == TimeSpan.Zero)
        {
            var delay = _random.NextFloat(component.MinStartDelay, component.MaxStartDelay);
            component.CrashAt = Timing.CurTime + TimeSpan.FromSeconds(delay);
            Sawmill.Info($"Vertibird crash scheduled in {delay:F0}s");
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<N14VertibirdCrashRuleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Crashed || comp.CrashAt == TimeSpan.Zero || Timing.CurTime < comp.CrashAt)
                continue;

            if (!HasComp<ActiveGameRuleComponent>(uid))
                continue;

            DoCrash(uid, comp);
        }
    }

    /// <summary>
    ///     Принудительно вызывает крушение при следующем тике (используется триггер-предметом).
    /// </summary>
    public void TriggerCrash(Entity<N14VertibirdCrashRuleComponent> rule)
    {
        rule.Comp.CrashAt = Timing.CurTime;
    }

    private void DoCrash(EntityUid ruleUid, N14VertibirdCrashRuleComponent comp)
    {
        comp.Crashed = true;

        // Ищем якорный грид, относительно которого выбираем точку падения.
        if (!TryGetAnchor(out var anchor, out var mapId))
        {
            Sawmill.Error("Vertibird crash aborted: no suitable anchor grid found");
            return;
        }

        var anchorPos = _xform.GetWorldPosition(anchor);

        // Ищем точку падения: предпочитаемый тайл, куда помещается весь обломок,
        // затем любой тайл поверхности в радиусе, затем случайная точка.
        if (!TryFindCrashSpot(anchor, mapId, comp, out var offset))
        {
            Sawmill.Error("Vertibird crash aborted: no suitable crash spot found");
            return;
        }

        // Звук пролетающего вертибёрда слышит вся карта.
        if (comp.ApproachSound != null)
            _audio.PlayGlobal(comp.ApproachSound, Filter.BroadcastMap(mapId), true);

        AdminLogManager.Add(LogType.EventStarted, LogImpact.High,
            $"Vertibird crash started at {offset} on map {mapId}");

        // Анонс на весь раунд.
        _announcer.SendAnnouncement(
            _announcer.GetAnnouncementId("N14VertibirdCrash"),
            Filter.Broadcast(),
            _announcer.GetEventLocaleString(_announcer.GetAnnouncementId("N14VertibirdCrash")),
            colorOverride: Color.Gold);

        // Загружаем обломки. Файл сохранён командой savemap (категория Map),
        // поэтому используем TryLoadGeneric: движок сам перенесёт грид и все
        // обломки на карту планеты с нужным смещением и удалит лишний map-entity.
        var loadOpts = new MapLoadOptions
        {
            MergeMap = mapId,
            Offset = offset,
        };

        if (!_mapLoader.TryLoadGeneric(new ResPath(comp.WreckPath), out _, out var grids, loadOpts)
            || grids.Count == 0)
        {
            Sawmill.Error($"Vertibird crash failed to load wreck grid {comp.WreckPath}");
            return;
        }

        EntityUid wreckUid = EntityUid.Invalid;
        Box2 wreckAabb = default;
        foreach (var g in grids)
        {
            wreckUid = g.Owner;
            wreckAabb = g.Comp.LocalAABB;
            break;
        }

        // Очищаем место падения: удаляем всё (камни, деревья, мусор), что оказалось
        // в габаритах обломков, чтобы вертибёрд не срастался с местностью.
        var cleared = ClearCrashSite(wreckUid, wreckAabb.Translated(offset), mapId);
        Sawmill.Info($"Vertibird crash: cleared {cleared} entities at the crash site");

        var center = new MapCoordinates(offset, mapId);

        // Варп-поинт в точке крушения — ориентир для призраков и админов.
        if (!string.IsNullOrEmpty(comp.WarpPointPrototype))
        {
            var warp = Spawn(comp.WarpPointPrototype, center);

            if (TryComp<WarpPointComponent>(warp, out var warpComp))
                warpComp.Location = Loc.GetString("n14-vertibird-warp-location");
        }
    }

    /// <summary>
    ///     Ищет якорный грид для выбора точки падения: сначала спавн-станции,
    ///     затем (если станций нет — типично для карт N14) крупнейший грид на любой карте.
    /// </summary>
    private bool TryGetAnchor(out EntityUid anchor, out MapId mapId)
    {
        var grids = new HashSet<EntityUid>();
        foreach (var station in _gameTicker.GetSpawnableStations())
        {
            if (TryComp<StationDataComponent>(station, out var data) && _station.GetLargestGrid(data) is { } grid)
                grids.Add(grid);
        }

        if (grids.Count > 0)
        {
            anchor = _random.Pick(grids);
            mapId = Transform(anchor).MapID;
            if (_mapSystem.MapExists(mapId))
                return true;
        }

        // Фолбэк: крупнейший грид среди всех карт (поверхность планеты).
        EntityUid? best = null;
        var bestArea = 0f;

        var query = EntityQueryEnumerator<MapGridComponent>();
        while (query.MoveNext(out var gridUid, out var grid))
        {
            var area = grid.LocalAABB.Width * grid.LocalAABB.Height;
            if (area <= bestArea)
                continue;

            bestArea = area;
            best = gridUid;
        }

        if (best is not { } chosen || bestArea < MinAnchorGridTiles)
        {
            anchor = EntityUid.Invalid;
            mapId = MapId.Nullspace;
            return false;
        }

        anchor = chosen;
        mapId = Transform(anchor).MapID;
        return _mapSystem.MapExists(mapId);
    }

    /// <summary>
    ///     Удаляет сущности, оказавшиеся в габаритах обломков. Игроки, другие гриды
    ///     и сами обломки вертибёрда не затрагиваются.
    /// </summary>
    private int ClearCrashSite(EntityUid wreckUid, Box2 worldAabb, MapId mapId)
    {
        var intersecting = new HashSet<Entity<TransformComponent>>();
        _lookup.GetEntitiesIntersecting(mapId, new Box2Rotated(worldAabb), intersecting,
            LookupFlags.Uncontained);

        var removed = 0;
        foreach (var ent in intersecting)
        {
            if (ent.Owner == wreckUid || HasComp<MapGridComponent>(ent))
                continue;

            // Игроков не удаляем.
            if (HasComp<ActorComponent>(ent))
                continue;

            // Сущности самого вертибёрда (заякорены на его грид) не трогаем.
            if (IsDescendantOf(ent.Owner, wreckUid))
                continue;

            QueueDel(ent.Owner);
            removed++;
        }

        return removed;
    }

    private bool IsDescendantOf(EntityUid ent, EntityUid ancestor)
    {
        var current = Transform(ent).ParentUid;
        while (current.IsValid())
        {
            if (current == ancestor)
                return true;

            current = Transform(current).ParentUid;
        }

        return false;
    }

    /// <summary>
    ///     Ищет точку крушения перебором тайлов поверхности в кольце допустимых дистанций
    ///     вокруг поселения. Приоритет: предпочитаемый тайл, на который целиком помещается
    ///     обломок → любой тайл поверхности → случайная точка.
    /// </summary>
    private bool TryFindCrashSpot(EntityUid anchor, MapId mapId, N14VertibirdCrashRuleComponent comp, out Vector2 spot)
    {
        spot = default;
        if (!TryComp<MapGridComponent>(anchor, out var grid))
            return false;

        var anchorPos = _xform.GetWorldPosition(anchor);
        var box = new Box2(
            anchorPos - new Vector2(comp.MaxDistance, comp.MaxDistance),
            anchorPos + new Vector2(comp.MaxDistance, comp.MaxDistance));

        var preferred = new List<Vector2>();
        var surface = new List<Vector2>();

        foreach (var tile in grid.GetTilesIntersecting(box, ignoreEmpty: true))
        {
            var pos = _mapSystem.GridTileToWorldPos(anchor, grid, tile.GridIndices);
            var dist = Vector2.Distance(pos, anchorPos);
            if (dist < comp.MinDistance || dist > comp.MaxDistance)
                continue;

            surface.Add(pos);

            var def = tile.Tile.GetContentTileDefinition();
            if (def != null && comp.PreferredTiles.Contains(def.ID))
                preferred.Add(pos);
        }

        // 1: предпочитаемый тайл, на который помещается весь обломок.
        _random.Shuffle(preferred);
        foreach (var candidate in preferred)
        {
            if (CanFitWreck(new MapCoordinates(candidate, mapId), comp))
            {
                spot = candidate;
                return true;
            }
        }

        // 2: любой тайл поверхности в радиусе — лучше, чем рисковать пустотой.
        if (surface.Count > 0)
        {
            spot = _random.Pick(surface);
            Sawmill.Info("Vertibird crash: no preferred spot with clearance, using random surface tile");
            return true;
        }

        // 3: поверхности в радиусе нет вовсе — случайная точка как есть.
        spot = anchorPos + _random.NextVector2(comp.MinDistance, comp.MaxDistance);
        Sawmill.Warning("Vertibird crash: no surface tiles found in range, using blind random point");
        return true;
    }

    /// <summary>
    ///     Проверяет, что обломок целиком помещается: центр и четыре точки по краям
    ///     должны стоять на предпочитаемых тайлах поверхности.
    /// </summary>
    private bool CanFitWreck(MapCoordinates center, N14VertibirdCrashRuleComponent comp)
    {
        var r = comp.WreckClearanceRadius;

        return IsOnPreferredTile(new MapCoordinates(center.Position, center.MapId), comp)
            && IsOnPreferredTile(new MapCoordinates(center.Position + new Vector2(r, 0), center.MapId), comp)
            && IsOnPreferredTile(new MapCoordinates(center.Position - new Vector2(r, 0), center.MapId), comp)
            && IsOnPreferredTile(new MapCoordinates(center.Position + new Vector2(0, r), center.MapId), comp)
            && IsOnPreferredTile(new MapCoordinates(center.Position - new Vector2(0, r), center.MapId), comp);
    }

    /// <summary>
    ///     Проверяет, находится ли точка на одном из предпочитаемых тайлов.
    /// </summary>
    private bool IsOnPreferredTile(MapCoordinates coords, N14VertibirdCrashRuleComponent comp)
    {
        if (comp.PreferredTiles.Count == 0)
            return false;

        if (!MapManager.TryFindGridAt(coords, out var gridUid, out var grid))
            return false;

        var tileIndices = _mapSystem.WorldToTile(gridUid, grid, coords.Position);
        if (!_mapSystem.TryGetTileRef(gridUid, grid, tileIndices, out var tileRef))
            return false;

        var def = tileRef.Tile.GetContentTileDefinition();
        return def != null && comp.PreferredTiles.Contains(def.ID);
    }
}
