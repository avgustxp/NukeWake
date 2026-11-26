/// Forge-Change-Start
using Content.Server.Administration;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Lightning;
using Content.Server.Maps;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Systems;
using Content.Shared.Administration;
using Content.Shared._N14.Weather;
using Content.Shared.CCVar;
using Content.Shared.Ghost;
using Content.Shared.Weather;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using System.Linq;
using System.Numerics;

namespace Content.Server.Weather;

public sealed class WeatherSystem : SharedWeatherSystem
{
    /// Forge-Change-Start
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly IConsoleHost _console = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly RadiationSystem _radiation = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly LightningSystem _lightning = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PostGameMapLoad>(OnGameMapLoaded);
        SubscribeLocalEvent<WeatherComponent, ComponentGetState>(OnWeatherGetState);
        _console.RegisterCommand("weather",
            Loc.GetString("cmd-weather-desc"),
            Loc.GetString("cmd-weather-help"),
            WeatherTwo,
            WeatherCompletion);
        _console.RegisterCommand("randomweather",
            Loc.GetString("cmd-randomweather-desc"),
            Loc.GetString("cmd-randomweather-help"),
            RandomWeatherCommand);
        _console.RegisterCommand("nextweather",
            Loc.GetString("cmd-nextweather-desc"),
            Loc.GetString("cmd-nextweather-help"),
            NextWeatherCommand);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _nextRandomWeatherTime = null;
        _effectAccumulators.Clear();
        _loadedGameMaps.Clear();
    }

    private void OnGameMapLoaded(PostGameMapLoad ev)
    {
        _loadedGameMaps[ev.Map] = ev.GameMap;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_config.GetCVar(CCVars.AutoWeather) || _gameTicker.RunLevel != GameRunLevel.InRound)
        {
            _nextRandomWeatherTime = null;
            return;
        }

        if (WeatherEventRunning())
        {
            _nextRandomWeatherTime = null;
            return;
        }

        if (_nextRandomWeatherTime == null)
        {
            ScheduleNextRandomWeather();
            return;
        }

        if (Timing.CurTime >= _nextRandomWeatherTime.Value)
        {
            _nextRandomWeatherTime = null;
            var (weather, map) = SetRandomWeather();

            if (weather != null)
            {
                Log.Info($"Randomizing weather to {weather.ID} on map {map}");
            }
            else
            {
                ScheduleNextRandomWeather();
            }
        }
    }

    protected override void Run(EntityUid uid, WeatherData weather, WeatherPrototype weatherProto, float frameTime)
    {
        base.Run(uid, weather, weatherProto, frameTime);

        if (weatherProto.Lightning is not { } lightning || weather.State != WeatherState.Running)
            return;

        var interval = TimeSpan.FromSeconds(_random.NextFloat(lightning.MinInterval, lightning.MaxInterval));

        if (weather.NextLightning == TimeSpan.Zero)
        {
            weather.NextLightning = Timing.CurTime + interval;
            return;
        }

        if (Timing.CurTime < weather.NextLightning)
            return;

        weather.NextLightning = Timing.CurTime + interval;

        if (!_random.Prob(lightning.Chance))
            return;

        TryStrikeWeatherLightning(uid, lightning);
    }

    private void TryStrikeWeatherLightning(EntityUid mapUid, WeatherLightningSettings settings)
    {
        var players = new List<EntityUid>();

        var query = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (query.MoveNext(out var player, out _, out var xform))
        {
            if (xform.MapUid != mapUid || HasComp<GhostComponent>(player))
                continue;

            players.Add(player);
        }

        if (players.Count == 0)
            return;

        var target = Transform(_random.Pick(players));
        var offset = _random.NextAngle().ToVec() * _random.NextFloat(1f, settings.Radius);
        var strikeMap = _transform.ToMapCoordinates(target.Coordinates.Offset(offset));
        var strike = Spawn("N14WeatherLightningStrikePoint", strikeMap);
        var sky = Spawn("N14WeatherLightningStrikePoint",
            new MapCoordinates(strikeMap.Position + new Vector2(0f, settings.Height), strikeMap.MapId));

        _lightning.ShootLightning(sky, strike, settings.Prototype.Id, triggerLightningEvents: false);
    }

    private bool WeatherRunning()
    {
        var query = EntityQueryEnumerator<WeatherComponent>();
        while (query.MoveNext(out _, out var comp))
        {
            foreach (var (protoId, _) in comp.Weather)
            {
                if (protoId == DefaultWeatherPrototype)
                    continue;

                return true;
            }
        }
        return false;
    }
    /// Forge-Change-End

    private void OnWeatherGetState(EntityUid uid, WeatherComponent component, ref ComponentGetState args)
    {
        args.State = new WeatherComponentState(component.Weather);
    }

    /// Forge-Change-Start
    [AdminCommand(AdminFlags.Fun)]
    private void WeatherTwo(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-no-arguments"));
            return;
        }

        if (!int.TryParse(args[0], out var mapInt))
            return;

        var mapId = new MapId(mapInt);

        if (!MapManager.MapExists(mapId))
            return;

        if (!MapManager.TryGetMap(mapId, out var mapUid))
            return;

        var weatherComp = EnsureComp<WeatherComponent>(mapUid.Value);

        //Weather Proto parsing
        WeatherPrototype? weather = null;
        if (!args[1].Equals("null"))
        {
            if (!ProtoMan.TryIndex(args[1], out weather))
            {
                shell.WriteError(Loc.GetString("cmd-weather-error-unknown-proto"));
                return;
            }
        }

        //Time parsing
        TimeSpan? endTime = null;
        if (args.Length == 3)
        {
            var curTime = Timing.CurTime;
            if (int.TryParse(args[2], out var durationInt))
            {
                endTime = curTime + TimeSpan.FromSeconds(durationInt);
            }
            else
            {
                shell.WriteError(Loc.GetString("cmd-weather-error-wrong-time"));
            }
        }

        SetWeather(mapId, weather, endTime);
    }

    [AdminCommand(AdminFlags.Fun)]
    private void RandomWeatherCommand(IConsoleShell shell, string argStr, string[] args)
    {
        var (weather, map) = SetRandomWeather();
        if (weather != null)
        {
            shell.WriteLine($"Picked {weather.ID} to run on map {map}");
        }
    }

    [AdminCommand(AdminFlags.Admin)]
    private void NextWeatherCommand(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_config.GetCVar(CCVars.AutoWeather))
        {
            shell.WriteLine(Loc.GetString("cmd-nextweather-auto-disabled"));
            return;
        }

        if (_gameTicker.RunLevel != GameRunLevel.InRound)
        {
            shell.WriteLine(Loc.GetString("cmd-nextweather-not-in-round"));
            return;
        }

        if (TryWriteActiveWeather(shell))
            return;

        if (_nextRandomWeatherTime == null)
            ScheduleNextRandomWeather();

        if (_nextRandomWeatherTime is not { } nextWeatherTime)
        {
            shell.WriteLine(Loc.GetString("cmd-nextweather-not-scheduled"));
            return;
        }

        var remaining = nextWeatherTime - Timing.CurTime;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        shell.WriteLine(Loc.GetString("cmd-nextweather-scheduled",
            ("time", FormatWeatherTime(remaining))));
    }

    private bool TryWriteActiveWeather(IConsoleShell shell)
    {
        var found = false;
        var query = EntityQueryEnumerator<WeatherComponent>();
        while (query.MoveNext(out _, out var comp))
        {
            foreach (var (protoId, data) in comp.Weather)
            {
                if (protoId == DefaultWeatherPrototype)
                    continue;

                found = true;
                var ending = data.EndTime == null
                    ? Loc.GetString("cmd-nextweather-active-indefinite")
                    : Loc.GetString("cmd-nextweather-active-ending",
                        ("time", FormatWeatherTime(data.EndTime.Value - Timing.CurTime)));

                shell.WriteLine(Loc.GetString("cmd-nextweather-active",
                    ("weather", protoId),
                    ("state", data.State),
                    ("ending", ending)));
            }
        }

        if (found)
            shell.WriteLine(Loc.GetString("cmd-nextweather-active-note"));

        return found;
    }

    private (WeatherPrototype?, MapId) SetRandomWeather()
    {
        var map = GetMainMap();
        if (map == MapId.Nullspace)
            return (null, map);

        _loadedGameMaps.TryGetValue(map, out var gameMap);
        var weather = RandomWeather(gameMap);
        if (weather != null)
        {
            SetWeather(map, weather, GetRandomWeatherEndTime(weather));
            return (weather, map);
        }
        return (weather, MapId.Nullspace);
    }

    private void ScheduleNextRandomWeather()
    {
        var delay = _random.NextFloat(
            (float) RandomWeatherMinDelay.TotalSeconds,
            (float) RandomWeatherMaxDelay.TotalSeconds);

        _nextRandomWeatherTime = Timing.CurTime + TimeSpan.FromSeconds(delay);
    }

    private TimeSpan GetRandomWeatherEndTime(WeatherPrototype weather)
    {
        var maxDuration = weather.Radioactive
            ? RandomRadioactiveWeatherMaxDuration
            : RandomWeatherMaxDuration;

        var minDuration = weather.Radioactive
            ? RandomRadioactiveWeatherMinDuration
            : RandomWeatherMinDuration;

        var duration = _random.NextFloat(
            (float) minDuration.TotalSeconds,
            (float) maxDuration.TotalSeconds);

        if (weather.Duration > 0f)
            duration = MathF.Min(duration, weather.Duration);

        return Timing.CurTime + TimeSpan.FromSeconds(duration);
    }

    /// <summary>
    /// Prefer the round's main map, falling back to the first map for standalone maps.
    /// </summary>
    private MapId GetMainMap()
    {
        if (_gameTicker.DefaultMap != MapId.Nullspace && MapManager.MapExists(_gameTicker.DefaultMap))
            return _gameTicker.DefaultMap;

        foreach (var mapId in MapManager.GetAllMapIds().OrderBy(id => id.GetHashCode()))
        {
            return mapId;
        }
        return MapId.Nullspace;
    }

    private WeatherPrototype? RandomWeather(GameMapPrototype? gameMap)
    {
        int totalChance = 0;
        foreach (var proto in _prototype.EnumeratePrototypes<WeatherPrototype>())
        {
            var weight = gameMap?.GetWeatherWeight(proto) ?? Math.Max(0, proto.Chance);
            if (weight <= 0)
                continue;

            totalChance += weight;
        }

        if (totalChance <= 0)
            return null;

        int tgtChance = _random.Next(totalChance);
        int curr = 0;
        foreach (var proto in _prototype.EnumeratePrototypes<WeatherPrototype>())
        {
            var weight = gameMap?.GetWeatherWeight(proto) ?? Math.Max(0, proto.Chance);
            if (weight <= 0)
                continue;

            if (curr <= tgtChance && tgtChance < curr + weight)
                return proto;
            curr += weight;
        }
        return null;
    }

    private static string FormatWeatherTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        if (time.TotalHours >= 1)
            return $"{(int) time.TotalHours}h {time.Minutes}m {time.Seconds}s";

        if (time.TotalMinutes >= 1)
            return $"{time.Minutes}m {time.Seconds}s";

        return $"{time.Seconds}s";
    }
    /// Forge-Change-End

    private CompletionResult WeatherCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(CompletionHelper.MapIds(EntityManager), "Map Id");

        var a = CompletionHelper.PrototypeIDs<WeatherPrototype>(true, ProtoMan);
        var b = a.Concat(new[] { new CompletionOption("null", Loc.GetString("cmd-weather-null")) });
        return CompletionResult.FromHintOptions(b, Loc.GetString("cmd-weather-hint"));
    }
}
