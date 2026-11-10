using Content.Client._Nuclear14.FactionReinforcement.UI;
using Content.Shared._Nuclear14.FactionReinforcement;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Nuclear14.FactionReinforcement;

[UsedImplicitly]
public sealed class FactionReinforcementBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private FactionReinforcementWindow? _window;

    public FactionReinforcementBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindowCenteredLeft<FactionReinforcementWindow>();
        _window.ReinforceRequested += job => SendMessage(new FactionReinforcementRequestMessage(job));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not FactionReinforcementBoundUserInterfaceState bState)
            return;

        _window?.UpdateState(bState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _window?.Close();
            _window = null;
        }
    }
}
