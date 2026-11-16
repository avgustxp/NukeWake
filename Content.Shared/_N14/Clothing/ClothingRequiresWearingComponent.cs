using System.Collections.Generic;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._N14.Clothing;

/// <summary>
/// Не даёт надеть предмет, если на носителе нет ни одного из указанных предметов одежды.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ClothingRequiresWearingComponent : Component
{
    /// <summary>
    /// Слоты, в которых ищется требуемая одежда.
    /// </summary>
    [DataField]
    public SlotFlags Slots = SlotFlags.OUTERCLOTHING;

    /// <summary>
    /// Прототипы одежды, любой из которых должен быть надет.
    /// </summary>
    [DataField(required: true)]
    public HashSet<EntProtoId> RequiredClothing = new();
}
