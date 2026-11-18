namespace Content.Server._N14.Caravan;

/// <summary>
/// A marker that removes the caravan once it arrives.
/// Optional - if absent, the caravan is removed after the last route point.
/// </summary>
[RegisterComponent, Access(typeof(N14CaravanSystem))]
public sealed partial class N14CaravanDespawnMarkerComponent : Component;