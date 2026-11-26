using Content.Shared._N14.RandomEncounters;

namespace Content.Server._N14.RandomEncounters;

/// <summary>
/// Spawns the checkpoint mob referenced by <see cref="N14RaiderCheckpointSpawnerComponent"/>
/// when the marker initializes (i.e. when the grid/map it is placed on loads) and removes
/// the marker.
/// </summary>
public sealed class N14RaiderCheckpointSpawnerSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<N14RaiderCheckpointSpawnerComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<N14RaiderCheckpointSpawnerComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.Prototype.Length == 0)
        {
            Log.Error($"N14RaiderCheckpointSpawner {ToPrettyString(ent)} has no prototype set");
            return;
        }

        Spawn(ent.Comp.Prototype, _transform.GetMapCoordinates(ent));
        QueueDel(ent);
    }
}