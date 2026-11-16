using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Robust.Shared.GameObjects;

namespace Content.Shared._N14.Clothing;

/// <summary>
/// Проверяет <see cref="ClothingRequiresWearingComponent"/> при попытке надеть предмет.
/// </summary>
public sealed class ClothingRequiresWearingSystem : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClothingRequiresWearingComponent, BeingEquippedAttemptEvent>(OnBeingEquipped);
    }

    private void OnBeingEquipped(Entity<ClothingRequiresWearingComponent> ent, ref BeingEquippedAttemptEvent args)
    {
        if (args.Cancelled || IsRequirementMet(ent.Comp, args.Equipee))
            return;

        args.Cancel();
        args.Reason = "clothing-requires-wearing-failed";
    }

    private bool IsRequirementMet(ClothingRequiresWearingComponent component, EntityUid wearer)
    {
        var enumerator = _inventory.GetSlotEnumerator(wearer, component.Slots);

        while (enumerator.NextItem(out var item))
        {
            var prototype = MetaData(item).EntityPrototype;

            if (prototype != null && component.RequiredClothing.Contains(prototype.ID))
                return true;
        }

        return false;
    }
}
