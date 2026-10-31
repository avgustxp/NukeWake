using System.Numerics;
using System.Text;
using Content.Client.Resources;
using Robust.Client.Audio;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Audio.Sources;
using Robust.Shared.Input;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Forge.Merchant;

/// <summary>
///     Цвета и шрифты UI торговца (FNV-подобная палитра).
/// </summary>
public static class N14MerchantTheme
{
    /// <summary>Цвет заливки UI: rgb(27, 255, 128).</summary>
    public static readonly Color Ui = Color.FromHex("#0f2519");

    /// <summary>Декоративный/акцентный цвет: rgb(255, 182, 66).</summary>
    public static readonly Color Accent = Color.FromHex("#FFB642");

    /// <summary>Задний фон окна (задний план под зелёным свечением).</summary>
    public static readonly Color Background = Color.FromHex("#09160D");

    /// <summary>Прозрачность заднего фона — мир за окном должен проглядывать.</summary>
    public const float BackgroundAlpha = 0.5f;

    public static Color UiTransparent => Ui.WithAlpha(0.5f);

    private const string FontPath = "/Fonts/Monofonto/Monofonto.ttf";
    private const string FallbackFontPath = "/Fonts/NotoSans/NotoSans-Regular.ttf";

    public static Font GetFont(int size = 12)
    {
        var cache = IoCManager.Resolve<IResourceCache>();

        // Monofonto — латиница (Fallout-стиль), NotoSans подхватывает кириллицу и прочие глифы.
        if (cache.TryGetResource<FontResource>(FontPath, out var res))
            return cache.GetFont(new[] { FontPath, FallbackFontPath }, size);

        return cache.GetFont(FallbackFontPath, size);
    }

    /// <summary>
    ///     Ширина текста по метрикам шрифта (в пикселях UI при scale=1).
    /// </summary>
    public static float MeasureText(Font font, string text)
    {
        var width = 0f;

        foreach (var rune in text.EnumerateRunes())
        {
            if (font.TryGetCharMetrics(rune, 1f, out var metrics))
                width += metrics.Advance;
        }

        return width;
    }
}

/// <summary>
///     Полупрозрачная заливка UI с зелёными размытыми краями
///     и декоративной акцентной линией внизу (прямые углы на концах).
/// </summary>
public sealed class N14MerchantBackdrop : Control
{
    private const int SoftSteps = 6;
    private const float SoftSpacing = 3f;
    private const float LineInset = 18f;
    private const float LineBend = 10f;

    /// <summary>
    ///     Контрол, под которым рисуется нижняя линия: её края совпадают с краями этого контрола
    ///     (обычно — контейнер колонок).
    /// </summary>
    public Control? LineTarget { get; set; }

    public N14MerchantBackdrop()
    {
        MouseFilter = Control.MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = PixelSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        var ui = N14MerchantTheme.Ui;
        var accent = N14MerchantTheme.Accent;

        // Размытые края: тонкое зелёное свечение вокруг основной заливки.
        for (var i = SoftSteps; i > 0; i--)
        {
            var alpha = 0.22f * (1f - i / (float)(SoftSteps + 1));
            var color = ui.WithAlpha(alpha);

            var pad = i * SoftSpacing;
            handle.DrawRect(new UIBox2(pad, pad, size.X - pad, size.Y - pad), color);
        }

        // Основная заливка — #061109, полупрозрачная.
        handle.DrawRect(new UIBox2(0, 0, size.X, size.Y), N14MerchantTheme.Background.WithAlpha(N14MerchantTheme.BackgroundAlpha));

        // Декоративная линия внизу (там, где начинается размытие), концы подняты под прямым углом.
        var y = size.Y - SoftSteps * SoftSpacing - 4f;

        // По умолчанию — по ширине окна с отступом, но если задан LineTarget,
        // выравниваем края линии по краям колонок.
        float left = LineInset;
        float right = size.X - LineInset;

        if (LineTarget != null)
        {
            var targetLeft = (LineTarget.GlobalPosition.X - GlobalPosition.X) * UIScale;
            left = targetLeft;
            right = targetLeft + LineTarget.Size.X * UIScale;
        }

        left = Math.Clamp(left, 0f, size.X);
        right = Math.Clamp(right, 0f, size.X);

        handle.DrawLine(new Vector2(left, y), new Vector2(right, y), accent);

        // Загибы вверх на концах — вертикальные отрезки, угол 90°.
        handle.DrawLine(new Vector2(left, y), new Vector2(left, y - LineBend), accent);
        handle.DrawLine(new Vector2(right, y), new Vector2(right, y - LineBend), accent);
    }
}

/// <summary>
///     Открытая рамка колонки: верхняя и нижняя границы без боковых сторон,
///     с прямыми углами на концах и разрывом под заголовок.
/// </summary>
public sealed class N14MerchantFrame : Control
{
    private const float Tick = 9f;
    private const float Inset = 6f;

    /// <summary>Ширина разрыва под заголовок по центру верхней границы.</summary>
    public float HeaderGap { get; set; } = 60f;

    /// <summary>Смещение разрыва от центра колонки (в пикселях).</summary>
    public float HeaderOffset { get; set; }

    public Color LineColor { get; set; } = N14MerchantTheme.Accent;

    public N14MerchantFrame()
    {
        MouseFilter = Control.MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = PixelSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        var color = LineColor;
        var halfGap = HeaderGap / 2f + Inset;
        var centerX = size.X / 2f + HeaderOffset;

        // Верхняя граница — прямая горизонталь с разрывом под заголовок.
        var yTop = 1f;
        handle.DrawLine(new Vector2(Inset + Tick, yTop), new Vector2(centerX - halfGap, yTop), color);
        handle.DrawLine(new Vector2(centerX + halfGap, yTop), new Vector2(size.X - Inset - Tick, yTop), color);

        // Загибы вниз на концах — вертикальные, угол 90°.
        handle.DrawLine(new Vector2(Inset + Tick, yTop), new Vector2(Inset + Tick, yTop + Tick), color);
        handle.DrawLine(new Vector2(size.X - Inset - Tick, yTop), new Vector2(size.X - Inset - Tick, yTop + Tick), color);

        // Нижняя граница.
        var yBot = size.Y - 1f;
        handle.DrawLine(new Vector2(Inset + Tick, yBot), new Vector2(size.X - Inset - Tick, yBot), color);
        handle.DrawLine(new Vector2(Inset + Tick, yBot), new Vector2(Inset + Tick, yBot - Tick), color);
        handle.DrawLine(new Vector2(size.X - Inset - Tick, yBot), new Vector2(size.X - Inset - Tick, yBot - Tick), color);
    }
}

/// <summary>
///     Треугольник-указатель у заголовка: основание примыкает к слову, вершина смотрит наружу.
/// </summary>
public sealed class N14MerchantTriangle : Control
{
    private const float Width = 10f;
    private const float Height = 15f;

    /// <summary>true — вершина смотрит вправо (▶), false — влево (◀).</summary>
    public bool PointRight { get; }

    public Color Fill { get; set; } = N14MerchantTheme.Accent;

    public N14MerchantTriangle(bool pointRight)
    {
        PointRight = pointRight;
        MinSize = new Vector2(Width, Height);
        MaxSize = new Vector2(Width, Height);
        MouseFilter = Control.MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = PixelSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        var points = PointRight
            ? new List<Vector2>
            {
                new(0f, 0f),
                new(size.X, size.Y / 2f),
                new(0f, size.Y),
            }
            : new List<Vector2>
            {
                new(size.X, 0f),
                new(size.X, size.Y),
                new(0f, size.Y / 2f),
            };

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, points, Fill);
    }
}

/// <summary>
///     Нарисованная стрелка перевода (вместо текстовых символов ⇨/⇦).
///     Контур стрелки: древко-прямоугольник и треугольный наконечник (только обводка).
/// </summary>
public sealed class N14MerchantArrow : Control
{
    private const float DefaultWidth = 96f;
    private const float DefaultHeight = 60f;

    /// <summary>true — стрелка смотрит вправо, false — влево.</summary>
    public bool PointRight { get; }

    public Color LineColor { get; set; } = N14MerchantTheme.Accent;

    public N14MerchantArrow(bool pointRight)
    {
        PointRight = pointRight;
        MinSize = new Vector2(DefaultWidth, DefaultHeight);
        MaxSize = new Vector2(DefaultWidth, DefaultHeight);
        MouseFilter = Control.MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = PixelSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        var color = LineColor;

        // Геометрия стрелки: наконечник занимает половину ширины, древко — центр по высоте.
        var headWidth = size.X * 0.5f;
        var shaftHeight = size.Y * 0.34f;
        var shaftTop = (size.Y - shaftHeight) / 2f;
        var shaftBottom = shaftTop + shaftHeight;

        if (PointRight)
        {
            var neckX = size.X - headWidth;

            // Древко (верх/низ).
            handle.DrawLine(new Vector2(0f, shaftTop), new Vector2(neckX, shaftTop), color);
            handle.DrawLine(new Vector2(0f, shaftBottom), new Vector2(neckX, shaftBottom), color);
            // Задний торец древка.
            handle.DrawLine(new Vector2(0f, shaftTop), new Vector2(0f, shaftBottom), color);
            // Наконечник.
            handle.DrawLine(new Vector2(neckX, 0f), new Vector2(size.X, size.Y / 2f), color);
            handle.DrawLine(new Vector2(size.X, size.Y / 2f), new Vector2(neckX, size.Y), color);
            // Вертикальная перекладина наконечника.
            handle.DrawLine(new Vector2(neckX, 0f), new Vector2(neckX, shaftTop), color);
            handle.DrawLine(new Vector2(neckX, shaftBottom), new Vector2(neckX, size.Y), color);
        }
        else
        {
            var neckX = headWidth;

            // Древко (верх/низ).
            handle.DrawLine(new Vector2(size.X, shaftTop), new Vector2(neckX, shaftTop), color);
            handle.DrawLine(new Vector2(size.X, shaftBottom), new Vector2(neckX, shaftBottom), color);
            // Задний торец древка.
            handle.DrawLine(new Vector2(size.X, shaftTop), new Vector2(size.X, shaftBottom), color);
            // Наконечник.
            handle.DrawLine(new Vector2(neckX, 0f), new Vector2(0f, size.Y / 2f), color);
            handle.DrawLine(new Vector2(0f, size.Y / 2f), new Vector2(neckX, size.Y), color);
            // Вертикальная перекладина наконечника.
            handle.DrawLine(new Vector2(neckX, 0f), new Vector2(neckX, shaftTop), color);
            handle.DrawLine(new Vector2(neckX, shaftBottom), new Vector2(neckX, size.Y), color);
        }
    }
}

/// <summary>
///     Маркер выбора строки: небольшой квадратик слева от названия предмета.
///     По длине чуть меньше блока с названием (высота строки).
/// </summary>
public sealed class N14MerchantSelectMark : Control
{
    private const float MarkSize = 14f;

    /// <summary>Отступ с боков маркера (слева от строки и справа от маркера).</summary>
    public const float MarkSideInset = 6f;

    public Color Color { get; set; } = N14MerchantTheme.Accent;

    public N14MerchantSelectMark()
    {
        MinSize = new Vector2(MarkSize, MarkSize);
        MaxSize = new Vector2(MarkSize, MarkSize);
        MouseFilter = Control.MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = PixelSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        // Полностью залитый янтарный квадрат (не контур).
        handle.DrawRect(new UIBox2(0f, 0f, size.X, size.Y), Color, true);
    }
}

/// <summary>
///     Колонка торговца: рамка + заголовок с треугольниками + капсы + скроллируемый список.
/// </summary>
public sealed class N14MerchantColumn : LayoutContainer
{
    private const float HeaderGapPadding = 32f;
    private const float HeaderSpacing = 8f;

    public Label Header { get; }
    public Label Caps { get; }
    public BoxContainer Rows { get; }

    private readonly N14MerchantFrame _frame;
    private readonly BoxContainer _headerRow;
    private readonly Font _headerFont = N14MerchantTheme.GetFont(16);

    public N14MerchantColumn(string headerText)
    {
        // Фиксируем размер колонки, чтобы обе колонки были одинаковой ширины/высоты
        // и их рамки (верхняя и нижняя линии) совпадали по горизонтали.
        InheritChildMeasure = false;
        MinSize = new Vector2(320, 360);
        MaxSize = new Vector2(320, 10000);

        _frame = new N14MerchantFrame();

        Header = new Label
        {
            Text = headerText,
            VerticalAlignment = VAlignment.Center,
            FontColorOverride = N14MerchantTheme.Accent,
            FontOverride = _headerFont,
        };

        _headerRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
        };
        // Пробелы между треугольниками и текстом заголовка.
        _headerRow.AddChild(new N14MerchantTriangle(false)
        {
            VerticalAlignment = VAlignment.Center,
            Margin = new Thickness(0, 0, HeaderSpacing, 0),
        });
        _headerRow.AddChild(Header);
        _headerRow.AddChild(new N14MerchantTriangle(true)
        {
            VerticalAlignment = VAlignment.Center,
            Margin = new Thickness(HeaderSpacing, 0, 0, 0),
        });

        Caps = new Label
        {
            // Крышки — у правого края колонки, не заходя за правую линию рамки (тик на width-15).
            HorizontalAlignment = HAlignment.Right,
            FontColorOverride = N14MerchantTheme.Accent,
            FontOverride = N14MerchantTheme.GetFont(14),
            Margin = new Thickness(0, 8, 9, 2),
        };

        Rows = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
        };

        var scroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            // Отступ, чтобы первая строка не налезала на слово "Caps".
            Margin = new Thickness(0, 30, 0, 0),
        };
        scroll.AddChild(Rows);

        var content = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            // Отрицательный верхний отступ ставит заголовок по центру верхней линии рамки (y ≈ 1).
            Margin = new Thickness(8, -9, 8, 16),
        };
        content.AddChild(_headerRow);
        content.AddChild(Caps);
        content.AddChild(scroll);

        AddChild(content);
        AddChild(_frame);

        LayoutContainer.SetAnchorPreset(content, LayoutContainer.LayoutPreset.Wide);
        LayoutContainer.SetAnchorPreset(_frame, LayoutContainer.LayoutPreset.Wide);

        SetHeaderText(headerText);
    }

    /// <summary>
    ///     Меняет текст заголовка и подгоняет разрыв рамки под его ширину.
    /// </summary>
    public void SetHeaderText(string text)
    {
        Header.Text = text;
        _frame.HeaderGap = N14MerchantTheme.MeasureText(_headerFont, text) + HeaderGapPadding;
    }

    /// <summary>
    ///     Сдвигает заголовок (и разрыв в рамке) от центра колонки.
    ///     Отрицательное значение — влево, положительное — вправо.
    /// </summary>
    public void SetHeaderOffset(float shift)
    {
        _headerRow.Margin = shift >= 0
            ? new Thickness(shift * 2f, 0, 0, 0)
            : new Thickness(0, 0, -shift * 2f, 0);

        _frame.HeaderOffset = shift;
    }
}

/// <summary>
///     Строка списка: название слева, цена справа; подсветка при выборе.
/// </summary>
public sealed class N14MerchantRow : PanelContainer
{
    private static readonly Thickness NoMargin = new(4, 2, 4, 2);

    /// <summary>Путь к звуку наведения курсора на строку.</summary>
    private const string HoverSoundPath = "/Audio/_Forge/UserInterface/ui_menu_focus.wav";

    /// <summary>Громкость звука наведения в дБ (0 — без усиления).</summary>
    private const float HoverSoundVolume = 6f;

    /// <summary>
    ///     Заранее загруженный источник звука наведения. Один общий: перезапуск с начала
    ///     (<see cref="IAudioSource.Restart"/>) без задержки и без наложения на прошлый звук.
    /// </summary>
    private static IAudioSource? _hoverSource;
    private static bool _hoverSourceLoaded;

    private readonly Label _name;
    private readonly Label _price;
    private readonly Control _mark;

    public string Proto { get; }

    /// <summary>Индекс в Stock (для покупки). -1 для строк-запросов.</summary>
    public int OfferIndex { get; }

    public int Price { get; }

    public event Action<N14MerchantRow>? OnClicked;

    /// <summary>Срабатывает при наведении/уходе курсора; аргумент — прототип под курсором или null.</summary>
    public event Action<string?>? OnHovered;

    private bool _selected;
    private bool _hovered;

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            ApplyStyle();
        }
    }

    public N14MerchantRow(string proto, int offerIndex, string name, int price, int count, Font font)
    {
        Proto = proto;
        OfferIndex = offerIndex;
        Price = price;

        EnsureHoverSource();

        // Без этого клик по строке не доходит: по умолчанию MouseFilter = Ignore.
        MouseFilter = Control.MouseFilterMode.Stop;

        // Наведение подсвечивает рамку строки и перезапускает звук с самого начала.
        OnMouseEntered += _ =>
        {
            _hovered = true;
            ApplyStyle();
            _hoverSource?.Restart();
            OnHovered?.Invoke(Proto);
        };
        OnMouseExited += _ =>
        {
            _hovered = false;
            ApplyStyle();
            OnHovered?.Invoke(null);
        };

        _name = new Label
        {
            Text = count > 1 ? $"{name} ({count})" : name,
            FontOverride = font,
            FontColorOverride = N14MerchantTheme.Accent,
            HorizontalAlignment = HAlignment.Left,
            VerticalAlignment = VAlignment.Center,
            HorizontalExpand = true,
        };

        // Квадратик слева от названия — маркер выбора (виден только когда строка выбрана).
        _mark = new N14MerchantSelectMark
        {
            VerticalAlignment = VAlignment.Center,
            Visible = false,
            Margin = new Thickness(N14MerchantSelectMark.MarkSideInset, 0, N14MerchantSelectMark.MarkSideInset, 0),
        };

        // У колонки торговца цена со знаком плюс, у колонки игрока — просто число.
        var sign = offerIndex >= 0 ? "+" : string.Empty;
        _price = new Label
        {
            Text = price == 0 ? string.Empty : $"{sign}{Math.Abs(price)}",
            FontOverride = font,
            FontColorOverride = N14MerchantTheme.Accent,
            HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Center,
        };

        var inner = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
        };
        inner.AddChild(_mark);
        inner.AddChild(_name);
        inner.AddChild(_price);

        AddChild(inner);
        MinHeight = 26;
        ApplyStyle();
    }

    /// <summary>
    ///     Один раз создаёт общий источник звука наведения (загружается заранее, вне игрового
    ///     цикла) — так звук стартует мгновенно и всегда проигрывается с начала.
    /// </summary>
    private static void EnsureHoverSource()
    {
        if (_hoverSourceLoaded)
            return;

        _hoverSourceLoaded = true;

        var cache = IoCManager.Resolve<IResourceCache>();

        if (!cache.TryGetResource<AudioResource>(HoverSoundPath, out var resource))
            return;

        var source = IoCManager.Resolve<IAudioManager>().CreateAudioSource(resource);

        if (source == null)
            return;

        source.Global = true;
        source.Volume = HoverSoundVolume;
        _hoverSource = source;
    }

    private void ApplyStyle()
    {
        // Выбор показывается маркером слева от названия, а не заливкой всей строки.
        _mark.Visible = _selected;

        Margin = NoMargin;
        _name.FontColorOverride = N14MerchantTheme.Accent;
        _price.FontColorOverride = N14MerchantTheme.Accent;

        // При наведении — только контур акцентного цвета, пока курсор на строке.
        PanelOverride = _hovered
            ? new StyleBoxFlat
            {
                BackgroundColor = Color.Transparent,
                BorderColor = N14MerchantTheme.Accent,
                BorderThickness = new Thickness(1),
            }
            : null;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        Selected = !Selected;
        args.Handle();
        OnClicked?.Invoke(this);
    }
}

/// <summary>
///     Рисует «половинную» рамку в стиле границ колонок: верхняя грань с загнутыми углами
///     и боковые вертикали только до половины высоты. Нижняя грань и нижние половины боковин убраны.
/// </summary>
public static class N14MerchantFrameDraw
{
    public const float Tick = 5f;

    public static void DrawHalfFrame(DrawingHandleScreen handle, Vector2 size, Color color)
    {
        if (size.X <= 0 || size.Y <= 0)
            return;

        // Боковые вертикали — только верхняя половина.
        var sideBottom = size.Y / 2f;
        handle.DrawLine(new Vector2(1f, Tick), new Vector2(1f, sideBottom), color);
        handle.DrawLine(new Vector2(size.X - 1f, Tick), new Vector2(size.X - 1f, sideBottom), color);

        // Верхняя грань с загнутыми углами вниз.
        handle.DrawLine(new Vector2(1f, 1f), new Vector2(size.X - 1f, 1f), color);
        handle.DrawLine(new Vector2(1f, 1f), new Vector2(1f, Tick), color);
        handle.DrawLine(new Vector2(size.X - 1f, 1f), new Vector2(size.X - 1f, Tick), color);
    }
}

/// <summary>
///     Маленький прямоугольный бокс-info под иконкой предпросмотра: рамка в стиле границ колонок
///     (боковые линии + загнутые углы) и подпись внутри. Используется для прочности/цены/калибра.
/// </summary>
public sealed class N14MerchantInfoBox : Control
{
    private readonly Label _label;

    public Color LineColor { get; set; } = N14MerchantTheme.Accent;

    /// <summary>Высота одной строки бокса (вместе с внутренним отступом).</summary>
    public const float RowHeight = 20f;

    public N14MerchantInfoBox(string text, Font font)
    {
        MouseFilter = Control.MouseFilterMode.Ignore;
        MinSize = new Vector2(0f, RowHeight);
        VerticalAlignment = VAlignment.Center;

        _label = new Label
        {
            Text = text,
            FontOverride = font,
            FontColorOverride = N14MerchantTheme.Accent,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
        };

        AddChild(_label);
    }

    public void SetText(string text) => _label.Text = text;

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        N14MerchantFrameDraw.DrawHalfFrame(handle, PixelSize, LineColor);
    }
}

/// <summary>
///     Блок описания предмета: контейнер с «половинной» рамкой (верхняя грань + половина боковин),
///     в стиле info-боксов. Содержит RichTextLabel с автопереносом.
/// </summary>
public sealed class N14MerchantDescBox : PanelContainer
{
    public Color LineColor { get; set; } = N14MerchantTheme.Accent;

    public N14MerchantDescBox()
    {
        MouseFilter = Control.MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        N14MerchantFrameDraw.DrawHalfFrame(handle, PixelSize, LineColor);
    }
}
