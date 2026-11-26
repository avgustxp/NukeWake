using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._Forge.WorldEvents;

/// <summary>
///     Правило случайного крушения вертибёрда: в назначенный момент времени на карту
///     возле поселения загружается грид обломков и происходит broadcast-анонс.
/// </summary>
[RegisterComponent, Access(typeof(N14VertibirdCrashRuleSystem))]
public sealed partial class N14VertibirdCrashRuleComponent : Component
{
    /// <summary>
    ///     Путь к карте обломков вертибёрда.
    /// </summary>
    [DataField]
    public string WreckPath = "/Maps/N14/VertibirdWreck.yml";

    /// <summary>
    ///     Минимальная дистанция точки падения от якорного грида поселения.
    /// </summary>
    [DataField]
    public float MinDistance = 100f;

    /// <summary>
    ///     Максимальная дистанция точки падения от якорного грида поселения.
    /// </summary>
    [DataField]
    public float MaxDistance = 300f;

    /// <summary>
    ///     Минимальная задержка до крушения после старта раунда, в секундах.
    /// </summary>
    [DataField]
    public float MinStartDelay = 600f;

    /// <summary>
    ///     Максимальная задержка до крушения после старта раунда, в секундах.
    /// </summary>
    [DataField]
    public float MaxStartDelay = 3600f;

    /// <summary>
    ///     Звук пролетающего самолёта перед крушением.
    /// </summary>
    [DataField]
    public SoundSpecifier? ApproachSound = new SoundPathSpecifier("/Audio/_Nuclear14/Effects/airplane_fly_by.ogg");

    /// <summary>
    ///     Тайлы, на которых вертибёрд предпочитает падать. Если подходящего места
    ///     не нашлось, используется любой тайл поверхности в радиусе.
    /// </summary>
    [DataField]
    public List<string> PreferredTiles = new()
    {
        "N14FloorConcrete", // бетон
        "N14FloorConcreteDark", // тёмный бетон
        "FloorWasteland", // пустошь (все варианты)
    };

    /// <summary>
    ///     Радиус свободного места (в метрах) вокруг центра падения, необходимый обломку.
    /// </summary>
    [DataField]
    public float WreckClearanceRadius = 10f;

    /// <summary>
    ///     Прототип варп-поинта, создаваемого в точке крушения (для телепорта призраков).
    ///     Пустая строка — не создавать.
    /// </summary>
    [DataField]
    public string WarpPointPrototype = "WarpPoint";

    /// <summary>
    ///     Минимальное расстояние (в тайлах) до ближайшего игрока. Точка крушения
    ///     выбирается только там, где в этом радиусе нет игроков.
    /// </summary>
    [DataField]
    public float MinPlayerClearance = 15f;

    /// <summary>
    ///     Прототип дыма, выпускаемого в точке крушения (как у дымовой гранаты).
    /// </summary>
    [DataField]
    public EntProtoId SmokePrototype = "Smoke";

    /// <summary>
    ///     Сколько секунд держится дым после крушения.
    /// </summary>
    [DataField]
    public float SmokeDuration = 30f;

    /// <summary>
    ///     Насколько сильно расползается дым (spreadAmount дымовой гранаты).
    /// </summary>
    [DataField]
    public int SmokeSpreadAmount = 50;

    /// <summary>
    ///     Прототип напольного огня, которым хаотично охватывается окрестность.
    /// </summary>
    [DataField]
    public EntProtoId FirePrototype = "ForgeSurfaceFire";

    /// <summary>
    ///     Сколько секунд горит каждый очаг огня.
    /// </summary>
    [DataField]
    public TimeSpan FireDuration = TimeSpan.FromSeconds(40);

    /// <summary>
    ///     Интенсивность очагов огня.
    /// </summary>
    [DataField]
    public float FireIntensity = 1f;

    /// <summary>
    ///     Радиус (в тайлах) вокруг точки крушения, в котором поджигается земля.
    /// </summary>
    [DataField]
    public float FireRadius = 8f;

    /// <summary>
    ///     Сколько случайных попыток поджога делается в радиусе FireRadius.
    /// </summary>
    [DataField]
    public int FireAttempts = 120;

    /// <summary>
    ///     Момент времени, когда произойдёт крушение.
    /// </summary>
    public TimeSpan CrashAt;

    /// <summary>
    ///     Произошло ли уже крушение в этом раунде.
    /// </summary>
    public bool Crashed;
}
