using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.Weather;

[Prototype("weather")]
public sealed partial class WeatherPrototype : IPrototype
{
    [IdDataField] public string ID { get; set; } = default!;

    [ViewVariables(VVAccess.ReadWrite), DataField("sprite")]
    public SpriteSpecifier? Sprite;

    [ViewVariables(VVAccess.ReadWrite), DataField("color")]
    public Color? Color;

    /// <summary>
    /// Sound to play on the affected areas.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("sound")]
    public SoundSpecifier? Sound;

    /// <summary>
    /// Additional ambient sounds that are played on top of <see cref="Sound"/>.
    /// Each entry is a separate looping stream, so they can be layered.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("sounds")]
    public List<SoundSpecifier> Sounds = new();

    /// <summary>
    /// Optional lightning strikes spawned while this weather is running.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("lightning")]
    public WeatherLightningSettings? Lightning;

    /// <summary>
    /// Weather temperature applied to floors.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("temperature")]
    public float Temperature = 308.15f;

    /// <summary>
    /// Locale id of the weather announcement message.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("message")]
    public LocId Message = string.Empty;

    /// <summary>
    /// Locale id of the announcement's sender, defaults to Inner Feeling.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("sender")]
    public LocId? Sender;

    /// <summary>
    /// Whenever to show message about new weather or not.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("showMessage")]
    public Boolean ShowMessage = false;

    /// <summary>
    /// If weather should deal radiation damage.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("radioactive")]
    public Boolean Radioactive = false;

    /// <summary>
    /// How many rads damage per second
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("rads")]
    public float RadsPerSecond = 1;

    /// <summary>
    /// How long is the weather, in seconds
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("duration")]
    public float Duration = 300;

    /// <summary>
    /// Chance of this weather being selected. During actual selection, the
    /// chance of an individual weather being selected is Chance/(Total chance
    /// of all weather prototypes defined). Setting this to 0 disables it.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public int Chance = 1;

}

/// <summary>
/// Describes random lightning strikes during weather.
/// </summary>
[DataDefinition]
public sealed partial class WeatherLightningSettings
{
    /// <summary>
    /// Minimum time in seconds between two strike attempts.
    /// </summary>
    [DataField]
    public float MinInterval = 25f;

    /// <summary>
    /// Maximum time in seconds between two strike attempts.
    /// </summary>
    [DataField]
    public float MaxInterval = 65f;

    /// <summary>
    /// Chance that a strike attempt actually spawns lightning.
    /// </summary>
    [DataField]
    public float Chance = 0.5f;

    /// <summary>
    /// Maximum distance in tiles from a random player where the strike can land.
    /// </summary>
    [DataField]
    public float Radius = 10f;

    /// <summary>
    /// How high above the strike point the bolt starts, in tiles.
    /// </summary>
    [DataField]
    public float Height = 8f;

    /// <summary>
    /// Entity prototype used for the lightning beam.
    /// </summary>
    [DataField]
    public EntProtoId Prototype = "N14WeatherLightning";
}
