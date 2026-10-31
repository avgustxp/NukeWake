using System.Diagnostics.CodeAnalysis;
using Content.Shared._Forge.Merchant;
using Content.Shared._Forge.Weapons.Ranged.Components;
using Content.Shared.Clothing.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.Weapons.Ranged.Components;
using Content.Server.Cargo.Components;
using Content.Server.Power.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._Forge.Merchant;

/// <summary>
///     Серверная логика торговца: отправка состояния, покупка за капсы, продажа за капсы.
/// </summary>
public sealed partial class N14MerchantSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private const string CapsStackId = "Caps";

    /// <summary>Тег внутреннего магазина/спидлоадера: считается частью оружия, не отдельной вещью.</summary>
    private static readonly ProtoId<TagPrototype> SpeedLoaderInternalTag = "SpeedLoaderInternal";

    /// <summary>Имя слота патронника оружия (см. SharedGunSystem.ChamberMagazine).</summary>
    private const string ChamberSlotId = "gun_chamber";

    /// <summary>Имя слота сменного магазина оружия (см. SharedGunSystem.Magazine).</summary>
    private const string MagazineSlotId = "gun_magazine";

    /// <summary>Наценка торговца на купленные у игрока вещи (25%).</summary>
    private const double MerchantMarkup = 1.25;

    /// <summary>Прототипы-списки цен и иконок (задаются в YAML).</summary>
    private static readonly ProtoId<N14MerchantPricePrototype> PriceListId = "N14MerchantPrices";
    private static readonly ProtoId<N14MerchantIconPrototype> IconListId = "N14MerchantIcons";

    /// <summary>
    ///     Детали силовой брони (перчатки, ботинки, блок питания), которые надеваются
    ///     только вместе с костюмом. В торговле они не участвуют.
    /// </summary>
    private HashSet<string>? _powerArmorParts;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<N14MerchantComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<N14MerchantComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<N14MerchantComponent, N14MerchantCommitMessage>(OnCommit);
        SubscribeLocalEvent<N14MerchantComponent, MapInitEvent>(OnMapInit);

        // При горячей перезагрузке прототипов пересобираем список деталей силовой брони.
        _prototype.PrototypesReloaded += _ => _powerArmorParts = null;

        // Диагностика: если YAML-прототип списка не загрузился (например, из-за дубля id),
        // торговец молча покажет дефолтную иконку — предупреждаем в лог.
        if (!_prototype.HasIndex(IconListId))
            Log.Warning($"Прототип списка иконок торговца '{IconListId}' не найден — используется дефолтная иконка.");

        if (!_prototype.HasIndex(PriceListId))
            Log.Warning($"Прототип списка цен торговца '{PriceListId}' не найден — цены берутся из StaticPrice.");
    }

    private void OnMapInit(EntityUid uid, N14MerchantComponent comp, MapInitEvent args)
    {
        // Roll the starting caps once per spawned merchant.
        if (comp.CapsMin >= 0 && comp.CapsMax >= comp.CapsMin)
        {
            comp.Caps = _random.Next(comp.CapsMin, comp.CapsMax + 1);

            // Remember that the roll already happened so saving/loading the map
            // does not reroll the merchant's purse.
            comp.CapsMin = -1;
            comp.CapsMax = -1;
        }

        // Собираем стартовый ассортимент из пула наборов. При загрузке карты
        // сгенерированные позиции пересоздаются, а выкупленные у игрока — остаются.
        // Если пула нет, торговец торгует фиксированным списком stock из YAML.
        if (comp.StockSets.Count > 0)
            FillAssortment(comp);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<N14MerchantComponent>();

        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.RestockSeconds <= 0 || comp.StockSets.Count == 0)
                continue;

            comp.RestockTimer += frameTime;
            if (comp.RestockTimer < comp.RestockSeconds)
                continue;

            comp.RestockTimer -= comp.RestockSeconds;
            FillAssortment(comp);
            RefreshOpenUis(uid, comp);
        }
    }

    /// <summary>
    ///     Формирует ассортимент торговца из пула наборов.
    ///     Сгенерированные позиции заменяются, выкупленные у игрока
    ///     (<see cref="N14MerchantListing.PlayerSold"/>) остаются в продаже.
    /// </summary>
    private void FillAssortment(N14MerchantComponent comp)
    {
        // Сгенерированные позиции заменяются; выкупленные у игрока остаются,
        // а полностью распроданные (Remaining == 0) больше не нужны.
        comp.Stock.RemoveAll(listing => !listing.PlayerSold || listing.Remaining == 0);

        if (comp.StockSize <= 0)
            return;

        var pool = CollectStockPool(comp);
        if (pool.Count == 0)
            return;

        // Не дублируем в ассортименте то, что игрок уже продал торговцу.
        var persistent = new HashSet<string>();
        foreach (var listing in comp.Stock)
        {
            if (listing.PlayerSold)
                persistent.Add(listing.Product.Id);
        }

        var candidates = new List<N14MerchantListing>(pool.Count);
        foreach (var listing in pool)
        {
            if (!persistent.Contains(listing.Product.Id))
                candidates.Add(listing);
        }

        _random.Shuffle(candidates);

        var count = Math.Min(comp.StockSize, candidates.Count);
        for (var i = 0; i < count; i++)
        {
            var listing = candidates[i];
            listing.PlayerSold = false;
            comp.Stock.Add(listing);
        }
    }

    /// <summary>Объединённый пул позиций из всех наборов торговца (без дублей).</summary>
    private List<N14MerchantListing> CollectStockPool(N14MerchantComponent comp)
    {
        var pool = new List<N14MerchantListing>();
        var seen = new HashSet<string>();

        foreach (var setId in comp.StockSets)
        {
            if (!_prototype.TryIndex(setId, out var set))
            {
                Log.Warning($"Merchant stock set '{setId}' was not found; skipping it.");
                continue;
            }

            foreach (var entry in set.Entries)
            {
                if (IsPowerArmorPart(entry.Product.Id))
                    continue;

                if (seen.Add(entry.Product.Id))
                    pool.Add(entry);
            }
        }

        return pool;
    }

    /// <summary>
    ///     Является ли предмет съёмной деталью силовой брони (перчатки, ботинки, блок питания).
    ///     Такие детали не должны появляться в окне торговли.
    /// </summary>
    private bool IsPowerArmorPart(string protoId)
    {
        EnsurePowerArmorParts();
        return _powerArmorParts!.Contains(protoId);
    }

    private void EnsurePowerArmorParts()
    {
        if (_powerArmorParts != null)
            return;

        _powerArmorParts = new HashSet<string>();

        // Детали силовой брони — это то, что костюм надевает через ToggleableClothing.
        // Костюм узнаём по сочетанию ToggleableClothing + MultiHandedItem.
        foreach (var proto in _prototype.EnumeratePrototypes<EntityPrototype>())
        {
            if (!proto.TryGetComponent<ToggleableClothingComponent>(out var toggle, _componentFactory) ||
                !proto.TryGetComponent<MultiHandedItemComponent>(out _, _componentFactory))
            {
                continue;
            }

            foreach (var (slot, part) in toggle.ClothingPrototypes)
            {
                // Шлем силовой брони можно носить отдельно, поэтому его не скрываем.
                if (slot != "head")
                    _powerArmorParts.Add(part.Id);
            }

            if (toggle.ClothingPrototype is { } legacy && toggle.Slot != "head")
                _powerArmorParts.Add(legacy.Id);
        }

        // Варианты деталей (например, блок питания рейдерской брони) наследуются
        // от базовой детали и тоже надеваются только вместе с костюмом.
        var variants = new List<string>();
        foreach (var proto in _prototype.EnumeratePrototypes<EntityPrototype>())
        {
            if (!_powerArmorParts.Contains(proto.ID) && InheritsFromPowerArmorPart(proto, new HashSet<string>()))
                variants.Add(proto.ID);
        }

        foreach (var id in variants)
            _powerArmorParts.Add(id);
    }

    private bool InheritsFromPowerArmorPart(EntityPrototype proto, HashSet<string> visited)
    {
        if (proto.Parents == null)
            return false;

        foreach (var parentId in proto.Parents)
        {
            if (_powerArmorParts!.Contains(parentId))
                return true;

            if (visited.Add(parentId) &&
                _prototype.TryIndex<EntityPrototype>(parentId, out var parent, false) &&
                InheritsFromPowerArmorPart(parent, visited))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Обновляет открытое окно торговца после смены ассортимента.</summary>
    private void RefreshOpenUis(EntityUid uid, N14MerchantComponent comp)
    {
        if (!_ui.IsUiOpen(uid, N14MerchantUiKey.Key))
            return;

        // Состояние UI одно на сущность, поэтому отправляем его от имени первого
        // открывшего окно игрока (капсы/патроны в остальных окнах не различаются).
        foreach (var actor in _ui.GetActors(uid, N14MerchantUiKey.Key))
        {
            _ui.SetUiState(uid, N14MerchantUiKey.Key, BuildState(uid, comp, actor));
            break;
        }
    }

    private void OnUiOpened(EntityUid uid, N14MerchantComponent comp, BoundUIOpenedEvent args)
    {
        // Пока окно открыто — игрок не может ходить.
        EnsureComp<N14MerchantTradeUserComponent>(args.Actor);
        _ui.SetUiState(uid, N14MerchantUiKey.Key, BuildState(uid, comp, args.Actor));
    }

    private void OnUiClosed(EntityUid uid, N14MerchantComponent comp, BoundUIClosedEvent args)
    {
        RemComp<N14MerchantTradeUserComponent>(args.Actor);
    }

    private void OnCommit(EntityUid uid, N14MerchantComponent comp, N14MerchantCommitMessage args)
    {
        var buyer = args.Actor;

        foreach (var index in args.BuyIndices)
        {
            if (index < 0 || index >= comp.Stock.Count)
                continue;

            var listing = comp.Stock[index];
            if (listing.Remaining == 0)
                continue;

            // Детали силовой брони не продаются даже по подделанному запросу.
            if (IsPowerArmorPart(listing.Product.Id))
                continue;

            var price = GetPrice(listing.Product.Id, listing.Price);
            if (price > 0 && !TryTakeCaps(buyer, price))
                continue;

            comp.Caps += price;
            listing.Remaining = listing.Remaining < 0 ? -1 : listing.Remaining - 1;
            comp.Stock[index] = listing;

            var product = Spawn(listing.Product, Transform(buyer).Coordinates);
            _hands.PickupOrDrop(buyer, product);
        }

        foreach (var proto in args.SellProtos)
        {
            // Детали силовой брони не принимаем даже по подделанному запросу.
            if (IsPowerArmorPart(proto))
                continue;

            if (!TryGetSellPrice(comp, proto, out var price))
                continue;

            if (price > comp.Caps)
                continue;

            if (!TryConsumeItem(buyer, proto))
                continue;

            comp.Caps -= price;
            GiveCaps(buyer, price);

            // Купленная вещь уходит в ассортимент торговца с наценкой.
            AddToStock(comp, proto, price);
        }

        // Звуки сделки проигрывает клиент (N14MerchantSounds) — серверный kaching убран,
        // чтобы не накладывался на новые UI-звуки.

        if (_ui.IsUiOpen(uid, N14MerchantUiKey.Key))
            _ui.SetUiState(uid, N14MerchantUiKey.Key, BuildState(uid, comp, buyer));
    }

    private bool TryFindWant(N14MerchantComponent comp, string proto, out N14MerchantListing want)
    {
        foreach (var listing in comp.Wants)
        {
            if (listing.Product.Id == proto)
            {
                want = listing;
                return true;
            }
        }

        want = default;
        return false;
    }

    private N14MerchantUiState BuildState(EntityUid uid, N14MerchantComponent comp, EntityUid actor)
    {
        var state = new N14MerchantUiState
        {
            MerchantName = Identity.Name(uid, EntityManager),
            MerchantCaps = comp.Caps,
            PlayerCaps = CountCaps(actor),
        };

        for (var i = 0; i < comp.Stock.Count; i++)
        {
            var listing = comp.Stock[i];

            if (listing.Remaining == 0)
                continue;

            if (!_prototype.TryIndex<EntityPrototype>(listing.Product, out var proto))
                continue;

            // Детали силовой брони не показываем в торговле.
            if (IsPowerArmorPart(listing.Product.Id))
                continue;

            var item = new N14MerchantItemState
            {
                Id = "o" + i,
                Index = i,
                Proto = listing.Product.Id,
                Name = proto.Name,
                Description = proto.Description,
                Price = GetPrice(listing.Product.Id, listing.Price),
                Count = listing.Remaining,
            };

            // Товар торговца не заспавнен — читаем данные из прототипа (у нового товара прочность полная).
            FillWeaponInfo(item, proto, null, actor);
            state.Offers.Add(item);
        }

        BuildRequests(comp, actor, state);
        return state;
    }

    /// <summary>
    ///     Список вещей в руках и экипировке игрока (с заходом в контейнеры вроде рюкзака).
    /// </summary>
    private void BuildRequests(N14MerchantComponent comp, EntityUid actor, N14MerchantUiState state)
    {
        var prices = new Dictionary<string, int>();
        foreach (var want in comp.Wants)
        {
            if (!prices.ContainsKey(want.Product.Id))
                prices[want.Product.Id] = want.Price;
        }

        var owned = new Dictionary<string, int>();
        var order = new List<string>();
        var samples = new Dictionary<string, EntityUid>();

        // Сущности, вставленные в магазин оружия (спидлоадеры/магазины) — часть оружия, не отдельные вещи.
        var insertedMags = CollectInsertedMagazines(actor);

        foreach (var ent in EnumerateInventory(actor))
        {
            if (!TryGetProtoId(ent, out var id))
                continue;

            // Детали силовой брони не показываем в списке продажи.
            if (IsPowerArmorPart(id))
                continue;

            // Виртуальные предметы рук (перетаскивание/экипировка) — не настоящие вещи.
            if (HasComp<VirtualItemComponent>(ent))
                continue;

            // Капсы — это валюта, в списке предметов их не показываем.
            if (TryComp<StackComponent>(ent, out var stack) && stack.StackTypeId == CapsStackId)
                continue;

            // Патроны и магазины в продаже не показываем (id содержит Cartridge/Magazine).
            if (IsAmmoOrMagazine(id))
                continue;

            // Внутренний магазин/спидлоадер считается частью оружия, а не отдельной вещью.
            if (_tag.HasTag(ent, SpeedLoaderInternalTag) || insertedMags.Contains(ent))
                continue;

            if (owned.TryAdd(id, 1))
            {
                order.Add(id);
                samples[id] = ent;
            }
            else
            {
                owned[id]++;
            }
        }

        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];

            if (!_prototype.TryIndex<EntityPrototype>(id, out var proto))
                continue;

            var item = new N14MerchantItemState
            {
                Id = "r" + i,
                Proto = id,
                Name = proto.Name,
                Description = proto.Description,
                Price = GetPrice(id, prices.GetValueOrDefault(id)),
                Count = owned[id],
            };

            // Вещи игрока заспавнены — читаем реальную прочность и патроны.
            samples.TryGetValue(id, out var sample);
            FillWeaponInfo(item, proto, sample, actor);
            state.Requests.Add(item);
        }
    }

    private bool TryGetProtoId(EntityUid uid, [NotNullWhen(true)] out string? protoId)
    {
        protoId = MetaData(uid).EntityPrototype?.ID;
        return protoId != null;
    }

    /// <summary>
    ///     Цена вещи: цена из YAML-списка цен -> явная цена из конфига -> StaticPrice у прототипа.
    /// </summary>
    private int GetPrice(string protoId, int configured)
    {
        // 1. Список цен из YAML (наивысший приоритет).
        if (_prototype.TryIndex(PriceListId, out var priceList) &&
            priceList.Prices.TryGetValue(protoId, out var listed))
        {
            return listed;
        }

        // 2. Явная цена из конфига торговца.
        if (configured > 0)
            return configured;

        // 3. StaticPrice у прототипа.
        if (_prototype.TryIndex<EntityPrototype>(protoId, out var proto) &&
            proto.TryGetComponent<StaticPriceComponent>(out var staticPrice))
        {
            return (int)Math.Round(staticPrice.Price);
        }

        return 0;
    }

    /// <summary>Путь к RSI-иконке предпросмотра для прототипа (или дефолт).</summary>
    private ResPath GetIconPath(string protoId)
    {
        if (_prototype.TryIndex(IconListId, out var iconList))
            return iconList.GetIcon(protoId);

        return N14MerchantIconPrototype.DefaultIcon;
    }

    /// <summary>Состояние RSI-иконки предпросмотра (или null, если подходит "icon"/первое).</summary>
    private string? GetIconState(string protoId)
    {
        if (_prototype.TryIndex(IconListId, out var iconList))
            return iconList.GetState(protoId);

        return null;
    }

    /// <summary>
    ///     Заполняет данные об оружии для строки UI: прочность, калибр, патроны в магазине и у игрока.
    /// </summary>
    /// <param name="entity">Заспавненная сущность (для вещей игрока) или null (товар торговца из прототипа).</param>
    private void FillWeaponInfo(N14MerchantItemState item, EntityPrototype proto, EntityUid? entity, EntityUid actor)
    {
        // Иконка предпросмотра из YAML-списка (или дефолт).
        item.IconPath = GetIconPath(proto.ID).ToString();
        item.IconState = GetIconState(proto.ID) ?? string.Empty;

        // Прочность.
        if (entity != null && TryComp<GunConditionComponent>(entity, out var cond) && cond.MaxCondition > 0)
        {
            item.Condition = Math.Clamp(cond.Condition / cond.MaxCondition, 0f, 1f);
        }
        else if (proto.TryGetComponent<GunConditionComponent>(out var protoCond) && protoCond.MaxCondition > 0)
        {
            item.Condition = Math.Clamp(protoCond.Condition / protoCond.MaxCondition, 0f, 1f);
        }

        // Калибр и магазин.
        BallisticAmmoProviderComponent? ballistic = null;
        RevolverAmmoProviderComponent? revolver = null;

        if (entity != null)
        {
            TryComp(entity, out ballistic);
            TryComp(entity, out revolver);
        }
        else
        {
            proto.TryGetComponent(out ballistic);
            proto.TryGetComponent(out revolver);
        }

        if (ballistic != null)
        {
            item.MagCapacity = ballistic.Capacity;
            item.AmmoInMag = entity != null
                ? Math.Clamp(ballistic.Count, 0, ballistic.Capacity)
                : ballistic.Capacity; // новый товар при спавне полон
        }
        else if (revolver != null)
        {
            // Револьвер: патроны лежат в барабане (Chambers/AmmoSlots), а не в боепитателе.
            item.MagCapacity = revolver.Capacity;
            item.AmmoInMag = entity != null
                ? CountRevolverLoaded(revolver)
                : CountRevolverProtoLoaded(revolver);
        }
        else if (TryGetMagazineInfo(proto, entity, out var magCapacity, out var magCount))
        {
            // Оружие со сменным магазином: ёмкость/заряды берём из вставленного магазина.
            item.MagCapacity = magCapacity;
            item.AmmoInMag = magCount;
        }
        else if (TryGetBatteryAmmoInfo(proto, entity, out var charge, out var maxCharge))
        {
            // Лазерное/плазменное оружие: показываем заряды батареи (оружия или энергоячейки).
            item.MagCapacity = maxCharge;
            item.AmmoInMag = charge;
            item.IsEnergyWeapon = true;
        }

        var tags = ballistic != null ? GetCaliberTags(ballistic) : null;

        // У револьвера нет BallisticAmmoProvider — калибр берём из барабана.
        if ((tags == null || !HasCaliberTag(tags)) && revolver != null)
            tags = GetRevolverCaliberTags(revolver) ?? tags;

        // Оружие без собственного калибра (например, со сменным магазином) — читаем калибр
        // из патронника (слот gun_chamber): вставленный патрон или whitelist слота.
        if (tags == null || !HasCaliberTag(tags))
            tags = GetChamberCaliberTags(proto, entity) ?? tags;

        // Если у самого оружия калиберных тегов нет (оно работает только через вставленный
        // магазин), берём калибр из вставленного внутреннего магазина/спидлоадера.
        if ((tags == null || !HasCaliberTag(tags)) && ballistic != null && entity != null)
            tags = GetInsertedMagazineCaliberTags(entity.Value) ?? tags;

        if (tags == null || tags.Count == 0)
            return;

        item.Caliber = FormatCaliber(tags[0].Id);
        item.AmmoInBag = CountAmmoInInventory(actor, tags, entity);
    }

    /// <summary>
    ///     Калиберные теги револьвера: из whitelist барабана, иначе — из прототипа патрона,
    ///     которым он снаряжается (<c>proto</c>).
    /// </summary>
    private List<ProtoId<TagPrototype>>? GetRevolverCaliberTags(RevolverAmmoProviderComponent revolver)
    {
        if (revolver.Whitelist?.Tags is { } whitelist)
        {
            var fromWhitelist = FilterCaliberTags(whitelist);
            if (fromWhitelist != null)
                return fromWhitelist;
        }

        if (revolver.FillPrototype is { } fill &&
            _prototype.TryIndex<EntityPrototype>(fill, out var cartridge) &&
            cartridge.TryGetComponent<TagComponent>(out var cartridgeTags))
        {
            return FilterCaliberTags(cartridgeTags.Tags);
        }

        return null;
    }

    /// <summary>
    ///     Сколько патронов заряжено в барабане револьвера. Правило как у
    ///     <c>SharedGunSystem.GetRevolverCount</c>: гнездо заряжено, если <c>Chambers[i]</c>
    ///     не null (патрон ещё не заспавнен) или в <c>AmmoSlots[i]</c> лежит сущность патрона.
    /// </summary>
    private static int CountRevolverLoaded(RevolverAmmoProviderComponent revolver)
    {
        var count = 0;

        for (var i = 0; i < revolver.Capacity; i++)
        {
            var chamber = i < revolver.Chambers.Length ? revolver.Chambers[i] : null;
            var ammo = i < revolver.AmmoSlots.Count ? revolver.AmmoSlots[i] : null;

            if (chamber != null || ammo != null)
                count++;
        }

        return count;
    }

    /// <summary>
    ///     Заряды барабана у НЕзаспавненного товара (прототипа): если <c>chambers</c> заданы
    ///     явно (например, у пустых вариантов) — считаем их, иначе при наличии <c>proto</c>
    ///     барабан считается полным (см. <c>SharedGunSystem.OnRevolverInit</c>).
    /// </summary>
    private static int CountRevolverProtoLoaded(RevolverAmmoProviderComponent revolver)
    {
        if (revolver.Capacity > 0 && revolver.Chambers.Length >= revolver.Capacity)
        {
            var explicitCount = 0;

            for (var i = 0; i < revolver.Capacity; i++)
            {
                if (revolver.Chambers[i] != null)
                    explicitCount++;
            }

            return explicitCount;
        }

        return revolver.FillPrototype != null ? revolver.Capacity : 0;
    }

    /// <summary>
    ///     Ёмкость и число зарядов сменного магазина оружия (слот <c>gun_magazine</c>).
    ///     Для заспавненной сущности — из вставленного магазина, для прототипа — из стартового.
    /// </summary>
    private bool TryGetMagazineInfo(EntityPrototype proto, EntityUid? entity, out int capacity, out int count)
    {
        capacity = 0;
        count = 0;

        ItemSlotsComponent? slots = null;

        if (entity != null)
            TryComp(entity, out slots);
        else
            proto.TryGetComponent(out slots);

        if (slots == null)
            return false;

        // Копируем ссылку в локальную переменную: к словарю слота нужен только Read-доступ.
        var slotDict = slots.Slots;
        if (!slotDict.TryGetValue(MagazineSlotId, out var slot))
            return false;

        // Заспавненное оружие — читаем реально вставленный магазин.
        if (entity != null && slot.Item is { } magazine)
        {
            if (!TryComp<BallisticAmmoProviderComponent>(magazine, out var magBallistic))
                return false;

            capacity = magBallistic.Capacity;
            count = Math.Clamp(magBallistic.Count, 0, magBallistic.Capacity);
            return capacity > 0;
        }

        // Товар торговца (прототип) — читаем стартовый магазин из прототипа.
        if (slot.StartingItem is { } startingItem &&
            _prototype.TryIndex<EntityPrototype>(startingItem, out var magProto) &&
            magProto.TryGetComponent<BallisticAmmoProviderComponent>(out var protoBallistic))
        {
            capacity = protoBallistic.Capacity;
            count = protoBallistic.Capacity; // новый товар при спавне полон
            return capacity > 0;
        }

        return false;
    }

    /// <summary>
    ///     Заряды батареи оружия (самозарядные лазеры) или его энергоячейки в слоте магазина.
    ///     <paramref name="count"/> — сколько выстрелов осталось, <paramref name="capacity"/> — ёмкость.
    /// </summary>
    private bool TryGetBatteryAmmoInfo(EntityPrototype proto, EntityUid? entity, out int count, out int capacity)
    {
        count = 0;
        capacity = 0;

        // Батарея самого оружия (оружие без сменной ячейки).
        if (TryGetBatteryProvider(proto, entity, out var provider) &&
            TryGetBattery(proto, entity, out var battery))
        {
            return TryGetBatteryAmmo(provider, battery, out count, out capacity);
        }

        // Энергоячейка в слоте магазина.
        ItemSlotsComponent? slots = null;

        if (entity != null)
            TryComp(entity, out slots);
        else
            proto.TryGetComponent(out slots);

        if (slots == null)
            return false;

        // Копируем ссылку в локальную переменную: к словарю слота нужен только Read-доступ.
        var slotDict = slots.Slots;
        if (!slotDict.TryGetValue(MagazineSlotId, out var slot))
            return false;

        if (entity != null)
        {
            // У заспавненного оружия ячейка могла быть вынута — тогда заряда нет.
            if (slot.Item is not { } magazine)
                return false;

            return TryGetBatteryProvider(null, magazine, out provider) &&
                   TryGetBattery(null, magazine, out battery) &&
                   TryGetBatteryAmmo(provider, battery, out count, out capacity);
        }

        if (slot.StartingItem is not { } startingItem ||
            !_prototype.TryIndex<EntityPrototype>(startingItem, out var magazineProto))
        {
            return false;
        }

        return TryGetBatteryProvider(magazineProto, null, out provider) &&
               TryGetBattery(magazineProto, null, out battery) &&
               TryGetBatteryAmmo(provider, battery, out count, out capacity);
    }

    /// <summary>Число выстрелов и ёмкость батареи (для прототипа — по <c>startingCharge</c>).</summary>
    private static bool TryGetBatteryAmmo(
        BatteryAmmoProviderComponent provider,
        BatteryComponent? battery,
        out int count,
        out int capacity)
    {
        count = 0;
        capacity = 0;

        if (provider.FireCost <= 0f)
            return false;

        if (battery is { MaxCharge: > 0f })
        {
            capacity = (int) (battery.MaxCharge / provider.FireCost);
            count = Math.Clamp((int) (battery.CurrentCharge / provider.FireCost), 0, capacity);
            return capacity > 0;
        }

        // Батареи нет (например, состояние ещё не пришло) — берём счётчик самого провайдера.
        capacity = provider.Capacity;
        count = Math.Clamp(provider.Shots, 0, capacity);
        return capacity > 0;
    }

    /// <summary>Провайдер боезапаса батареи: <c>Hitscan</c> для лазеров, <c>Projectile</c> для плазмы.</summary>
    private bool TryGetBatteryProvider(
        EntityPrototype? proto,
        EntityUid? entity,
        [NotNullWhen(true)] out BatteryAmmoProviderComponent? provider)
    {
        provider = null;

        if (entity != null)
        {
            if (TryComp<HitscanBatteryAmmoProviderComponent>(entity.Value, out var hitscan))
                provider = hitscan;
            else if (TryComp<ProjectileBatteryAmmoProviderComponent>(entity.Value, out var projectile))
                provider = projectile;
        }
        else if (proto != null)
        {
            if (proto.TryGetComponent<HitscanBatteryAmmoProviderComponent>(out var hitscan))
                provider = hitscan;
            else if (proto.TryGetComponent<ProjectileBatteryAmmoProviderComponent>(out var projectile))
                provider = projectile;
        }

        return provider != null;
    }

    private bool TryGetBattery(
        EntityPrototype? proto,
        EntityUid? entity,
        out BatteryComponent? battery)
    {
        battery = null;

        if (entity != null)
            TryComp(entity.Value, out battery);
        else if (proto != null)
            proto.TryGetComponent(out battery);

        return battery != null;
    }

    /// <summary>
    ///     Калибр из патронника оружия (слот <c>gun_chamber</c>): теги реально вставленного патрона,
    ///     иначе — калиберные теги из whitelist слота (например, <c>N14Cartridge308Rifle</c>).
    /// </summary>
    private List<ProtoId<TagPrototype>>? GetChamberCaliberTags(EntityPrototype proto, EntityUid? entity)
    {
        ItemSlotsComponent? slots = null;

        if (entity != null)
            TryComp(entity, out slots);
        else
            proto.TryGetComponent(out slots);

        if (slots == null)
            return null;

        // Копируем ссылку в локальную переменную: к словарю слота нужен только Read-доступ.
        var slotDict = slots.Slots;
        if (!slotDict.TryGetValue(ChamberSlotId, out var slot))
            return null;

        // Вставленный патрон точнее whitelist — патрон могли заменить.
        if (slot.Item is { } cartridge && TryComp<TagComponent>(cartridge, out var tagComp))
        {
            var fromItem = FilterCaliberTags(tagComp.Tags);
            if (fromItem != null)
                return fromItem;
        }

        return slot.Whitelist?.Tags is { } whitelistTags ? FilterCaliberTags(whitelistTags) : null;
    }

    /// <summary>Оставляет только калиберные теги (N14Cartridge*/N14Shell*).</summary>
    private static List<ProtoId<TagPrototype>>? FilterCaliberTags(IEnumerable<ProtoId<TagPrototype>> all)
    {
        List<ProtoId<TagPrototype>>? result = null;

        foreach (var tag in all)
        {
            if (!IsCaliberTag(tag.Id))
                continue;

            result ??= new List<ProtoId<TagPrototype>>();
            result.Add(tag);
        }

        return result;
    }

    /// <summary>Есть ли среди переданных тегов хотя бы один калиберный (N14Cartridge*/N14Shell*).</summary>
    private static bool HasCaliberTag(List<ProtoId<TagPrototype>> tags)
    {
        foreach (var tag in tags)
        {
            if (IsCaliberTag(tag.Id))
                return true;
        }

        return false;
    }

    private static bool IsCaliberTag(string id)
        => id.StartsWith("N14Cartridge", StringComparison.Ordinal) ||
           id.StartsWith("N14Shell", StringComparison.Ordinal);

    /// <summary>Патрон или магазин по id прототипа (содержит Cartridge/Magazine) — не продаётся.</summary>
    private static bool IsAmmoOrMagazine(string protoId)
        => protoId.Contains("Cartridge", StringComparison.OrdinalIgnoreCase) ||
           protoId.Contains("Magazine", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Калиберные теги вставленного в оружие внутреннего магазина/спидлоадера (если есть).
    /// </summary>
    private List<ProtoId<TagPrototype>>? GetInsertedMagazineCaliberTags(EntityUid weapon)
    {
        if (!TryComp<ContainerManagerComponent>(weapon, out var manager))
            return null;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var ent in container.ContainedEntities)
            {
                if (!TryComp<BallisticAmmoProviderComponent>(ent, out var mag))
                    continue;

                var tags = GetCaliberTags(mag);
                if (tags != null && HasCaliberTag(tags))
                    return tags;
            }
        }

        return null;
    }

    /// <summary>
    ///     Теги калибра из whitelist патронов оружия (теги вида N14Cartridge*/N14Shell*).
    /// </summary>
    private static List<ProtoId<TagPrototype>>? GetCaliberTags(BallisticAmmoProviderComponent ballistic)
    {
        var all = ballistic.Whitelist?.Tags;
        if (all == null || all.Count == 0)
            return null;

        var result = new List<ProtoId<TagPrototype>>();

        foreach (var tag in all)
        {
            if (IsCaliberTag(tag.Id))
                result.Add(tag);
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    ///     Человекочитаемые названия калибров (тег патрона -> подпись в info-боксе).
    ///     Позволяет не выводить служебные суффиксы тегов вроде <c>Rifle</c>/<c>Pistol</c>.
    /// </summary>
    private static readonly Dictionary<string, string> CaliberNames = new()
    {
        ["N14CartridgePistol22"] = ".22",
        ["N14CartridgePistol45"] = ".45",
        ["N14CartridgePistol5"] = "5mm",
        ["N14CartridgePistol9"] = "9mm",
        ["N14CartridgePistol10"] = "10mm",
        ["N14CartridgePistol127"] = "12.7mm",
        ["N14Cartridge44Magnum"] = ".44 Magnum",
        ["N14Cartridge45-70"] = ".45-70",
        ["N14Cartridge308Rifle"] = ".308",
        ["N14Cartridge50"] = ".50",
        ["N14Cartridge50HEIAP"] = ".50 HEIAP",
        ["N14Cartridge545Rifle"] = "5.45",
        ["N14Cartridge556Rifle"] = "5.56",
        ["N14Cartridge68Rifle"] = "6.8",
        ["N14Cartridge762Rifle"] = "7.62",
        ["N14Cartridge12-7x55mm"] = "12.7x55mm",
        ["N14CartridgeGauss"] = "2mm EC",
        ["N14CartridgeNail"] = "Nail",
        ["N14ShellShotgun12"] = "12ga",
        ["N14ShellShotgun20"] = "20ga",
    };

    /// <summary>
    ///     Человекочитаемый калибр: "N14Cartridge45-70" -> ".45-70", "N14ShellShotgun12" -> "12ga".
    /// </summary>
    private static string FormatCaliber(string tagId)
    {
        if (CaliberNames.TryGetValue(tagId, out var knownName))
            return knownName;

        // Неизвестный тег — прежняя эвристика (на случай новых калибров).
        if (tagId.StartsWith("N14Cartridge", StringComparison.Ordinal))
        {
            var rest = tagId["N14Cartridge".Length..];
            return rest.Length > 0 && char.IsDigit(rest[0]) ? "." + rest : rest;
        }

        if (tagId.StartsWith("N14ShellShotgun", StringComparison.Ordinal))
        {
            var rest = tagId["N14ShellShotgun".Length..];
            return rest + "ga";
        }

        if (tagId.StartsWith("N14Shell", StringComparison.Ordinal))
            return tagId["N14Shell".Length..];

        return tagId;
    }

    /// <summary>
    ///     Считает все подходящие патроны у игрока: свободные патроны в инвентаре и заряженные
    ///     (в том числе в <see cref="BallisticAmmoProviderComponent.UnspawnedCount"/>) в оружии,
    ///     магазинах, спидлоадерах и барабанах револьверов.
    /// </summary>
    private int CountAmmoInInventory(EntityUid actor, List<ProtoId<TagPrototype>> tags, EntityUid? excludeWeapon)
    {
        var inventory = EnumerateInventory(actor);

        // Патроны, лежащие внутри боепитателя/барабана, — это тоже сущности в контейнерах,
        // поэтому их считаем ТОЛЬКО через счётчик провайдера (иначе двойной учёт).
        var insideProvider = new HashSet<EntityUid>();

        var total = 0;

        foreach (var ent in inventory)
        {
            if (TryComp<BallisticAmmoProviderComponent>(ent, out var provider))
            {
                if (provider.Container != null)
                {
                    foreach (var ammo in provider.Container.ContainedEntities)
                        insideProvider.Add(ammo);
                }

                var providerTags = GetCaliberTags(provider);
                if (providerTags != null && HasCommonTag(providerTags, tags))
                    total += provider.Count;
            }

            if (TryComp<RevolverAmmoProviderComponent>(ent, out var revolver))
            {
                foreach (var ammo in revolver.AmmoSlots)
                {
                    if (ammo is { } cart)
                        insideProvider.Add(cart);
                }

                var revolverTags = GetRevolverCaliberTags(revolver);

                if (revolverTags != null && HasCommonTag(revolverTags, tags))
                    total += CountRevolverLoaded(revolver);
            }
        }

        // Свободные патроны (в том числе стопкой) — всё, что не лежит в боепитателе.
        foreach (var ent in inventory)
        {
            if (insideProvider.Contains(ent))
                continue;

            if (!_tag.HasAnyTag(ent, tags))
                continue;

            if (TryComp<StackComponent>(ent, out var stack))
                total += stack.Count;
            else
                total++;
        }

        return total;
    }

    /// <summary>Есть ли хотя бы один общий тег у двух списков.</summary>
    private static bool HasCommonTag(List<ProtoId<TagPrototype>> a, List<ProtoId<TagPrototype>> b)
    {
        foreach (var tag in a)
        {
            if (b.Contains(tag))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     По какой цене торговец берёт вещь у игрока: цена Wants, иначе StaticPrice.
    /// </summary>
    private bool TryGetSellPrice(N14MerchantComponent comp, string proto, out int price)
    {
        if (TryFindWant(comp, proto, out var want) && want.Price > 0)
        {
            price = want.Price;
            return true;
        }

        price = GetPrice(proto, 0);
        return price > 0;
    }

    /// <summary>
    ///     Купленная у игрока вещь попадает в ассортимент торговца с наценкой <see cref="MerchantMarkup"/>.
    ///     Позиция помечается <see cref="N14MerchantListing.PlayerSold"/> и переживает
    ///     обновление ассортимента, пока её не купят.
    /// </summary>
    private void AddToStock(N14MerchantComponent comp, string product, int boughtPrice)
    {
        var sellPrice = (int)Math.Round(boughtPrice * MerchantMarkup, MidpointRounding.AwayFromZero);

        for (var i = 0; i < comp.Stock.Count; i++)
        {
            var listing = comp.Stock[i];

            // Сгенерированные позиции не трогаем: у них своя цена и свой цикл обновления.
            if (!listing.PlayerSold || listing.Product.Id != product || listing.Remaining == 0)
                continue;

            // Докидываем штуку в уже существующую позицию, цену обновляем.
            listing.Remaining = listing.Remaining < 0 ? 1 : listing.Remaining + 1;
            listing.Price = sellPrice;
            comp.Stock[i] = listing;
            return;
        }

        comp.Stock.Add(new N14MerchantListing
        {
            Product = new EntProtoId(product),
            Price = sellPrice,
            Remaining = 1,
            PlayerSold = true,
        });
    }

    #region Капсы

    private int CountCaps(EntityUid uid)
    {
        var total = 0;

        foreach (var ent in EnumerateInventory(uid))
        {
            if (TryComp<StackComponent>(ent, out var stack) && stack.StackTypeId == CapsStackId)
                total += stack.Count;
        }

        return total;
    }

    private bool TryTakeCaps(EntityUid uid, int amount)
    {
        if (amount <= 0)
            return true;

        if (CountCaps(uid) < amount)
            return false;

        foreach (var ent in EnumerateInventory(uid))
        {
            if (amount <= 0)
                break;

            if (!TryComp<StackComponent>(ent, out var stack) || stack.StackTypeId != CapsStackId)
                continue;

            if (stack.Count <= 0)
                continue;

            var take = Math.Min(stack.Count, amount);
            _stack.SetCount(ent, stack.Count - take, stack);
            amount -= take;
        }

        return amount <= 0;
    }

    private void GiveCaps(EntityUid uid, int amount)
    {
        if (amount <= 0)
            return;

        foreach (var ent in EnumerateInventory(uid))
        {
            if (amount <= 0)
                break;

            if (!TryComp<StackComponent>(ent, out var stack) || stack.StackTypeId != CapsStackId)
                continue;

            var max = _prototype.TryIndex<StackPrototype>(CapsStackId, out var proto) ? proto.MaxCount ?? int.MaxValue : amount;
            var space = max - stack.Count;
            if (space <= 0)
                continue;

            var add = Math.Min(space, amount);
            _stack.SetCount(ent, stack.Count + add, stack);
            amount -= add;
        }

        if (amount <= 0)
            return;

        var perStack = _prototype.TryIndex<StackPrototype>(CapsStackId, out var sp) ? Math.Max(1, sp.MaxCount ?? amount) : amount;
        var coords = Transform(uid).Coordinates;

        while (amount > 0)
        {
            var give = Math.Min(perStack, amount);
            var cap = Spawn("N14CurrencyCap", coords);

            if (TryComp<StackComponent>(cap, out var newStack))
                _stack.SetCount(cap, give, newStack);

            _hands.PickupOrDrop(uid, cap);
            amount -= give;
        }
    }

    #endregion

    #region Инвентарь

    /// <summary>
    ///     Сущности, вставленные в контейнер боеприпасов любого предмета инвентаря игрока
    ///     (спидлоадеры/магазины). Считаются частью оружия и не показываются отдельными строками.
    /// </summary>
    private HashSet<EntityUid> CollectInsertedMagazines(EntityUid actor)
    {
        var result = new HashSet<EntityUid>();

        foreach (var ent in EnumerateInventory(actor))
        {
            if (!TryComp<BallisticAmmoProviderComponent>(ent, out var ballistic))
                continue;

            foreach (var contained in ballistic.Container.ContainedEntities)
            {
                // Только магазины/спидлоадеры (у них есть свой провайдер), патроны не трогаем.
                if (TryComp<BallisticAmmoProviderComponent>(contained, out _))
                    result.Add(contained);
            }
        }

        return result;
    }

    private List<EntityUid> EnumerateInventory(EntityUid uid)
    {
        var result = new List<EntityUid>();

        foreach (var held in _hands.EnumerateHeld(uid))
        {
            result.Add(held);
            Collect(held, result, 0);
        }

        if (TryComp<InventoryComponent>(uid, out var inventory))
        {
            foreach (var slot in inventory.Containers)
            {
                foreach (var ent in slot.ContainedEntities)
                {
                    result.Add(ent);
                    Collect(ent, result, 0);
                }
            }
        }

        return result;
    }

    private void Collect(EntityUid uid, List<EntityUid> list, int depth)
    {
        if (depth > 8)
            return;

        if (!TryComp<ContainerManagerComponent>(uid, out var manager))
            return;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var ent in container.ContainedEntities)
            {
                list.Add(ent);
                Collect(ent, list, depth + 1);
            }
        }
    }

    /// <summary>
    ///     Собирает все сущности внутри контейнеров <paramref name="uid"/> в множество.
    /// </summary>
    private void Collect(EntityUid uid, HashSet<EntityUid> set, int depth)
    {
        if (depth > 8)
            return;

        if (!TryComp<ContainerManagerComponent>(uid, out var manager))
            return;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var ent in container.ContainedEntities)
            {
                set.Add(ent);
                Collect(ent, set, depth + 1);
            }
        }
    }

    private bool TryConsumeItem(EntityUid user, string proto)
    {
        foreach (var ent in EnumerateInventory(user))
        {
            if (HasComp<VirtualItemComponent>(ent))
                continue;

            if (!TryGetProtoId(ent, out var id) || id != proto)
                continue;

            Del(ent);
            return true;
        }

        return false;
    }

    #endregion
}
