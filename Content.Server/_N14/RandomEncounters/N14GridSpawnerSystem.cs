using Content.Shared._N14.RandomEncounters;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._N14.RandomEncounters;

/// <summary>
/// Loads the pre-mapped grid referenced by <see cref="N14GridSpawnerComponent.GridPath"/>
/// into the map when the spawner marker initializes. The grid lands with its local origin
/// on the spawner's position.
/// After placement the area is stamped: any static world entities of the target map inside
/// the grid's footprint are deleted and the outpost's own static entities are snapped and
/// anchored to its floor so it sits flush on the ground.
/// </summary>
public sealed class N14GridSpawnerSystem : EntitySystem
{
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<N14GridSpawnerComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<N14GridSpawnerComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.GridPath is not { } path)
            return;

        var xform = Transform(ent);
        if (xform.MapUid is not { } mapUid)
            return;

        var offset = _transform.GetWorldPosition(ent);
        var outpost = new HashSet<EntityUid> { ent.Owner };
        var loaded = false;

        // Snapshot the map's entities before loading so the difference tells us exactly
        // what the load added. This also covers prefabs saved without their own grid.
        var before = SnapshotMapEntities(mapUid);

        // Editor saves tend to be whole maps (category Map): merge the file's contents
        // (grid + anything placed at map level) onto this map at the spawner.
        if (_mapLoader.TryMergeMap(xform.MapID, path, out var merged, offset: offset, rot: ent.Comp.Rotation))
        {
            foreach (var uid in SnapshotMapEntities(mapUid))
                outpost.Add(uid);
            loaded = true;
            Log.Info($"N14GridSpawner merged map grid {path} at {offset}");
        }
        // A proper single-grid save (editor "Save Grid") loads directly.
        else if (_mapLoader.TryLoadGrid(xform.MapID, path, out var grid, offset: offset, rot: ent.Comp.Rotation))
        {
            var gridUid = grid.Value.Owner;
            outpost.Add(gridUid);
            var gridComp = Comp<MapGridComponent>(gridUid);
            var gridBox = _transform.GetWorldMatrix(gridUid).TransformBox(gridComp.LocalAABB);
            _lookup.GetEntitiesIntersecting(gridUid, gridBox, outpost);
            loaded = true;
            Log.Info($"N14GridSpawner loaded grid {path} at {offset}");
        }

        if (!loaded)
        {
            Log.Warning($"N14GridSpawner failed to load {path} while spawning {ToPrettyString(ent)}");
            return;
        }

        outpost.ExceptWith(before);
        StampArea(ent, outpost);
    }

    /// <summary>
    /// Returns every entity currently attached to the given map. Used to tell which
    /// entities a map merge added, since merges only report back the grids.
    /// </summary>
    private HashSet<EntityUid> SnapshotMapEntities(EntityUid mapUid)
    {
        var set = new HashSet<EntityUid>();
        foreach (var uid in EntityManager.GetEntities())
        {
            if (Deleted(uid))
                continue;

            if (Transform(uid).MapUid != mapUid)
                continue;

            set.Add(uid);
        }

        return set;
    }

    /// <summary>
    /// Wipes static world entities of the host map inside the loaded footprint and anchors
    /// the outpost's own static entities to the host grid (or the loaded grid's floor).
    /// </summary>
    private void StampArea(Entity<N14GridSpawnerComponent> marker, HashSet<EntityUid> outpost)
    {
        var footprint = Box2.Empty;
        foreach (var uid in outpost)
        {
            if (uid == marker.Owner || Deleted(uid))
                continue;

            if (HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid))
                continue;

            var box = _lookup.GetWorldAABB(uid);

            // Only solid, static pieces shape the stamp area; ignore small props, mobs
            // and anything dynamic.
            if (box.Width < 1f || box.Height < 1f)
                continue;

            if (TryComp<PhysicsComponent>(uid, out var physics) && physics.BodyType == BodyType.Dynamic)
                continue;

            footprint = footprint.IsEmpty() ? box : footprint.Union(box);
        }

        // A standalone grid save keeps its broadphase box even though we might not
        // measure one from the entities (e.g. grid set to empty in the file).
        if (footprint.IsEmpty())
        {
            foreach (var uid in outpost)
            {
                if (Deleted(uid))
                    continue;

                if (!TryComp(uid, out MapGridComponent? gridComp))
                    continue;

                var gridBox = _transform.GetWorldMatrix(uid).TransformBox(gridComp.LocalAABB);
                footprint = footprint.IsEmpty() ? gridBox : footprint.Union(gridBox);
            }
        }

        // Clear static world entities of the host map that the outpost now covers.
        var intersecting = _lookup.GetEntitiesIntersecting(Transform(marker.Owner).MapID, footprint.Enlarged(0.5f));
        foreach (var uid in intersecting)
        {
            if (outpost.Contains(uid) || Deleted(uid))
                continue;

            if (HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid))
                continue;

            var xform = Transform(uid);
            if (xform.MapUid != Transform(marker.Owner).MapUid)
                continue;

            // Only anchored static world pieces are wiped; players, loose items and
            // living things are left alone.
            if (!xform.Anchored)
                continue;

            // Entities whose prototype ids carry "Indestructible" survive the stamp.
            if (MetaData(uid).EntityPrototype?.ID is { } protoId &&
                protoId.Contains("Indestructible"))
                continue;

            Del(uid);
        }

        // Snap unanchored static entities of the outpost to the floor. They land at map
        // level for gridless prefabs, so anchoring onto the host grid is what seats them.
        foreach (var uid in outpost)
        {
            if (uid == marker.Owner || Deleted(uid))
                continue;

            if (HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid))
                continue;

            var xform = Transform(uid);
            if (xform.Anchored)
                continue;

            // Mobs (and anything else dynamic) must stay mobile.
            if (TryComp<PhysicsComponent>(uid, out var physics) && physics.BodyType == BodyType.Dynamic)
                continue;

            if (!_transform.AnchorEntity(uid, xform))
                Log.Debug($"N14GridSpawner could not anchor {ToPrettyString(uid)} to the floor");
        }
    }
}