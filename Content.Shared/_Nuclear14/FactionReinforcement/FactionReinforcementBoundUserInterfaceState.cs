using Robust.Shared.Serialization;

namespace Content.Shared._Nuclear14.FactionReinforcement;

[Serializable, NetSerializable]
public sealed class FactionReinforcementBoundUserInterfaceState : BoundUserInterfaceState
{
    public string DepartmentId = string.Empty;

    public List<FactionReinforcementJobEntry> Jobs = new();
}

[Serializable, NetSerializable]
public sealed class FactionReinforcementJobEntry
{
    public string Job = string.Empty;

    public FactionReinforcementStatus Status;
}

[Serializable, NetSerializable]
public enum FactionReinforcementStatus : byte
{
    Available,
    NotInStation,
    Unlimited,
    HasFreeSlots,
    NoHolders,
    HolderAlive,
    HolderHasId,
    NotFallen,
    Unavailable,
}
