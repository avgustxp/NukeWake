namespace Content.Server._N14.Caravan;

/// <summary>
/// A caravan route marker. Route order is determined by the digits
/// in the marker's name (e.g. "caravan point 1", "caravan point 2").
/// </summary>
[RegisterComponent, Access(typeof(N14CaravanSystem))]
public sealed partial class N14CaravanRoutePointComponent : Component;