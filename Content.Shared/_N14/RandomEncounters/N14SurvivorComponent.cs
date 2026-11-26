using Robust.Shared.GameObjects;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// Marks a "wasteland survivor": a neutral beggar that greets the first player
/// who approaches, accepts a bottle of clean water offered through the F key,
/// says thanks and is removed once no player is in view anymore.
/// </summary>
[RegisterComponent]
public sealed partial class N14SurvivorComponent : Component
{
    /// <summary>Whether the survivor has already greeted a player.</summary>
    [ViewVariables]
    public bool Greeted;

    /// <summary>A player has been within view range at least once.</summary>
    [ViewVariables]
    public bool EverSeen;

    /// <summary>Whether the survivor has received clean water.</summary>
    [ViewVariables]
    public bool HasReceivedWater;

    /// <summary>Last offer that was processed, to avoid handling the same offer twice.</summary>
    [ViewVariables]
    public (EntityUid Target, EntityUid Item)? HandledOffer;
}