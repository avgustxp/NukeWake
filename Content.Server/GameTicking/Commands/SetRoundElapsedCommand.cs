
using System.Globalization;
using Content.Server.Administration;
using Content.Server.GameTicking;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.GameTicking.Commands;

[AdminCommand(AdminFlags.Round)]
public sealed class SetRoundElapsedCommand : IConsoleCommand
{
    public string Command => "setroundelapsed";
    public string Description => "Sets elapsed round time.";
    public string Help => "Usage: setroundelapsed <hh:mm:ss>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 ||
            !TimeSpan.TryParse(args[0], CultureInfo.InvariantCulture, out var elapsed) ||
            elapsed < TimeSpan.Zero)
        {
            shell.WriteError(Help);
            return;
        }

        var ticker = EntitySystem.Get<GameTicker>();

        if (!ticker.SetRoundElapsedTime(elapsed))
        {
            shell.WriteError("Round is not running.");
            return;
        }

        shell.WriteLine($"Round elapsed time set to {elapsed:c}.");
    }
}
