using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Forge.Merchant;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Utility;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Forge.Merchant;

/// <summary>
///     Окно торговца: две колонки (Вещи / NPC), выбор стрелкой с ценой, Accept / Exit.
/// </summary>
public sealed class N14MerchantWindow : FancyWindow
{
    public event Action? OnAccept;
    public event Action? OnExit;

    /// <summary>Дефолтная иконка предпросмотра для сущностей без явного пути в списке иконок.</summary>
    private const string DefaultPreviewRsiPath = "/Textures/_NukeWake/icons/iconsother/musor.rsi";

    /// <summary>
    ///     «Золотой стандарт» предпросмотра — иконка N14WeaponRifle556Service.
    ///     Задаёт пропорции рамки: все остальные иконки масштабируются внутрь неё.
    /// </summary>
    private const string StandardPreviewRsiPath = "/Textures/_NukeWake/icons/iconsguns/servicerifle.rsi";

    /// <summary>Высота рамки предпросмотра (px). Ширина рамки считается по пропорциям эталона.</summary>
    private const float PreviewBoxHeight = 112f;

    /// <summary>Пропорции эталона (141x161) — на случай, если ресурс не загрузился.</summary>
    private const float PreviewFallbackAspect = 141f / 161f;

    /// <summary>Отступ рамки предпросмотра от нижнего края левой колонки.</summary>
    private const float PreviewPanelTopGap = 18f;

    /// <summary>Кэш текстур предпросмотра по пути к RSI.</summary>
    private static readonly Dictionary<string, Texture?> PreviewCache = new();

    private readonly N14MerchantColumn _playerColumn;
    private readonly N14MerchantColumn _merchantColumn;
    private readonly BoxContainer _middle;
    private readonly TextureRect _preview;
    private readonly N14MerchantInfoBox _priceBox;
    private readonly N14MerchantInfoBox _ammoBox;
    private readonly Control _previewPanel;
    private readonly BoxContainer _infoStack;
    private readonly N14MerchantDescBox _descBox;
    private readonly RichTextLabel _descLabel;
    private readonly BoxContainer _itemData;
    private readonly BoxContainer _columns;

    /// <summary>Ширина одного info-бокса (цена / калибр).</summary>
    private const float InfoBoxWidth = 150f;

    /// <summary>Ширина блока описания = сумма ширин двух info-боксов.</summary>
    private const float InfoWidth = InfoBoxWidth * 2f;

    /// <summary>Отступ нижнего края блока описания от низа окна.</summary>
    private const float ItemDataBottomGap = 40f;

    /// <summary>Внутренний отступ блока описания (рамка).</summary>
    private const float DescPadding = 4f;

    /// <summary>Отступ текста описания от боковых сторон блока.</summary>
    private const float DescTextInset = 10f;

    /// <summary>Диапазон размера шрифта описания (авто-подгонка под высоту полосы).</summary>
    private const int DescFontMax = 14;
    private const int DescFontMin = 7;

    /// <summary>Теги, разрешённые в описании (нужен font для авто-подгонки размера).</summary>
    private static readonly Type[] DescTags =
    {
        typeof(FontTag),
        typeof(ColorTag),
        typeof(BoldTag),
        typeof(ItalicTag),
    };

    /// <summary>Текущий текст описания (в markup-escape виде) для повторной укладки.</summary>
    private string _descText = string.Empty;

    /// <summary>Состояния предметов по прототипу — для отображения info-боксов при наведении.</summary>
    private readonly Dictionary<string, N14MerchantItemState> _itemsByProto = new();

    private readonly HashSet<int> _selectedOffers = new();
    private readonly Dictionary<int, string> _selectedOfferProtos = new();
    private readonly HashSet<string> _selectedRequests = new();

    private readonly Font _font = N14MerchantTheme.GetFont(14);
    private readonly Font _infoFont = N14MerchantTheme.GetFont(12);

    public N14MerchantWindow()
    {
        // Звуки кликов загружаются заранее, чтобы первый клик не ждал файла.
        N14MerchantSounds.Preload();

        Resizable = false;
        SetSize = new Vector2(820, 620);
        Title = Loc.GetString("n14-merchant-window-title");

        _playerColumn = new N14MerchantColumn(Loc.GetString("n14-merchant-column-items"));
        _merchantColumn = new N14MerchantColumn(Loc.GetString("n14-merchant-column-npc"));

        // Заголовки стоят на уровне верхней линии, но Items чуть левее, NPC чуть правее.
        _playerColumn.SetHeaderOffset(-18f);
        _merchantColumn.SetHeaderOffset(18f);

        _middle = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            MinWidth = 118,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            Margin = new Thickness(4, 0),
        };

        var columns = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            // Слегка тянем блок колонок вверх.
            Margin = new Thickness(0, -32, 0, 0),
        };
        // Левую колонку тянем влево, правую — вправо.
        _playerColumn.Margin = new Thickness(-20, 0, 0, 0);
        _merchantColumn.Margin = new Thickness(0, 0, -20, 0);
        columns.AddChild(_playerColumn);
        columns.AddChild(_middle);
        columns.AddChild(_merchantColumn);
        _columns = columns;
        // Позиция/размер колонок меняются при пересборке строк — пересчитываем укладку описания.
        _columns.OnResized += LayoutItemData;

        // Рамка предпросмотра задаётся эталонной иконкой (N14WeaponRifle556Service):
        // высота фиксирована, ширина — по её пропорциям.
        var previewBox = GetPreviewBox();

        // Спрайт-предпросмотр: висит под нижним краем левой колонки, по центру её ширины.
        _preview = new TextureRect
        {
            Texture = LoadPreviewTexture(null),
            Modulate = N14MerchantTheme.Accent,
            MinSize = previewBox,
            MaxSize = previewBox,
            // Каждая иконка вписывается в рамку эталона целиком: ширина, высота и общий размер
            // меняются под её пропорции, но сама картинка не искажается.
            Stretch = TextureRect.StretchMode.KeepAspectCentered,
            MouseFilter = Control.MouseFilterMode.Ignore,
            VerticalAlignment = VAlignment.Center,
        };

        // Info-боксы (аналог BM_ItemData в F:NV): цена и калибр (патроны в магазине / у игрока).
        _priceBox = new N14MerchantInfoBox(string.Empty, _infoFont) { MinWidth = InfoBoxWidth };
        _ammoBox = new N14MerchantInfoBox(string.Empty, _infoFont) { MinWidth = InfoBoxWidth };

        _infoStack = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            MouseFilter = Control.MouseFilterMode.Ignore,
        };
        _infoStack.AddChild(_priceBox);
        _infoStack.AddChild(_ammoBox);

        // Большой блок описания предмета снизу окна: ширина = сумма ширин info-боксов.
        _descLabel = new RichTextLabel
        {
            HorizontalAlignment = HAlignment.Left,
            VerticalAlignment = VAlignment.Top,
            // Отступ текста от левой рамки (чтобы не был в притык).
            Margin = new Thickness(DescTextInset, 2, DescTextInset, 2),
        };

        _descBox = new N14MerchantDescBox
        {
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.Transparent,
            },
            Margin = new Thickness(4),
            MinWidth = InfoWidth,
            MaxWidth = InfoWidth,
        };
        _descBox.AddChild(_descLabel);

        // Панель предпросмотра = только иконка; висит под нижним краем левой колонки, по её центру.
        var previewPanel = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            Visible = false,
            MouseFilter = Control.MouseFilterMode.Ignore,
        };
        previewPanel.AddChild(_preview);

        // Анкер к нижнему центру колонки: рамка иконки отцентрирована по её ширине.
        LayoutContainer.SetAnchorLeft(previewPanel, 0.5f);
        LayoutContainer.SetAnchorRight(previewPanel, 0.5f);
        LayoutContainer.SetAnchorTop(previewPanel, 1f);
        LayoutContainer.SetAnchorBottom(previewPanel, 1f);
        LayoutContainer.SetMarginLeft(previewPanel, -previewBox.X / 2f);
        LayoutContainer.SetMarginRight(previewPanel, previewBox.X / 2f);
        LayoutContainer.SetMarginTop(previewPanel, PreviewPanelTopGap);
        LayoutContainer.SetMarginBottom(previewPanel, PreviewPanelTopGap + PreviewBoxHeight);

        _previewPanel = previewPanel;
        _playerColumn.AddChild(previewPanel);

        var accept = new Button
        {
            Text = Loc.GetString("n14-merchant-accept"),
            MinSize = new Vector2(130, 34),
            HorizontalAlignment = HAlignment.Right,
        };
        accept.Label.FontOverride = _font;
        accept.Label.FontColorOverride = N14MerchantTheme.Accent;
        ApplyButtonStyle(accept);
        accept.OnPressed += _ =>
        {
            // Accept — подтверждение сделки; звуки покупки/продажи — по выбранным позициям.
            N14MerchantSounds.PlayAccept();

            if (_selectedOffers.Count > 0)
                N14MerchantSounds.PlayPurchase();

            if (_selectedRequests.Count > 0)
                N14MerchantSounds.PlaySale();

            OnAccept?.Invoke();
        };

        var exit = new Button
        {
            Text = Loc.GetString("n14-merchant-exit"),
            MinSize = new Vector2(130, 34),
            HorizontalAlignment = HAlignment.Right,
        };
        exit.Label.FontOverride = _font;
        exit.Label.FontColorOverride = N14MerchantTheme.Accent;
        ApplyButtonStyle(exit);
        exit.OnPressed += _ =>
        {
            N14MerchantSounds.PlayExit();
            OnExit?.Invoke();
            Close();
        };

        var buttons = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalAlignment = HAlignment.Right,
            Margin = new Thickness(0, 0, 8, 4),
        };
        buttons.AddChild(accept);
        buttons.AddChild(exit);

        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            Margin = new Thickness(16, 14, 16, 34),
        };

        var topSpacer = new Control { MinHeight = 8, VerticalExpand = true };
        var bottomSpacer = new Control { MinHeight = 8, VerticalExpand = true };

        root.AddChild(topSpacer);
        root.AddChild(columns);
        root.AddChild(bottomSpacer);
        root.AddChild(buttons);

        var backdrop = new N14MerchantBackdrop
        {
            // Нижняя линия фона выравнивается по краям колонок.
            LineTarget = columns,
        };

        var layout = new LayoutContainer();
        layout.AddChild(backdrop);
        layout.AddChild(root);

        // Низ окна: info-боксы (цена/калибр) — всегда на фиксированной высоте от низа,
        // описание — над ними и никогда не поднимается выше нижней линии колонок.
        var itemData = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Bottom,
            Visible = false,
            MouseFilter = Control.MouseFilterMode.Ignore,
        };
        _itemData = itemData;

        itemData.AddChild(_descBox);
        itemData.AddChild(_infoStack);

        layout.AddChild(itemData);
        LayoutContainer.SetAnchorLeft(itemData, 0.5f);
        LayoutContainer.SetAnchorRight(itemData, 0.5f);
        LayoutContainer.SetAnchorTop(itemData, 1f);
        LayoutContainer.SetAnchorBottom(itemData, 1f);
        LayoutContainer.SetMarginLeft(itemData, -InfoWidth / 2f);
        LayoutContainer.SetMarginRight(itemData, InfoWidth / 2f);
        LayoutContainer.SetMarginTop(itemData, -2000f);
        LayoutContainer.SetMarginBottom(itemData, -ItemDataBottomGap);

        LayoutContainer.SetAnchorPreset(backdrop, LayoutContainer.LayoutPreset.Wide);
        LayoutContainer.SetAnchorPreset(root, LayoutContainer.LayoutPreset.Wide);

        XamlChildren.Add(layout);

        StripWindowChrome(this);
    }

    /// <summary>
    ///     Окно жёстко закреплено по центру экрана — перетаскивание отключено.
    /// </summary>
    protected override DragMode GetDragModeFor(Vector2 relativeMousePos) => DragMode.None;

    /// <summary>
    ///     Держим блок описания в границах доступной полосы: info-боксы всегда на фиксированной
    ///     высоте от низа, а описание над ними не поднимается выше нижней линии колонок.
    /// </summary>
    protected override void Resized()
    {
        base.Resized();
        LayoutItemData();
    }

    /// <summary>
    ///     Подгоняет размер шрифта описания так, чтобы весь текст помещался в полосу
    ///     между нижней линией колонок и info-боксами, не сдвигая последние.
    /// </summary>
    private void LayoutItemData()
    {
        if (_itemData == null || _descLabel == null)
            return;

        // Нижний край полосы описания = верх info-боксов; верх полосы = низ колонок.
        var itemDataBottom = GlobalPosition.Y + Size.Y - ItemDataBottomGap;
        var descriptionBottom = itemDataBottom - N14MerchantInfoBox.RowHeight;
        var columnsBottom = _columns.GlobalPosition.Y + _columns.Size.Y;

        // Доступная высота под описание (может быть отрицательной — тогда текст не влезает вовсе).
        var available = MathF.Max(0f, descriptionBottom - columnsBottom);

        FitDescriptionFont(available);
    }

    /// <summary>
    ///     Подбирает максимальный размер шрифта (от <see cref="DescFontMax"/> к <see cref="DescFontMin"/>),
    ///     при котором описание умещается по высоте в <paramref name="available"/> пикселей.
    /// </summary>
    private void FitDescriptionFont(float available)
    {
        if (string.IsNullOrEmpty(_descText))
        {
            _descBox.Visible = false;
            return;
        }

        _descBox.Visible = true;

        // Внутренняя ширина блока (минус рамка/паддинг и отступы текста).
        var innerWidth = InfoWidth - DescPadding * 2f - DescTextInset * 2f;

        for (var size = DescFontMax; size >= DescFontMin; size--)
        {
            _descLabel.SetMessage(DescMessage(size), DescTags, N14MerchantTheme.Accent);
            _descLabel.Measure(new Vector2(innerWidth, float.PositiveInfinity));

            if (_descLabel.DesiredSize.Y <= available)
            {
                _descBox.MaxHeight = float.PositiveInfinity;
                return;
            }
        }

        // Даже минимальный размер не влез — оставляем минимальный и ограничиваем высоту блока.
        _descLabel.SetMessage(DescMessage(DescFontMin), DescTags, N14MerchantTheme.Accent);
        _descLabel.Measure(new Vector2(innerWidth, float.PositiveInfinity));
        _descBox.MaxHeight = available;
    }

    /// <summary>Описание, обёрнутое в тег размера шрифта (для авто-подгонки).</summary>
    private FormattedMessage DescMessage(int size)
    {
        return FormattedMessage.FromMarkupPermissive($"[font size={size}]{_descText}[/font]");
    }

    /// <summary>
    ///     Убираем стандартную шапку окна (заголовок, крестик), разделитель под ней
    ///     и серый фон FancyWindow.
    /// </summary>
    private static void StripWindowChrome(Control root)
    {
        foreach (var child in root.Children.ToArray())
        {
            if (child is PanelContainer panel)
            {
                if (panel.StyleClasses.Contains("LowDivider") ||
                    panel.StyleClasses.Contains("AngleRect"))
                {
                    panel.Visible = false;
                    continue;
                }

                if (panel.StyleClasses.Contains("WindowHeadingBackground"))
                {
                    if (panel.Parent is Control header)
                        header.Visible = false;
                    else
                        panel.Visible = false;

                    continue;
                }
            }

            StripWindowChrome(child);
        }
    }

    public void UpdateState(N14MerchantUiState state)
    {
        Title = state.MerchantName;
        _merchantColumn.SetHeaderText(state.MerchantName);
        _playerColumn.Caps.Text = Loc.GetString("n14-merchant-caps", ("caps", state.PlayerCaps));
        _merchantColumn.Caps.Text = Loc.GetString("n14-merchant-caps", ("caps", state.MerchantCaps));

        // Пересобираем строки, сохраняя выбор по прототипу/индексу.
        _previewPanel.Visible = false;
        _itemData.Visible = false;
        _descText = string.Empty;
        _descBox.MaxHeight = float.PositiveInfinity;
        _itemsByProto.Clear();
        foreach (var item in state.Requests)
            _itemsByProto.TryAdd(item.Proto, item);
        foreach (var item in state.Offers)
            _itemsByProto.TryAdd(item.Proto, item);

        // Отбрасываем выбор по индексам, которые после обновления ассортимента
        // указывают уже на другой товар (или на позицию, которой больше нет).
        var currentOffers = new Dictionary<int, string>();
        foreach (var item in state.Offers)
            currentOffers[item.Index] = item.Proto;

        _selectedOffers.RemoveWhere(index =>
            !currentOffers.TryGetValue(index, out var proto) ||
            _selectedOfferProtos.GetValueOrDefault(index) != proto);
        _selectedOfferProtos.Clear();
        foreach (var index in _selectedOffers)
            _selectedOfferProtos[index] = currentOffers[index];

        _playerColumn.Rows.RemoveAllChildren();
        foreach (var item in state.Requests)
        {
            var row = new N14MerchantRow(item.Proto, -1, item.Name, item.Price, item.Count, _font);
            row.Selected = _selectedRequests.Contains(item.Proto);
            row.OnClicked += OnRequestClicked;
            row.OnHovered += OnRowHovered;
            _playerColumn.Rows.AddChild(row);
        }

        _merchantColumn.Rows.RemoveAllChildren();
        foreach (var item in state.Offers)
        {
            var row = new N14MerchantRow(item.Proto, item.Index, item.Name, item.Price, item.Count, _font);
            row.Selected = _selectedOffers.Contains(item.Index);
            row.OnClicked += OnOfferClicked;
            row.OnHovered += OnRowHovered;
            _merchantColumn.Rows.AddChild(row);
        }

        _selectedRequests.RemoveWhere(p => state.Requests.All(r => r.Proto != p));

        RecenterWindow(new Vector2(0.5f, 0.5f));
        RebuildMiddle();
    }

    private void OnOfferClicked(N14MerchantRow row)
    {
        if (row.Selected)
        {
            _selectedOffers.Add(row.OfferIndex);
            _selectedOfferProtos[row.OfferIndex] = row.Proto;
            N14MerchantSounds.PlayItemSelected();
        }
        else
        {
            _selectedOffers.Remove(row.OfferIndex);
            _selectedOfferProtos.Remove(row.OfferIndex);
            N14MerchantSounds.PlayItemDeselected();
        }

        RebuildMiddle();
    }

    private void OnRequestClicked(N14MerchantRow row)
    {
        if (row.Selected)
        {
            _selectedRequests.Add(row.Proto);
            N14MerchantSounds.PlayItemSelected();
        }
        else
        {
            _selectedRequests.Remove(row.Proto);
            N14MerchantSounds.PlayItemDeselected();
        }

        RebuildMiddle();
    }

    /// <summary>
    ///     Показывает спрайт-предпросмотр и info-боксы под левой колонкой, пока курсор на предмете.
    /// </summary>
    private void OnRowHovered(string? proto)
    {
        if (proto == null || !_itemsByProto.TryGetValue(proto, out var item))
        {
            _previewPanel.Visible = false;
            _itemData.Visible = false;
            return;
        }

        // Цена — есть у любого предмета.
        _priceBox.SetText(Loc.GetString("n14-merchant-info-price", ("price", item.Price)));
        _priceBox.Visible = true;

        // Калибр и патроны — для огнестрельного; заряд батареи — для лазерного/плазменного.
        if (!string.IsNullOrEmpty(item.Caliber))
        {
            _ammoBox.SetText(Loc.GetString("n14-merchant-info-ammo",
                ("caliber", item.Caliber),
                ("mag", item.AmmoInMag),
                ("cap", item.MagCapacity),
                ("bag", item.AmmoInBag)));
            _ammoBox.Visible = true;
        }
        else if (item.IsEnergyWeapon)
        {
            _ammoBox.SetText(Loc.GetString("n14-merchant-info-charge",
                ("mag", item.AmmoInMag),
                ("cap", item.MagCapacity)));
            _ammoBox.Visible = true;
        }
        else
        {
            _ammoBox.Visible = false;
        }

        // Большой блок с описанием предмета: текст масштабируется, чтобы не сдвигать info-боксы.
        _descText = EscapeMarkup(item.Description);

        // Иконка, описание и info-боксы показываются для любого предмета.
        _previewPanel.Visible = true;
        _itemData.Visible = true;

        // Иконка из YAML-списка (или дефолт iconsother.rsi).
        _preview.Texture = LoadPreviewTexture(item.IconPath, item.IconState);
        LayoutItemData();
    }

    /// <summary>
    ///     Экранирует служебные для markup-разметки символы, чтобы описание рендерилось как текст.
    /// </summary>
    private static string EscapeMarkup(string text)
        => text.Replace("[", "[lb]");

    /// <summary>
    ///     Размер рамки предпросмотра по «золотому стандарту»: высота фиксирована, ширина —
    ///     из пропорций эталонной иконки (N14WeaponRifle556Service). Внутри рамки каждая иконка
    ///     вписывается целиком, поэтому её ширина, высота и общий размер меняются относительно
    ///     эталона, но сама картинка не растягивается.
    /// </summary>
    private static Vector2 GetPreviewBox()
    {
        var aspect = PreviewFallbackAspect;

        if (TryLoadRsiFirstFrame(StandardPreviewRsiPath) is { Height: > 0 } standard)
            aspect = (float)standard.Width / standard.Height;

        return new Vector2(PreviewBoxHeight * aspect, PreviewBoxHeight);
    }

    /// <summary>
    ///     Первый кадр состояния RSI с иконкой предпросмотра (или null, если не нашли).
    ///     <paramref name="state"/> задаёт конкретное состояние; без него берётся "icon", затем первое.
    /// </summary>
    private static Texture? LoadPreviewTexture(string? rsiPath, string? state = null)
    {
        var path = string.IsNullOrEmpty(rsiPath) ? DefaultPreviewRsiPath : rsiPath;
        var key = string.IsNullOrEmpty(state) ? path : $"{path}#{state}";

        if (PreviewCache.TryGetValue(key, out var cached))
            return cached;

        var texture = TryLoadRsiFirstFrame(path, state);

        // Путь из списка мог быть неверным/битым — падаем на дефолтную иконку.
        if (texture == null && path != DefaultPreviewRsiPath)
            texture = TryLoadRsiFirstFrame(DefaultPreviewRsiPath);

        PreviewCache[key] = texture;
        return texture;
    }

    /// <summary>
    ///     Кадр иконки из RSI. Приоритет: заданное состояние -> "icon" -> первое доступное.
    /// </summary>
    private static Texture? TryLoadRsiFirstFrame(string path, string? state = null)
    {
        var cache = IoCManager.Resolve<IResourceCache>();

        if (!cache.TryGetResource<RSIResource>(path, out var rsi))
            return null;

        // Явно заданное состояние (например, "iron" для руды).
        if (!string.IsNullOrEmpty(state) && rsi.RSI.TryGetState(state, out var requested))
            return requested.Frame0;

        // Prefer the inventory icon state so clothes/armour previews show the item sprite.
        if (rsi.RSI.TryGetState("icon", out var icon))
            return icon.Frame0;

        foreach (var rsiState in rsi.RSI)
            return rsiState.Frame0;

        return null;
    }

    /// <summary>
    ///     Между колонками — по одной стрелке на направление с суммарной ценой.
    ///     Покупка — минус (тратим крышки), продажа — плюс (получаем крышки).
    /// </summary>
    private void RebuildMiddle()
    {
        _middle.RemoveAllChildren();

        // Суммируем выбранные позиции, чтобы не плодить стрелки с отдельными ценами.
        var buyTotal = 0;
        var sellTotal = 0;

        foreach (var row in _merchantColumn.Rows.Children)
        {
            if (row is N14MerchantRow offer && offer.Selected)
                buyTotal += offer.Price;
        }

        foreach (var row in _playerColumn.Rows.Children)
        {
            if (row is N14MerchantRow request && request.Selected)
                sellTotal += request.Price;
        }

        // Покупка у торговца: списываем крышки — знак «-».
        if (buyTotal > 0)
            _middle.AddChild(CreateIndicator(true, -buyTotal));

        // Продажа торговцу: получаем крышки — знак «+».
        if (sellTotal > 0)
            _middle.AddChild(CreateIndicator(false, sellTotal));
    }

    private static void ApplyButtonStyle(Button button)
    {
        // Без серой заливки: только янтарный контур.
        button.StyleBoxOverride = new StyleBoxFlat
        {
            BackgroundColor = Color.Transparent,
            BorderColor = N14MerchantTheme.Accent,
            BorderThickness = new Thickness(1),
        };
    }

    private Control CreateIndicator(bool pointRight, int price)
    {
        // Стрелка и цифра накладываются друг на друга: цифра — по центру внутри стрелки.
        var overlay = new LayoutContainer
        {
            HorizontalAlignment = HAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var arrow = new N14MerchantArrow(pointRight)
        {
            LineColor = N14MerchantTheme.Accent,
        };
        LayoutContainer.SetAnchorPreset(arrow, LayoutContainer.LayoutPreset.Wide);
        overlay.AddChild(arrow);

        if (price != 0)
        {
            var text = new Label
            {
                Text = price >= 0 ? $"+{price}" : price.ToString(),
                FontOverride = N14MerchantTheme.GetFont(14),
                FontColorOverride = N14MerchantTheme.Accent,
                HorizontalAlignment = HAlignment.Center,
                VerticalAlignment = VAlignment.Center,
                MouseFilter = Control.MouseFilterMode.Ignore,
            };
            LayoutContainer.SetAnchorPreset(text, LayoutContainer.LayoutPreset.Wide);
            overlay.AddChild(text);
        }

        return overlay;
    }

    public (List<int> Buy, List<string> Sell) GetSelection()
        => (_selectedOffers.ToList(), _selectedRequests.ToList());

    public void ClearSelection()
    {
        _selectedOffers.Clear();
        _selectedOfferProtos.Clear();
        _selectedRequests.Clear();

        foreach (var row in _merchantColumn.Rows.Children)
        {
            if (row is N14MerchantRow offer)
                offer.Selected = false;
        }

        foreach (var row in _playerColumn.Rows.Children)
        {
            if (row is N14MerchantRow request)
                request.Selected = false;
        }

        RebuildMiddle();
    }
}
