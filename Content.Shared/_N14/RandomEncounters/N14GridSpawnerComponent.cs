using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._N14.RandomEncounters;

/// <summary>
/// A map-editor marker that loads a pre-mapped grid file into the map on MapInit.
/// The loaded grid's local origin lands on the spawner's position. Used to place
/// hand-mapped random-encounter sites (e.g. raider checkpoints) at runtime.
/// </summary>
[RegisterComponent]
public sealed partial class N14GridSpawnerComponent : Component
{
    /// <summary>Grid map file to load (e.g. /Maps/N14/raider_checkpoint.yml).</summary>
    [DataField("gridPath")]
    public ResPath? GridPath;

    /// <summary>Extra rotation applied to the loaded grid.</summary>
    [DataField("rotation")]
    public Angle Rotation;
}