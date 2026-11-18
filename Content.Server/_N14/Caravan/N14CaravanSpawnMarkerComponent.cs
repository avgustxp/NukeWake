namespace Content.Server._N14.Caravan;

/// <summary>
/// A marker that spawns a caravan. The caravan walks the numbered
/// <see cref="N14CaravanRoutePointComponent"/> markers and is removed
/// at an optional <see cref="N14CaravanDespawnMarkerComponent"/>.
/// </summary>
[RegisterComponent, Access(typeof(N14CaravanSystem))]
public sealed partial class N14CaravanSpawnMarkerComponent : Component;