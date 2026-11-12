using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Shared.Light.EntitySystems;

/// <summary>
/// Handles the roof flag for tiles that gets used for the RoofOverlay.
/// </summary>
public abstract class SharedRoofSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    private HashSet<Entity<IsRoofComponent>> _roofSet = new();

    /// <summary>
    ///     Number of entities with <see cref="IsRoofComponent"/>. Almost all maps use the per-tile
    ///     bitmask instead, so when this is zero we can skip the entity lookup entirely.
    /// </summary>
    private int _roofEntities;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IsRoofComponent, ComponentStartup>(OnRoofEntityStartup);
        SubscribeLocalEvent<IsRoofComponent, ComponentShutdown>(OnRoofEntityShutdown);
    }

    private void OnRoofEntityStartup(Entity<IsRoofComponent> ent, ref ComponentStartup args)
    {
        _roofEntities++;
    }

    private void OnRoofEntityShutdown(Entity<IsRoofComponent> ent, ref ComponentShutdown args)
    {
        _roofEntities--;
    }

    /// <summary>
    /// Returns whether the specified tile is roof-occupied.
    /// </summary>
    /// <returns>Returns false if no data or not rooved.</returns>
    public bool IsRooved(Entity<MapGridComponent, RoofComponent> grid, Vector2i index)
    {
        var roof = grid.Comp2;
        var chunkOrigin = SharedMapSystem.GetChunkIndices(index, RoofComponent.ChunkSize);

        if (roof.Data.TryGetValue(chunkOrigin, out var bitMask))
        {
            var chunkRelative = SharedMapSystem.GetChunkRelative(index, RoofComponent.ChunkSize);
            var bitFlag = (ulong) 1 << (chunkRelative.X + chunkRelative.Y * RoofComponent.ChunkSize);

            var isRoof = (bitMask & bitFlag) == bitFlag;

            // Early out, otherwise check for components on tile.
            if (isRoof)
                return true;
        }

        // Fast path: nothing in the world can mark a tile as roofed, don't do a lookup per tile.
        if (_roofEntities <= 0)
            return false;

        _roofSet.Clear();
        _lookup.GetLocalEntitiesIntersecting(grid.Owner, index, _roofSet);

        foreach (var isRoofEnt in _roofSet)
        {
            if (!isRoofEnt.Comp.Enabled)
                continue;

            return true;
        }

        return false;
    }

    public void SetRoof(Entity<MapGridComponent?, RoofComponent?> grid, Vector2i index, bool value)
    {
        if (!Resolve(grid, ref grid.Comp1, ref grid.Comp2, false))
            return;

        var chunkOrigin = SharedMapSystem.GetChunkIndices(index, RoofComponent.ChunkSize);
        var roof = grid.Comp2;

        if (!roof.Data.TryGetValue(chunkOrigin, out var chunkData))
        {
            // No value to remove so leave it.
            if (!value)
            {
                return;
            }

            chunkData = 0;
        }

        var chunkRelative = SharedMapSystem.GetChunkRelative(index, RoofComponent.ChunkSize);
        var bitFlag = (ulong) 1 << (chunkRelative.X + chunkRelative.Y * RoofComponent.ChunkSize);

        if (value)
        {
            // Already set
            if ((chunkData & bitFlag) == bitFlag)
                return;

            chunkData |= bitFlag;
        }
        else
        {
            // Not already set
            if ((chunkData & bitFlag) == 0x0)
                return;

            chunkData &= ~bitFlag;
        }

        roof.Data[chunkOrigin] = chunkData;
        Dirty(grid.Owner, roof);
    }
}
