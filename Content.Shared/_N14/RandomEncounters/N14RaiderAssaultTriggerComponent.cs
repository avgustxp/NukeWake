using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// An item that launches a raider assault on an attack point when used in hand.
/// Mirrors <c>N14VertibirdCrashTriggerComponent</c>: trigger items are handed out
/// by admins or events, while the point and spawners live in the map.
/// </summary>
[RegisterComponent]
public sealed partial class N14RaiderAssaultTriggerComponent : Component
{
    /// <summary>
    /// Attack point to assault (see <see cref="N14RaiderAssaultPointComponent.AttackPointId"/>).
    /// </summary>
    [DataField("attackPointId")]
    public string AttackPointId = "attack_point";
}
