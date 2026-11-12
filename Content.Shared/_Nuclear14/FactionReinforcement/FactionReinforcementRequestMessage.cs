using Robust.Shared.Serialization;

namespace Content.Shared._Nuclear14.FactionReinforcement;

[Serializable, NetSerializable]
public sealed class FactionReinforcementRequestMessage : BoundUserInterfaceMessage
{
    public string Job;

    public FactionReinforcementRequestMessage(string job)
    {
        Job = job;
    }
}
