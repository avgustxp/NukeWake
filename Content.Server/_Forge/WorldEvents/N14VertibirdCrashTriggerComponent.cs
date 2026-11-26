namespace Content.Server._Forge.WorldEvents;

/// <summary>
///     Компонент предмета, принудительно запускающего крушение вертибёрда при использовании в руке.
/// </summary>
[RegisterComponent]
public sealed partial class N14VertibirdCrashTriggerComponent : Component
{
    /// <summary>
    ///     ID прототипа правила крушения, которое запускает этот предмет.
    /// </summary>
    [DataField]
    public string RuleId = "N14VertibirdCrash";
}
