using Content.Server.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Popups;

namespace Content.Server._Forge.WorldEvents;

/// <summary>
///     Система триггер-предмета: при использовании в руке немедленно вызывает
///     крушение вертибёрда. Если крушение уже произошло — сообщает об этом.
/// </summary>
public sealed class N14VertibirdCrashTriggerSystem : EntitySystem
{
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly N14VertibirdCrashRuleSystem _crash = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<N14VertibirdCrashTriggerComponent, UseInHandEvent>(OnUseInHand);
    }

    private void OnUseInHand(Entity<N14VertibirdCrashTriggerComponent> ent, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        var query = EntityQueryEnumerator<N14VertibirdCrashRuleComponent>();
        while (query.MoveNext(out var ruleUid, out var rule))
        {
            if (rule.Crashed || HasComp<EndedGameRuleComponent>(ruleUid))
            {
                _popup.PopupEntity(Loc.GetString("n14-vertibird-trigger-already"), ent, args.User);
                return;
            }

            if (!HasComp<ActiveGameRuleComponent>(ruleUid))
            {
                // Правило добавлено, но ещё не запущено — запускаем немедленно.
                if (!_gameTicker.StartGameRule(ruleUid))
                    return;
            }

            _crash.TriggerCrash((ruleUid, rule));
            _popup.PopupEntity(Loc.GetString("n14-vertibird-triggered"), ent, args.User);
            return;
        }

        // Правила ещё нет в раунде — создаём и сразу запускаем.
        if (_gameTicker.StartGameRule(ent.Comp.RuleId, out var newRule) &&
            TryComp<N14VertibirdCrashRuleComponent>(newRule, out var newComp))
        {
            _crash.TriggerCrash((newRule, newComp));
            _popup.PopupEntity(Loc.GetString("n14-vertibird-triggered"), ent, args.User);
        }
    }
}
