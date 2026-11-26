using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// Marks the boss of a raider checkpoint. The boss demands a toll (caps) from
/// players who offer an item through the F key; players who have paid are stored
/// in <see cref="PaidUsers"/> so checkpoint markers let them pass.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderCheckpointComponent : Component
{
    /// <summary>
    /// Links the boss, all garrison members and gate markers of one checkpoint.
    /// Entities are grouped together by this id at runtime.
    /// </summary>
    [DataField("checkpointId")]
    public string CheckpointId = string.Empty;

    /// <summary>How many caps the boss demands to open the gate.</summary>
    [DataField("paymentAmount")]
    public int PaymentAmount = 50;

    /// <summary>Stack prototype id of the accepted currency.</summary>
    [DataField("currencyStack")]
    public string CurrencyStackType = "Caps";

    /// <summary>
    /// Character pawns the boss has already told about the toll. Keyed by the
    /// player's entity, so a brand new character is greeted again even when the
    /// same player session brings them.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> GreetedCharacters = new();

    /// <summary>Player session user IDs who have paid the toll to this boss.</summary>
    [ViewVariables]
    public HashSet<NetUserId> PaidUsers = new();

    /// <summary>Last offer that was processed, to avoid handling the same offer twice.</summary>
    [ViewVariables]
    public (EntityUid Target, EntityUid Item)? HandledOffer;
}