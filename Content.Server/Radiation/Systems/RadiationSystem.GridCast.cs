using System.Numerics;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Systems;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Radiation.Systems;

// main algorithm that fires radiation rays to targets
public partial class RadiationSystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    /// <summary>
    ///     Static empty list, so the debug overlay doesn't need a new allocation when it is disabled.
    /// </summary>
    private static readonly List<RadiationRay> NoRays = new();

    /// <summary>
    ///     Everything the gridcast needs to know about a radiation source.
    ///     Calculated once per update instead of for every source-receiver pair.
    /// </summary>
    private readonly struct SourceData
    {
        public readonly EntityUid Uid;
        public readonly TransformComponent Transform;
        public readonly Vector2 WorldPosition;

        /// <summary>
        ///     Intensity after stack count and radiation blocking containers.
        /// </summary>
        public readonly float Intensity;

        public readonly float Slope;

        /// <summary>
        ///     Squared distance after which the source is guaranteed to be below <see cref="MinIntensity"/>.
        /// </summary>
        public readonly float MaxRangeSquared;

        public SourceData(EntityUid uid, TransformComponent transform, Vector2 worldPosition,
            float intensity, float slope, float maxRangeSquared)
        {
            Uid = uid;
            Transform = transform;
            WorldPosition = worldPosition;
            Intensity = intensity;
            Slope = slope;
            MaxRangeSquared = maxRangeSquared;
        }
    }

    // sources grouped by map, so receivers don't have to look at other maps at all
    private readonly Dictionary<MapId, List<SourceData>> _sourcesByMap = new();

    // reusable buffers: the update path doesn't allocate anything
    private readonly List<(EntityUid Uid, float Rads)> _receiverBuffer = new();
    private List<Entity<MapGridComponent>> _gridsBuffer = new();
    private readonly List<RadiationBlockingContainerComponent> _blockingContainersBuffer = new();
    private readonly Stopwatch _stopwatch = new();

    private EntityQuery<RadiationGridResistanceComponent> _resistanceQuery;
    private EntityQuery<RadiationReceiverComponent> _receiverQuery;
    private EntityQuery<TransformComponent> _transformQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<StackComponent> _stackQuery;
    private EntityQuery<RadiationBlockingContainerComponent> _radiationBlockingContainers;

    private void InitGridcast()
    {
        _resistanceQuery = GetEntityQuery<RadiationGridResistanceComponent>();
        _receiverQuery = GetEntityQuery<RadiationReceiverComponent>();
        _transformQuery = GetEntityQuery<TransformComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();
        _radiationBlockingContainers = GetEntityQuery<RadiationBlockingContainerComponent>();
    }

    private void UpdateGridcast()
    {
        // should we save debug information into rays?
        // if there is no debug session connected - just ignore it
        var saveVisitedTiles = _debugSessions.Count > 0;

        if (saveVisitedTiles)
            _stopwatch.Restart();

        BuildSources();

        var rays = saveVisitedTiles ? new List<RadiationRay>() : NoRays;
        var receiversCount = 0;

        var receivers = EntityQueryEnumerator<RadiationReceiverComponent, TransformComponent>();
        while (receivers.MoveNext(out var destUid, out _, out var destTrs))
        {
            receiversCount++;

            // no sources on this map at all - the receiver is clean
            if (!_sourcesByMap.TryGetValue(destTrs.MapID, out var sources) || sources.Count == 0)
            {
                _receiverBuffer.Add((destUid, 0f));
                continue;
            }

            var destWorld = _transform.GetWorldPosition(destTrs, _transformQuery);
            var rads = 0f;

            foreach (var source in sources)
            {
                var delta = destWorld - source.WorldPosition;
                var distanceSquared = delta.LengthSquared();

                // cheap early out: this source can't reach that far
                if (distanceSquared > source.MaxRangeSquared)
                    continue;

                var distance = MathF.Sqrt(distanceSquared);
                var incomingRads = source.Intensity - source.Slope * distance;
                if (incomingRads <= MinIntensity)
                    continue;

                RadiationRay? ray = null;
                if (saveVisitedTiles)
                {
                    ray = new RadiationRay(destTrs.MapID, GetNetEntity(source.Uid), source.WorldPosition,
                        GetNetEntity(destUid), destWorld, incomingRads);
                }

                // send ray towards destination entity
                var remainingRads = Irradiate(source, destTrs, destWorld, incomingRads, ray);

                if (ray != null)
                    rays.Add(ray);

                // add rads to total rad exposure
                if (remainingRads > 0f)
                    rads += remainingRads;
            }

            // Apply modifier if the destination entity is hidden within a radiation blocking container
            if (rads > 0f)
                rads = GetAdjustedRadiationIntensity(destUid, rads);

            _receiverBuffer.Add((destUid, rads));
        }

        // the stopwatch is restarted at the beginning of the update, no need to stop it
        var elapsedTime = saveVisitedTiles ? _stopwatch.Elapsed.TotalMilliseconds : 0d;

        // update information for debug overlay
        UpdateGridcastDebugOverlay(elapsedTime, saveVisitedTiles ? CountSources() : 0, receiversCount, rays);

        // send rads to each entity, after the query, because event handlers may modify entities
        foreach (var (uid, rads) in _receiverBuffer)
        {
            if (!_receiverQuery.TryGetComponent(uid, out var receiver))
                continue;

            // update radiation value of receiver
            // if no radiation rays reached target, that will set it to 0
            receiver.CurrentRadiation = rads;

            // also send an event with combination of total rad
            if (rads > 0f)
                IrradiateEntity(uid, rads, GridcastUpdateRate);
        }

        _receiverBuffer.Clear();

        if (receiversCount == 0)
            return;

        // raise broadcast event that radiation system has updated
        RaiseLocalEvent(new RadiationSystemUpdatedEvent());
    }

    /// <summary>
    ///     Prepares all enabled radiation sources for a single update.
    /// </summary>
    private void BuildSources()
    {
        foreach (var list in _sourcesByMap.Values)
        {
            list.Clear();
        }

        var maxDistance = GridcastMaxDistance;
        var sources = EntityQueryEnumerator<RadiationSourceComponent, TransformComponent>();

        while (sources.MoveNext(out var uid, out var source, out var sourceTrs))
        {
            if (!source.Enabled)
                continue;

            var intensity = source.Intensity;
            if (_stackQuery.TryGetComponent(uid, out var stack))
                intensity *= _stack.GetCount(uid, stack);

            // radiation blocking containers don't change during a tick, so subtract them once
            intensity = GetAdjustedRadiationIntensity(uid, intensity);

            if (intensity <= MinIntensity)
                continue;

            var slope = source.Slope;
            var range = slope > 0f ? (intensity - MinIntensity) / slope : maxDistance;
            if (range > maxDistance)
                range = maxDistance;

            if (!_sourcesByMap.TryGetValue(sourceTrs.MapID, out var list))
            {
                list = new List<SourceData>();
                _sourcesByMap[sourceTrs.MapID] = list;
            }

            list.Add(new SourceData(uid, sourceTrs,
                _transform.GetWorldPosition(sourceTrs, _transformQuery),
                intensity, slope, range * range));
        }
    }

    private int CountSources()
    {
        var count = 0;
        foreach (var list in _sourcesByMap.Values)
        {
            count += list.Count;
        }

        return count;
    }

    /// <summary>
    ///     Traces a single ray through every grid between source and destination.
    /// </summary>
    /// <returns>Radiation left after all blockers, or 0 if the ray was blocked completely.</returns>
    private float Irradiate(SourceData source, TransformComponent destTrs, Vector2 destWorld,
        float rads, RadiationRay? ray)
    {
        var sourceTrs = source.Transform;

        // if source and destination are on the same grid it's possible that
        // between them can be another grid (ie. shuttle in center of donut station)
        // however we can do simplification and ignore that case
        if (GridcastSimplifiedSameGrid && sourceTrs.GridUid != null && sourceTrs.GridUid == destTrs.GridUid)
        {
            if (!_gridQuery.TryGetComponent(sourceTrs.GridUid.Value, out var grid))
                return rads;

            return Gridcast((sourceTrs.GridUid.Value, grid), source, destTrs, destWorld, rads, ray);
        }

        // lets check how many grids are between source and destination
        // do a box intersection test between target and destination
        // it's not very precise, but really cheap
        var box = Box2.FromTwoPoints(source.WorldPosition, destWorld);
        _gridsBuffer.Clear();
        _mapManager.FindGridsIntersecting(sourceTrs.MapID, box, ref _gridsBuffer, true);

        // gridcast through each grid and try to hit some radiation blockers
        // the ray will be updated with each grid that has some blockers
        foreach (var grid in _gridsBuffer)
        {
            rads = Gridcast(grid, source, destTrs, destWorld, rads, ray);

            // looks like last grid blocked all radiation
            // we can return right now
            if (rads <= 0f)
                return 0f;
        }

        return rads;
    }

    private float Gridcast(Entity<MapGridComponent> grid, SourceData source, TransformComponent destTrs,
        Vector2 destWorld, float rads, RadiationRay? ray)
    {
        if (rads <= MinIntensity)
            return 0f;

        // if grid doesn't have resistance map just apply distance penalty
        if (!_resistanceQuery.TryGetComponent(grid.Owner, out var resistance))
            return rads;

        var resistanceMap = resistance.ResistancePerTile;

        // TODO Grid overlap. This currently assumes the grid is always parented directly to the map (local matrix == world matrix).
        // If ever grids are allowed to overlap, this might no longer be true. In that case, this should precompute and cache
        // inverse world matrices.
        var gridTrs = _transformQuery.GetComponent(grid.Owner);
        var invLocalMatrix = gridTrs.InvLocalMatrix;

        // get coordinate of source and destination in grid coordinates
        var sourceTrs = source.Transform;
        var srcLocal = sourceTrs.ParentUid == grid.Owner
            ? sourceTrs.LocalPosition
            : Vector2.Transform(source.WorldPosition, invLocalMatrix);

        var dstLocal = destTrs.ParentUid == grid.Owner
            ? destTrs.LocalPosition
            : Vector2.Transform(destWorld, invLocalMatrix);

        var invTileSize = 1f / grid.Comp.TileSize;
        var sourceGrid = new Vector2i(
            (int) MathF.Floor(srcLocal.X * invTileSize),
            (int) MathF.Floor(srcLocal.Y * invTileSize));

        var destGrid = new Vector2i(
            (int) MathF.Floor(dstLocal.X * invTileSize),
            (int) MathF.Floor(dstLocal.Y * invTileSize));

        // only debug overlay needs the visited tiles, don't waste memory on it otherwise
        var saveVisitedTiles = ray != null;
        List<(Vector2i, float)>? blockers = saveVisitedTiles ? new List<(Vector2i, float)>() : null;

        // iterate tiles in grid line from source to destination
        var line = new GridLineEnumerator(sourceGrid, destGrid);
        while (line.MoveNext())
        {
            var point = line.Current;
            if (!resistanceMap.TryGetValue(point, out var tileResistance))
                continue;

            rads -= tileResistance;

            // save data for debug
            if (blockers != null)
                blockers.Add((point, rads));

            // no intensity left after blocker
            if (rads <= MinIntensity)
            {
                rads = 0f;
                break;
            }
        }

        // save data for debug if needed
        if (blockers is { Count: > 0 })
            ray!.Blockers[GetNetEntity(grid.Owner)] = blockers;

        return rads;
    }

    private float GetAdjustedRadiationIntensity(EntityUid uid, float rads)
    {
        if (rads <= 0f)
            return 0f;

        _blockingContainersBuffer.Clear();
        if (_container.TryFindComponentsOnEntityContainerOrParent(uid, _radiationBlockingContainers, _blockingContainersBuffer))
        {
            foreach (var blocking in _blockingContainersBuffer)
            {
                rads -= blocking.RadResistance;
            }
        }

        return MathF.Max(rads, 0f);
    }
}
