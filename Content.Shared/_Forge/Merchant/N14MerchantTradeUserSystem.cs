using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Events;

namespace Content.Shared._Forge.Merchant;

/// <summary>
///     Блокирует движение игроку, пока у него открыто окно торговца.
/// </summary>
public sealed class N14MerchantTradeUserSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<N14MerchantTradeUserComponent, UpdateCanMoveEvent>(OnUpdateCanMove);
        SubscribeLocalEvent<N14MerchantTradeUserComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<N14MerchantTradeUserComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnUpdateCanMove(EntityUid uid, N14MerchantTradeUserComponent component, UpdateCanMoveEvent args)
    {
        if (component.LifeStage > ComponentLifeStage.Running)
            return;

        args.Cancel();
    }

    private void OnStartup(EntityUid uid, N14MerchantTradeUserComponent component, ComponentStartup args)
    {
        _actionBlocker.UpdateCanMove(uid);
    }

    private void OnShutdown(EntityUid uid, N14MerchantTradeUserComponent component, ComponentShutdown args)
    {
        _actionBlocker.UpdateCanMove(uid);
    }
}
