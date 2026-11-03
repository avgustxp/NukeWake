using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Forge.Merchant;

/// <summary>
///     Помечает игрока, у которого открыто окно торговца: движение заблокировано до закрытия окна.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class N14MerchantTradeUserComponent : Component
{
    /// <summary>
    ///     Торговцы, окна которых сейчас открыты у игрока. Серверное поле:
    ///     компонент снимается только после закрытия последнего окна.
    /// </summary>
    public readonly HashSet<EntityUid> OpenMerchants = new();
}

/// <summary>
///     Неподвижный торговец. Открывает своё окно по клику (см. <c>ActivatableUI</c> в YAML).
/// </summary>
[RegisterComponent]
public sealed partial class N14MerchantComponent : Component
{
    /// <summary>
    ///     Игрок, у которого сейчас открыто окно торговца. Пока окно открыто,
    ///     другие игроки открыть его не могут (одно окно на торговца).
    /// </summary>
    public EntityUid? CurrentUser;

    [DataField("merchantName")]
    public string MerchantName = "Merchant";

    /// <summary>
    ///     Капсы на счёте торговца. Увеличиваются при покупке у игрока, уменьшаются при продаже.
    /// </summary>
    [DataField("caps")]
    [ViewVariables(VVAccess.ReadWrite)]
    public int Caps;

    /// <summary>
    ///     Что торговец ПРОДАЁТ. Игрок платит <see cref="N14MerchantListing.Price"/>.
    /// </summary>
    [DataField("stock")]
    public List<N14MerchantListing> Stock = new();

    /// <summary>
    ///     Что торговец ПОКУПАЕТ у игрока. Игроку платят <see cref="N14MerchantListing.Price"/>.
    /// </summary>
    [DataField("wants")]
    public List<N14MerchantListing> Wants = new();

    /// <summary>
    ///     Наборы-пулы, из которых торговец собирает ассортимент.
    ///     Записи всех перечисленных наборов объединяются в один пул, и при каждом
    ///     обновлении из него случайно выбирается <see cref="StockSize"/> позиций.
    /// </summary>
    [DataField("stockSets")]
    public List<ProtoId<N14MerchantStockSetPrototype>> StockSets = new();

    /// <summary>
    ///     Сколько позиций торговец набирает из пула при создании и обновлении ассортимента.
    /// </summary>
    [DataField("stockSize")]
    public int StockSize = 10;

    /// <summary>
    ///     Сколько экземпляров одного товара торговец выставляет за цикл.
    ///     После продажи всех экземпляров позиция пропадает до обновления ассортимента.
    ///     0 или меньше — без ограничения.
    /// </summary>
    [DataField("stockPerItem")]
    public int StockPerItem = 3;

    /// <summary>
    ///     Интервал обновления ассортимента в секундах. 0 или меньше — не обновлять.
    /// </summary>
    [DataField("restockSeconds")]
    public int RestockSeconds = 1200;

    /// <summary>
    ///     Накопитель времени до следующего обновления. Не сохраняется в карту.
    /// </summary>
    public float RestockTimer;

    /// <summary>
    ///     Lower bound for the random starting caps roll. Negative = use static <see cref="Caps"/>.
    /// </summary>
    [DataField("capsMin")]
    public int CapsMin = -1;

    /// <summary>
    ///     Upper bound for the random starting caps roll (inclusive).
    /// </summary>
    [DataField("capsMax")]
    public int CapsMax = -1;
}

/// <summary>
///     Одна позиция в ассортименте/запросах торговца.
/// </summary>
[DataRecord]
public partial record struct N14MerchantListing()
{
    /// <summary>
    ///     Прототип товара.
    /// </summary>
    [DataField("product", required: true)]
    public EntProtoId Product { get; set; }

    /// <summary>
    ///     Цена в капсах.
    /// </summary>
    [DataField("price", required: true)]
    public int Price { get; set; }

    /// <summary>
    ///     Остаток. -1 = бесконечно.
    /// </summary>
    [DataField("remaining")]
    public int Remaining { get; set; } = -1;

    /// <summary>
    ///     Позиция выкуплена у игрока. Такие позиции не удаляются при обновлении
    ///     ассортимента и остаются в продаже до тех пор, пока их не купят.
    /// </summary>
    [DataField("playerSold")]
    public bool PlayerSold { get; set; }

    /// <summary>
    ///     Прочность оружия на момент продажи торговцу (в единицах компонента).
    ///     -1 — прочность не сохранялась (не оружие или товар из прототипа).
    /// </summary>
    [DataField("condition")]
    public float Condition { get; set; } = -1f;
}
