using Robust.Client.Audio;
using Robust.Client.ResourceManagement;
using Robust.Shared.Audio.Sources;
using Robust.Shared.IoC;
using Robust.Shared.Random;

namespace Content.Client._Forge.Merchant;

/// <summary>
///     UI-звуки окна торговца. Пары файлов проигрываются случайно («или тот, или тот»).
///     Источники создаются один раз и перезапускаются с начала, поэтому звук срабатывает
///     мгновенно и не ждёт загрузки файла.
/// </summary>
public static class N14MerchantSounds
{
    // Звук перемещения вещи (в покупку/продажу).
    private const string ClothingDown01 = "/Audio/_Forge/UserInterface/ui_items_clothing_down_01.wav";
    private const string ClothingDown03 = "/Audio/_Forge/UserInterface/ui_items_clothing_down_03.wav";

    // Звук получения крышек (продажа).
    private const string BottlecapsUp01 = "/Audio/_Forge/UserInterface/ui_items_bottlecaps_up_01.wav";
    private const string BottlecapsUp02 = "/Audio/_Forge/UserInterface/ui_items_bottlecaps_up_02.wav";

    // Подтверждение / отмена.
    private const string MenuOk = "/Audio/_Forge/UserInterface/ui_menu_ok.wav";
    private const string MenuCancel = "/Audio/_Forge/UserInterface/ui_menu_cancel.wav";

    /// <summary>Громкость UI-звуков в дБ (0 — без усиления).</summary>
    private const float UiVolume = 0f;

    private static readonly string[] ClothingDownSounds = { ClothingDown01, ClothingDown03 };
    private static readonly string[] BottlecapsUpSounds = { BottlecapsUp01, BottlecapsUp02 };

    /// <summary>Все используемые файлы — для предзагрузки при открытии окна.</summary>
    private static readonly string[] AllSounds =
    {
        ClothingDown01,
        ClothingDown03,
        BottlecapsUp01,
        BottlecapsUp02,
        MenuOk,
        MenuCancel,
    };

    /// <summary>Готовые источники по пути к файлу (null — файл не загрузился).</summary>
    private static readonly Dictionary<string, IAudioSource?> Sources = new();

    /// <summary>Заранее создаёт источники, чтобы первый клик не ждал загрузки файла.</summary>
    public static void Preload()
    {
        foreach (var path in AllSounds)
            GetSource(path);
    }

    /// <summary>
    ///     Предмет перемещён в список покупки/продажи: звук вещи + подтверждение выбора
    ///     (квадратик слева от названия).
    /// </summary>
    public static void PlayItemSelected()
    {
        PlayRandom(ClothingDownSounds);
        Play(MenuOk);
    }

    /// <summary>Выбор предмета отменён (квадратик пропал).</summary>
    public static void PlayItemDeselected()
    {
        Play(MenuCancel);
    }

    /// <summary>Нажата кнопка Accept.</summary>
    public static void PlayAccept()
    {
        Play(MenuOk);
    }

    /// <summary>Нажата кнопка Exit.</summary>
    public static void PlayExit()
    {
        Play(MenuCancel);
    }

    /// <summary>Совершена покупка: игрок забирает вещь у торговца.</summary>
    public static void PlayPurchase()
    {
        PlayRandom(ClothingDownSounds);
    }

    /// <summary>Совершена продажа: игрок получает крышки.</summary>
    public static void PlaySale()
    {
        PlayRandom(BottlecapsUpSounds);
    }

    private static void Play(string path)
    {
        GetSource(path)?.Restart();
    }

    private static void PlayRandom(string[] paths)
    {
        if (paths.Length == 0)
            return;

        Play(paths[IoCManager.Resolve<IRobustRandom>().Next(paths.Length)]);
    }

    private static IAudioSource? GetSource(string path)
    {
        if (Sources.TryGetValue(path, out var cached))
            return cached;

        IAudioSource? source = null;

        var cache = IoCManager.Resolve<IResourceCache>();

        if (cache.TryGetResource<AudioResource>(path, out var resource))
        {
            source = IoCManager.Resolve<IAudioManager>().CreateAudioSource(resource);

            if (source != null)
            {
                source.Global = true;
                source.Volume = UiVolume;
            }
        }

        Sources[path] = source;
        return source;
    }
}
