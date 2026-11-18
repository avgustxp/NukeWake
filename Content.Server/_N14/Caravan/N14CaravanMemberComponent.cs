using Robust.Shared.GameObjects;

namespace Content.Server._N14.Caravan;

/// <summary>
/// Attached to every member of a caravan so back-references to the caravan
/// (the entity holding the <see cref="N14CaravanComponent"/>) are easy to find,
/// e.g. to propagate retaliation between guards.
/// </summary>
[RegisterComponent, Access(typeof(N14CaravanSystem))]
public sealed partial class N14CaravanMemberComponent : Component
{
    /// <summary>Entity holding the <see cref="N14CaravanComponent"/> (the leading guard).</summary>
    [ViewVariables]
    public EntityUid? Caravan;
}