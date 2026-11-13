using Content.Server.Radiation.Components;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Events;
using Robust.Shared.Configuration;

namespace Content.Server.Radiation.Systems;

/// <summary>
///     Casts radiation from entities with <see cref="RadiationSourceComponent"/>
///     to entities with <see cref="RadiationReceiverComponent"/>.
/// </summary>
/// <remarks>
///     An update is split into cheap stages so that the expensive gridcast only runs
///     for pairs that can actually be reached:
///     <list type="number">
///         <item>sources are prepared once per update (stack count, blockers, world position, range);</item>
///         <item>receivers are filtered by map and by a squared distance check (no sqrt, no allocations);</item>
///         <item>only reached pairs are traced through radiation blockers.</item>
///     </list>
///     Nothing is allocated per source/receiver pair unless the debug overlay is enabled.
/// </remarks>
public sealed partial class RadiationSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeCvars();
        InitRadBlocking();
        InitGridcast();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < GridcastUpdateRate)
            return;

        UpdateGridcast();
        UpdateResistanceDebugOverlay();
        _accumulator = 0f;
    }

    public void IrradiateEntity(EntityUid uid, float radsPerSecond, float time)
    {
        var msg = new OnIrradiatedEvent(time, radsPerSecond);
        RaiseLocalEvent(uid, msg);
    }

    public void SetSourceEnabled(Entity<RadiationSourceComponent?> entity, bool val)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        entity.Comp.Enabled = val;
    }

    /// <summary>
    ///     Marks entity to receive/ignore radiation rays.
    /// </summary>
    public void SetCanReceive(EntityUid uid, bool canReceive)
    {
        if (canReceive)
        {
            EnsureComp<RadiationReceiverComponent>(uid);
        }
        else
        {
            RemComp<RadiationReceiverComponent>(uid);
        }
    }
}
