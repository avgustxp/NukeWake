using Robust.Shared.Serialization;

namespace Content.Shared._Forge.Merchant;

/// <summary>
///     Ключ UI торговца. Используется и в YAML (<c>key: enum.N14MerchantUiKey.Key</c>), и в системах.
/// </summary>
[Serializable, NetSerializable]
public enum N14MerchantUiKey : byte
{
    Key = 0,
}

/// <summary>
///     Состояние окна торговца: имя, капсы игрока и торговца, ассортимент и запросы.
/// </summary>
[Serializable, NetSerializable]
public sealed class N14MerchantUiState : BoundUserInterfaceState
{
    public string MerchantName = "Merchant";

    /// <summary>Капы торговца (сколько он может заплатить за наши товары).</summary>
    public int MerchantCaps;

    /// <summary>Капсы игрока.</summary>
    public int PlayerCaps;

    public List<N14MerchantItemState> Offers = new();
    public List<N14MerchantItemState> Requests = new();
}

/// <summary>
///     Одна отображаемая строка в колонке торговца или игрока.
/// </summary>
[Serializable, NetSerializable]
public sealed class N14MerchantItemState
{
    /// <summary>Стабильный id строки в рамках текущего состояния.</summary>
    public string Id = string.Empty;

    /// <summary>Прототип товара.</summary>
    public string Proto = string.Empty;

    /// <summary>Локализованное имя для показа.</summary>
    public string Name = string.Empty;

    public int Price;

    /// <summary>Остаток/доступное количество (-1 = ∞).</summary>
    public int Count = -1;

    /// <summary>Индекс в <see cref="N14MerchantComponent.Stock"/>. Для запросов не используется.</summary>
    public int Index;

    /// <summary>Condition of the weapon 0..1, or -1 if it has no condition component.</summary>
    public float Condition = -1f;

    /// <summary>Caliber string, e.g. ".45-70". Empty = not a firearm.</summary>
    public string Caliber = string.Empty;

    /// <summary>Rounds currently in the magazine/drum.</summary>
    public int AmmoInMag;

    /// <summary>Magazine/drum capacity.</summary>
    public int MagCapacity;

    /// <summary>Weapon is powered by a battery: <see cref="AmmoInMag"/>/<see cref="MagCapacity"/> hold shots (charge).</summary>
    public bool IsEnergyWeapon;

    /// <summary>Rounds of that caliber in the player's inventory (outside the magazine).</summary>
    public int AmmoInBag;

    /// <summary>Item description shown in the bottom info block.</summary>
    public string Description = string.Empty;

    /// <summary>RSI path for the preview icon (empty = use client default).</summary>
    public string IconPath = string.Empty;

    /// <summary>Optional RSI state for the preview icon (empty = "icon" or the first state).</summary>
    public string IconState = string.Empty;
}

/// <summary>
///     Игрок подтвердил выбор. <see cref="BuyIndices"/> — индексы в <c>Stock</c>,
///     <see cref="SellProtos"/> — прототипы вещей из инвентаря игрока, которые он продаёт.
/// </summary>
[Serializable, NetSerializable]
public sealed class N14MerchantCommitMessage : BoundUserInterfaceMessage
{
    public List<int> BuyIndices = new();
    public List<string> SellProtos = new();
}
