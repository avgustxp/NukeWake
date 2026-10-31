using System.Numerics;
using Content.Shared.CCVar;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;

namespace Content.Client._Forge.Overlays;

/// <summary>
///     Applies a global fullscreen color filter to the local player's view,
///     replicating Fallout: New Vegas' amber wasteland tint.
/// </summary>
public sealed class N14ColorFilterSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayMan = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    /// <summary>#FFB642 as RGB in the 0-1 range.</summary>
    public static readonly Vector3 TintColor = new(1.0f, 0.713f, 0.259f);

    /// <summary>Opacity of the tint, ~30%.</summary>
    public const float TintOpacity = 0.30f;

    private N14ColorFilterOverlay _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new N14ColorFilterOverlay();
        Subs.CVar(_cfg, CCVars.NoVisionFilters, OnNoVisionFiltersChanged, true);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayMan.RemoveOverlay(_overlay);
    }

    private void OnNoVisionFiltersChanged(bool disabled)
    {
        if (disabled)
            _overlayMan.RemoveOverlay(_overlay);
        else
            _overlayMan.AddOverlay(_overlay);
    }
}
