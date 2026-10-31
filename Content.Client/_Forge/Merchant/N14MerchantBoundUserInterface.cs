using Content.Shared._Forge.Merchant;
using Robust.Client.UserInterface;

namespace Content.Client._Forge.Merchant;

/// <summary>
///     BUI торговца: открывает окно, шлёт состояния сервера, передаёт выбор игрока.
/// </summary>
public sealed class N14MerchantBoundUserInterface : BoundUserInterface
{
    private N14MerchantWindow? _window;

    public N14MerchantBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<N14MerchantWindow>();
        _window.OnAccept += OnAccept;
        _window.OnExit += Close;

        if (State is N14MerchantUiState state)
            _window.UpdateState(state);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is N14MerchantUiState merchantState)
            _window?.UpdateState(merchantState);
    }

    private void OnAccept()
    {
        if (_window == null)
            return;

        var (buy, sell) = _window.GetSelection();
        SendMessage(new N14MerchantCommitMessage
        {
            BuyIndices = new List<int>(buy),
            SellProtos = new List<string>(sell),
        });

        _window.ClearSelection();
    }
}
