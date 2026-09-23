using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MowIT.Application.Services;
using MowIT.Application.UseCases;
using MowIT.Benchmarks;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;
using MowIT.Domain.Strategies;
using MowIT.Infrastructure.ScheduleSync;
using MowIT.Infrastructure.Services;
using MowIT.Shared.Schedules;
using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

internal static class ScheduleTestRunner
{
    private const int    DefaultRepeats       = 20;
    private const int    DefaultOffsetSeconds = 90;
    private const int    PollIntervalMs       = 500;
    private const int    ScheduleId           = 1;
    private const int    MowingState          = VirtualRobot.StateMowingValue;
    private const int    PrimeRoutePoints     = 100;
    private const int    TriggerCeilingSec    = 70;
    private const int    SyncCeilingSec       = 15;
    private const int    IdleCeilingSec       = 15;
    private const int    DoubleTriggerWatchSec = 25;
    private const double MetersPerDegree      = 111_319.444;
    private const double GpsEpsilonDeg        = 1e-9;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record RunRow(
        int RunIndex, DateTime ScheduledUtc, DateTime? DetectedUtc, DateTime? StartedUtc);

    private sealed record ScenarioRow(
        string Id, string Description, string Expected, string Actual, bool Pass);

    private sealed record OfflineRig(
        RecordingRobotBoundary Recorder,
        InMemoryScheduleRepository ScheduleStore,
        MowingSchedulerService Scheduler,
        MowingRoutePlanner Planner,
        BoundaryZone ZoneA,
        BoundaryZone ZoneB);

    public static async Task RunAsync(IConfiguration config)
    {
        string baseUrl = LoopbackUrl.Normalize(
            config["Robot:BaseUrl"] ?? "http://localhost:5080", out bool rewritten);
        string robotId = config["Robot:RobotId"] ?? "demo-robot-01";
        string token   = config["Robot:Token"]   ?? "dev-token-please-replace-in-prod";

        int repeats = Int(config["Schedule:Repeats"],       DefaultRepeats);
        int offset  = Int(config["Schedule:OffsetSeconds"], DefaultOffsetSeconds);

        string resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        Console.WriteLine($"MowIT.RobotSimulator - dio C2 (raspored) -> {baseUrl}, robot '{robotId}'");
        Console.WriteLine($"{repeats} ponavljanja, zakazivanje T+{offset} s");
        Console.WriteLine($"Izlaz: {resultsDir}");
        if (rewritten)
            Console.WriteLine("napomena: localhost preslikan u 127.0.0.1 (vidi LoopbackUrl.cs)");
        Console.WriteLine();

        using var reader = NewClient(baseUrl, token);
        string versionUrl   = $"/robots/{Uri.EscapeDataString(robotId)}/schedules/version";
        string schedulesUrl = $"/robots/{Uri.EscapeDataString(robotId)}/schedules";
        string telemetryUrl = $"/robots/{Uri.EscapeDataString(robotId)}/telemetry";

        var control = new ThinRobotControlClient(baseUrl, robotId, token);

        if (!await PrepareRobotAsync(reader, telemetryUrl, control))
            return;

        var connection  = new ToggleableConnection { IsConnected = true };
        var scheduleRepo = new InMemoryScheduleRepository();
        var syncService  = new ThinScheduleSyncClient(baseUrl, robotId, token);
        var syncingRepo  = new SyncingScheduleRepository(scheduleRepo, syncService);
        var sendZone     = new SendZoneToRobotUseCase(
            new EmptyBoundaryRepository(), control, control,
            new MowingRoutePlanner(Array.Empty<IMowingStrategy>()));

        using var loggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information));

        var schedule = new MowingSchedule
        {
            Id              = ScheduleId,
            DurationMinutes = 10,
            IsActive        = true,
            ZoneName        = "C2-test",
            ZoneId          = null
        };

        using var scheduler = new MowingSchedulerService(
            syncingRepo, connection, sendZone, loggerFactory.CreateLogger<MowingSchedulerService>());

        var runs      = new List<RunRow>();
        var scenarios = new List<ScenarioRow>();

        for (int i = 0; i < repeats; i++)
        {
            Console.WriteLine($"-- ponavljanje {i + 1}/{repeats}");
            var run = await RunRepetitionAsync(
                i, offset, schedule, syncingRepo, reader, versionUrl, telemetryUrl, control);
            runs.Add(run);

            if (run.StartedUtc is null)
                Console.WriteLine("   ! robot nije usao u stanje kosnje unutar granice - redak bez vrijednosti");
            else
                Console.WriteLine($"   sync {F((run.DetectedUtc!.Value - run.ScheduledUtc).TotalSeconds + offset, "0.00")} s, " +
                                  $"okidanje {F((run.StartedUtc.Value - run.ScheduledUtc).TotalSeconds, "0.00")} s");
        }

        scenarios.Add(CheckS1(runs, offset));
        scenarios.Add(await CheckS2Async(offset, schedule, syncingRepo, scheduleRepo, reader, telemetryUrl, control));
        scenarios.Add(await CheckS3Async(robotId, control));
        scenarios.Add(await CheckS4Async(offset, schedule, syncingRepo, scheduleRepo, connection, reader, telemetryUrl, control));
        scenarios.Add(await CheckS5Async(schedule, syncingRepo, reader, schedulesUrl));

        scenarios.Add(await CheckS6Async(loggerFactory));
        scenarios.Add(await CheckS7Async(loggerFactory));
        scenarios.Add(await CheckS8Async(loggerFactory));

        WriteRuns(Path.Combine(resultsDir, "schedule.csv"), runs, offset);
        WriteScenarios(Path.Combine(resultsDir, "schedule_scenarios.csv"), scenarios);

        RunInfo.Write(resultsDir, "schedule-test", new Dictionary<string, string>
        {
            ["base_url"]       = baseUrl,
            ["robot_id"]       = robotId,
            ["repeats"]        = repeats.ToString(CultureInfo.InvariantCulture),
            ["offset_seconds"] = offset.ToString(CultureInfo.InvariantCulture),
            ["poll_interval_ms"] = PollIntervalMs.ToString(CultureInfo.InvariantCulture),
        });

        Console.WriteLine();
        foreach (var s in scenarios)
            Console.WriteLine($"{s.Id}  {(s.Pass ? "PROLAZ" : "PAD   ")}  {s.Actual}");

        Console.WriteLine();
        Console.WriteLine($"schedule.csv ({runs.Count} redaka) i schedule_scenarios.csv zapisani");
    }

    private static async Task<bool> PrepareRobotAsync(
        HttpClient reader, string telemetryUrl, ThinRobotControlClient control)
    {
        var envelope = await TryGetAsync<TelemetryEnvelope>(reader, telemetryUrl);
        if (envelope?.IsOnline != true)
        {
            Console.WriteLine("! robot nije na vezi - pokrenuti MowIT.ScheduleServer i " +
                              "MowIT.RobotSimulator u normalnom nacinu rada prije ovog testa");
            return false;
        }

        var points = new GpsPointDto[PrimeRoutePoints];
        for (int i = 0; i < PrimeRoutePoints; i++)
            points[i] = new GpsPointDto { Lat = 43.8563 + i / MetersPerDegree, Lon = 18.4131 };

        await control.SendRawAsync(new RobotCommandDto
        {
            Kind     = CommandKinds.Boundary,
            Boundary = new BoundaryUploadDto { Name = "C2-priprema", Points = points }
        });

        await Task.Delay(2000);
        Console.WriteLine($"priprema: robotu poslana ruta od {PrimeRoutePoints} tocaka");
        return true;
    }

    private static async Task<RunRow> RunRepetitionAsync(
        int index, int offsetSeconds, MowingSchedule schedule, IScheduleRepository repo,
        HttpClient reader, string versionUrl, string telemetryUrl, ThinRobotControlClient control)
    {
        await EnsureIdleAsync(reader, telemetryUrl, control);

        long baseline = await GetVersionAsync(reader, versionUrl);
        var  scheduled = DateTime.UtcNow.AddSeconds(offsetSeconds);
        ApplySchedule(schedule, scheduled);

        var pushedAt = DateTime.UtcNow;
        await repo.SaveAsync(schedule);

        var detected = await WaitForVersionChangeAsync(
            reader, versionUrl, baseline, pushedAt.AddSeconds(SyncCeilingSec));

        var started = await WaitForMowingAsync(
            reader, telemetryUrl, scheduled.AddSeconds(TriggerCeilingSec));

        if (started is not null)
            await control.SendActionAsync(RobotAction.Stop);

        return new RunRow(index, scheduled, detected, started);
    }

    private static void ApplySchedule(MowingSchedule schedule, DateTime scheduledUtc)
    {
        var local = scheduledUtc.ToLocalTime();
        schedule.ActiveDays   = [local.DayOfWeek];
        schedule.StartTime    = local.TimeOfDay;
        schedule.IsActive     = true;
        schedule.LastExecuted = default;
    }

    private static ScenarioRow CheckS1(IReadOnlyList<RunRow> runs, int offsetSeconds)
    {
        var last = runs.LastOrDefault(r => r.StartedUtc is not null);
        if (last is null)
            return new ScenarioRow("S1", "Raspored se okine u zakazano vrijeme",
                "kasnjenje okidanja 0-25 s", "nijedno ponavljanje nije okinulo", false);

        double delay = (last.StartedUtc!.Value - last.ScheduledUtc).TotalSeconds;
        bool pass = delay is >= 0 and <= 25;
        return new ScenarioRow("S1", "Raspored se okine u zakazano vrijeme",
            "kasnjenje okidanja 0-25 s",
            $"zadnje uspjesno ponavljanje: {F(delay, "0.00")} s", pass);
    }

    private static async Task<ScenarioRow> CheckS2Async(
        int offsetSeconds, MowingSchedule schedule, IScheduleRepository repo,
        InMemoryScheduleRepository store, HttpClient reader, string telemetryUrl,
        ThinRobotControlClient control)
    {
        Console.WriteLine("-- scenarij S2 (dvostruko okidanje)");
        await EnsureIdleAsync(reader, telemetryUrl, control);
        store.ResetExecutedSaveCount();

        var scheduled = DateTime.UtcNow.AddSeconds(offsetSeconds);
        ApplySchedule(schedule, scheduled);
        await repo.SaveAsync(schedule);

        var started = await WaitForMowingAsync(reader, telemetryUrl, scheduled.AddSeconds(TriggerCeilingSec));
        if (started is null)
        {
            await control.SendActionAsync(RobotAction.Stop);
            return new ScenarioRow("S2", "Raspored se ne okine dvaput za isti termin",
                "tocno jedno okidanje po terminu", "robot nije okinuo ni prvi put", false);
        }

        int transitions = await CountMowingTransitionsAsync(
            reader, telemetryUrl, DateTime.UtcNow.AddSeconds(DoubleTriggerWatchSec));

        await control.SendActionAsync(RobotAction.Stop);

        int executedSaves = store.ExecutedSaveCount;
        bool pass = transitions == 0 && executedSaves == 1;
        return new ScenarioRow("S2", "Raspored se ne okine dvaput za isti termin",
            "0 dodatnih prijelaza u kosnju i LastExecuted zapisan tocno jednom",
            $"dodatnih prijelaza: {transitions}; LastExecuted zapisan {executedSaves} put(a) " +
            $"tijekom {DoubleTriggerWatchSec} s unutar istog prozora", pass);
    }

    private static async Task<ScenarioRow> CheckS3Async(string robotId, ThinRobotControlClient live)
    {
        Console.WriteLine("-- scenarij S3 (posluzitelj nedostupan)");
        const string deadUrl = "http://127.0.0.1:59999";
        const string desc = "Posluzitelj nedostupan -> robot nastavlja, ne rusi se " +
                            "(mjeritelj ne moze ugasiti posluzitelj u drugom terminalu, " +
                            "pa se klijenti usmjeravaju na zatvoreni port - provjerava se " +
                            "obrazac try/catch iz WifiRobotService.PostCommand)";

        try
        {
            var deadControl = new ThinRobotControlClient(deadUrl, robotId, "irrelevant");
            await deadControl.SendActionAsync(RobotAction.Stop);

            var deadSync = new ThinScheduleSyncClient(deadUrl, robotId, "irrelevant");
            await deadSync.PushAsync(Array.Empty<MowingSchedule>());

            await live.SendActionAsync(RobotAction.Stop);
        }
        catch (Exception ex)
        {
            return new ScenarioRow("S3", desc, "iznimka se ne propagira, proces nastavlja",
                $"iznimka je pobjegla: {ex.GetType().Name}", false);
        }

        return new ScenarioRow("S3", desc, "iznimka se ne propagira, proces nastavlja",
            "poziv prema zatvorenom portu uhvacen, sljedeci poziv prema zivom posluzitelju uspio", true);
    }

    private static async Task<ScenarioRow> CheckS4Async(
        int offsetSeconds, MowingSchedule schedule, IScheduleRepository repo,
        InMemoryScheduleRepository store, ToggleableConnection connection,
        HttpClient reader, string telemetryUrl, ThinRobotControlClient control)
    {
        Console.WriteLine("-- scenarij S4 (robot offline u trenutku okidanja)");
        await EnsureIdleAsync(reader, telemetryUrl, control);
        store.ResetExecutedSaveCount();
        connection.IsConnected = false;

        var scheduled = DateTime.UtcNow.AddSeconds(offsetSeconds);
        ApplySchedule(schedule, scheduled);
        await repo.SaveAsync(schedule);

        int transitions = await CountMowingTransitionsAsync(
            reader, telemetryUrl, scheduled.AddSeconds(TriggerCeilingSec));

        connection.IsConnected = true;

        var stored = store.Snapshot().FirstOrDefault(s => s.Id == ScheduleId);
        bool pass = stored is not null && stored.LastExecuted == default
                    && transitions == 0 && store.ExecutedSaveCount == 0;

        schedule.IsActive = false;
        await repo.SaveAsync(schedule);

        return new ScenarioRow("S4", "Robot offline u trenutku okidanja -> raspored se ne izgubi trajno",
            "raspored ostaje pohranjen, LastExecuted nepromijenjen, bez kosnje",
            stored is null
                ? "raspored je nestao iz repozitorija"
                : $"raspored prisutan, LastExecuted={(stored.LastExecuted == default ? "nepostavljen" : "postavljen")}, " +
                  $"prijelaza u kosnju: {transitions}", pass);
    }

    private static async Task<ScenarioRow> CheckS5Async(
        MowingSchedule schedule, IScheduleRepository repo, HttpClient reader, string schedulesUrl)
    {
        Console.WriteLine("-- scenarij S5 (izmjena rasporeda)");
        var newStart = DateTime.Now.AddHours(6).TimeOfDay;
        schedule.StartTime = newStart;
        schedule.IsActive  = false;
        await repo.SaveAsync(schedule);

        const string desc = "Promjena rasporeda u aplikaciji stigne do robota " +
                            "(provjereno do posluzitelja; preuzimanje na robotu pokriva sync_delay_s)";

        var deadline = DateTime.UtcNow.AddSeconds(SyncCeilingSec);
        while (DateTime.UtcNow < deadline)
        {
            var list = await TryGetAsync<ScheduleListResponse>(reader, schedulesUrl);
            var dto  = list?.Schedules.FirstOrDefault(s => s.Id == ScheduleId);
            if (dto is not null && dto.StartTimeTicks == newStart.Ticks && !dto.IsActive)
                return new ScenarioRow("S5", desc,
                    $"posluzitelj vraca StartTimeTicks={newStart.Ticks} i IsActive=false",
                    "posluzitelj odrazava novu vrijednost", true);

            await Task.Delay(PollIntervalMs);
        }

        return new ScenarioRow("S5", desc,
            $"posluzitelj vraca StartTimeTicks={newStart.Ticks} i IsActive=false",
            $"nova vrijednost nije vidljiva ni nakon {SyncCeilingSec} s", false);
    }

    private static async Task<ScenarioRow> CheckS6Async(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("-- scenarij S6 (ispravna zona)");
        var rig = NewOfflineRig(loggerFactory);
        try
        {
            var expectedA = rig.Planner.Plan(rig.ZoneA);
            var expectedB = rig.Planner.Plan(rig.ZoneB);

            var nowUtc   = DateTime.UtcNow;
            var triggerA = nowUtc.AddSeconds(DefaultOffsetSeconds);
            var triggerB = nowUtc.AddSeconds(DefaultOffsetSeconds * 2);

            int baseline = rig.Recorder.SnapshotRouteCallCount();
            await rig.ScheduleStore.SaveAsync(NewOfflineSchedule(201, 101, "S6-zonaA", triggerA));
            await rig.ScheduleStore.SaveAsync(NewOfflineSchedule(202, 102, "S6-zonaB", triggerB));

            var routeA = await WaitForNextRouteAsync(
                rig.Recorder, baseline, triggerA.AddSeconds(TriggerCeilingSec));
            bool aMatches = RoutesEqual(routeA, expectedA);

            int baselineB = rig.Recorder.SnapshotRouteCallCount();
            var routeB = await WaitForNextRouteAsync(
                rig.Recorder, baselineB, triggerB.AddSeconds(TriggerCeilingSec));
            bool bMatches = RoutesEqual(routeB, expectedB);

            bool pass = aMatches && bMatches;
            return new ScenarioRow("S6", "Pri okidanju se salje ruta ispravne zone iz rasporeda",
                $"1. okidanje = ruta zone A (id 101, {expectedA.Count} t.); " +
                $"2. okidanje = ruta zone B (id 102, {expectedB.Count} t.)",
                $"1. okidanje: {(routeA is null ? "nije okinulo" : $"{routeA.Count} t., podudara={aMatches}")}; " +
                $"2. okidanje: {(routeB is null ? "nije okinulo" : $"{routeB.Count} t., podudara={bMatches}")}",
                pass);
        }
        finally
        {
            rig.Scheduler.Dispose();
        }
    }

    private static async Task<ScenarioRow> CheckS7Async(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("-- scenarij S7 (maska dana)");
        var rig = NewOfflineRig(loggerFactory);
        try
        {
            var triggerLocal = DateTime.Now.AddSeconds(DefaultOffsetSeconds);
            var today    = triggerLocal.DayOfWeek;
            var tomorrow = (DayOfWeek)(((int)today + 1) % 7);

            var scheduleToday = new MowingSchedule
            {
                Id = 301, DurationMinutes = 10, IsActive = true,
                ZoneName = "S7-danas", ZoneId = 101,
                ActiveDays = [today], StartTime = triggerLocal.TimeOfDay, LastExecuted = default
            };
            var scheduleTomorrow = new MowingSchedule
            {
                Id = 302, DurationMinutes = 10, IsActive = true,
                ZoneName = "S7-sutra", ZoneId = 101,
                ActiveDays = [tomorrow], StartTime = triggerLocal.TimeOfDay, LastExecuted = default
            };

            await rig.ScheduleStore.SaveAsync(scheduleToday);
            await rig.ScheduleStore.SaveAsync(scheduleTomorrow);

            var deadlineUtc = triggerLocal.ToUniversalTime().AddSeconds(TriggerCeilingSec);
            while (DateTime.UtcNow < deadlineUtc)
                await Task.Delay(PollIntervalMs);

            int calls = rig.Recorder.SnapshotRouteCallCount();
            bool pass = calls == 1;

            return new ScenarioRow("S7", "Okida se samo raspored ciji ActiveDays sadrzi danasnji dan",
                "tocno jedno okidanje (danasnji raspored); sutrasnji se ne okine",
                $"broj primljenih ruta u prozoru: {calls} (danas={today}, sutra={tomorrow})",
                pass);
        }
        finally
        {
            rig.Scheduler.Dispose();
        }
    }

    private static async Task<ScenarioRow> CheckS8Async(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("-- scenarij S8 (sadrzaj rute prema planeru)");
        var rig = NewOfflineRig(loggerFactory);
        try
        {
            var expected = rig.Planner.Plan(rig.ZoneA);

            var nowUtc   = DateTime.UtcNow;
            var trigger  = nowUtc.AddSeconds(DefaultOffsetSeconds);
            int baseline = rig.Recorder.SnapshotRouteCallCount();
            await rig.ScheduleStore.SaveAsync(NewOfflineSchedule(401, 101, "S8-zonaA", trigger));

            var route = await WaitForNextRouteAsync(
                rig.Recorder, baseline, trigger.AddSeconds(TriggerCeilingSec));
            bool pass = RoutesEqual(route, expected);

            return new ScenarioRow("S8", "Primljena ruta se tocka po tocku podudara s planner.Plan(zone)",
                $"{expected.Count} tocaka, jednak redoslijed, odstupanje ispod {GpsEpsilonDeg:0.0e+00} stupnjeva",
                route is null
                    ? "raspored nije okinuo"
                    : $"primljeno {route.Count} t. naspram {expected.Count} ocekivanih; podudara={pass}",
                pass);
        }
        finally
        {
            rig.Scheduler.Dispose();
        }
    }

    private static OfflineRig NewOfflineRig(ILoggerFactory loggerFactory)
    {
        var proj  = new LocalProjection(TestPolygons.Origin);
        var zoneA = MakeZone(101, "S6-zonaA", proj, (0, 0), (8, 0), (8, 8), (0, 8));
        var zoneB = MakeZone(102, "S6-zonaB", proj, (100, 100), (115, 100), (115, 115), (100, 115));

        var recorder  = new RecordingRobotBoundary();
        var zonesRepo = new FixedZonesRepository(new[] { zoneA, zoneB });
        var planner   = new MowingRoutePlanner(new IMowingStrategy[] { new BoustrophedonStrategy() });
        var sendZone  = new SendZoneToRobotUseCase(zonesRepo, recorder, recorder, planner);

        var scheduleStore = new InMemoryScheduleRepository();
        var connection    = new ToggleableConnection { IsConnected = true };

        var scheduler = new MowingSchedulerService(
            scheduleStore, connection, sendZone,
            loggerFactory.CreateLogger<MowingSchedulerService>());

        return new OfflineRig(recorder, scheduleStore, scheduler, planner, zoneA, zoneB);
    }

    private static BoundaryZone MakeZone(
        int id, string name, LocalProjection proj, params (double East, double North)[] local) => new()
    {
        Id        = id,
        Name      = name,
        CreatedAt = DateTime.UtcNow,
        Points    = local.Select(p => proj.ToGps(p.East, p.North)).ToList()
    };

    private static MowingSchedule NewOfflineSchedule(int id, int zoneId, string zoneName, DateTime triggerUtc)
    {
        var local = triggerUtc.ToLocalTime();
        return new MowingSchedule
        {
            Id              = id,
            DurationMinutes = 10,
            IsActive        = true,
            ZoneName        = zoneName,
            ZoneId          = zoneId,
            ActiveDays      = [local.DayOfWeek],
            StartTime       = local.TimeOfDay,
            LastExecuted    = default
        };
    }

    private static async Task<List<GpsPoint>?> WaitForNextRouteAsync(
        RecordingRobotBoundary recorder, int baselineCallCount, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            if (recorder.SnapshotRouteCallCount() > baselineCallCount)
                return recorder.SnapshotRoute();
            await Task.Delay(PollIntervalMs);
        }
        return null;
    }

    private static bool RoutesEqual(List<GpsPoint>? actual, List<GpsPoint> expected)
    {
        if (actual is null || actual.Count != expected.Count) return false;
        for (int i = 0; i < actual.Count; i++)
        {
            if (Math.Abs(actual[i].Latitude  - expected[i].Latitude)  > GpsEpsilonDeg) return false;
            if (Math.Abs(actual[i].Longitude - expected[i].Longitude) > GpsEpsilonDeg) return false;
        }
        return true;
    }

    private static async Task EnsureIdleAsync(
        HttpClient reader, string telemetryUrl, ThinRobotControlClient control)
    {
        await control.SendActionAsync(RobotAction.Stop);

        var deadline = DateTime.UtcNow.AddSeconds(IdleCeilingSec);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs);
            if (await GetStateAsync(reader, telemetryUrl) != MowingState) return;
        }
        Console.WriteLine("   ! robot se nije vratio u mirovanje unutar granice");
    }

    private static async Task<DateTime?> WaitForMowingAsync(
        HttpClient reader, string telemetryUrl, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs);
            if (await GetStateAsync(reader, telemetryUrl) == MowingState) return DateTime.UtcNow;
        }
        return null;
    }

    private static async Task<int> CountMowingTransitionsAsync(
        HttpClient reader, string telemetryUrl, DateTime deadline)
    {
        int transitions = 0;
        int previous = await GetStateAsync(reader, telemetryUrl);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs);
            int current = await GetStateAsync(reader, telemetryUrl);
            if (current == MowingState && previous != MowingState) transitions++;
            previous = current;
        }
        return transitions;
    }

    private static async Task<DateTime?> WaitForVersionChangeAsync(
        HttpClient reader, string versionUrl, long baseline, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            if (await GetVersionAsync(reader, versionUrl) != baseline) return DateTime.UtcNow;
            await Task.Delay(PollIntervalMs);
        }
        return null;
    }

    private static async Task<long> GetVersionAsync(HttpClient reader, string versionUrl)
        => (await TryGetAsync<ScheduleVersionResponse>(reader, versionUrl))?.Version ?? -1;

    private static async Task<int> GetStateAsync(HttpClient reader, string telemetryUrl)
        => (await TryGetAsync<TelemetryEnvelope>(reader, telemetryUrl))?.Latest?.State ?? -1;

    private static async Task<T?> TryGetAsync<T>(HttpClient reader, string url) where T : class
    {
        try { return await reader.GetFromJsonAsync<T>(url); }
        catch { return null; }
    }

    private static HttpClient NewClient(string baseUrl, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void WriteRuns(string path, IEnumerable<RunRow> runs, int offsetSeconds)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("run_index,scheduled_utc,detected_utc,started_utc,sync_delay_s,trigger_delay_s");

        foreach (var r in runs)
        {
            var pushedAt = r.ScheduledUtc.AddSeconds(-offsetSeconds);

            writer.WriteLine(string.Join(',',
                r.RunIndex.ToString(CultureInfo.InvariantCulture),
                Iso(r.ScheduledUtc),
                r.DetectedUtc is null ? "" : Iso(r.DetectedUtc.Value),
                r.StartedUtc  is null ? "" : Iso(r.StartedUtc.Value),
                r.DetectedUtc is null ? "" : F((r.DetectedUtc.Value - pushedAt).TotalSeconds, "0.###"),
                r.StartedUtc  is null ? "" : F((r.StartedUtc.Value - r.ScheduledUtc).TotalSeconds, "0.###")));
        }
    }

    private static void WriteScenarios(string path, IEnumerable<ScenarioRow> scenarios)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("scenario_id,description,expected,actual,pass");
        foreach (var s in scenarios)
            writer.WriteLine(string.Join(',',
                s.Id, Csv(s.Description), Csv(s.Expected), Csv(s.Actual), s.Pass ? "1" : "0"));
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string Iso(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static int Int(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}

internal sealed class RecordingRobotBoundary : IRobotBoundary, IRobotControl
{
    private readonly object _gate = new();
    private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();

    private List<GpsPoint>? _lastRoute;
    private BoundaryZone?   _lastBoundary;
    private int             _routeCallCount;

    public List<(double ElapsedS, RobotAction Action)> Calls { get; } = new();

    public Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
    {
        lock (_gate) _lastBoundary = zone;
        return Task.CompletedTask;
    }

    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
    {
        lock (_gate)
        {
            _lastRoute = route;
            _routeCallCount++;
        }
        return Task.CompletedTask;
    }

    public Task ClearBoundaryAsync() => Task.CompletedTask;

    public Task SendMotorCommandAsync(float linearVel, float angularVel) => Task.CompletedTask;

    public Task SendActionAsync(RobotAction action, byte param = 0)
    {
        lock (_gate) Calls.Add((_elapsed.Elapsed.TotalSeconds, action));
        return Task.CompletedTask;
    }

    public List<GpsPoint>? SnapshotRoute()
    {
        lock (_gate) return _lastRoute is null ? null : new List<GpsPoint>(_lastRoute);
    }

    public BoundaryZone? SnapshotBoundary()
    {
        lock (_gate) return _lastBoundary;
    }

    public int SnapshotRouteCallCount()
    {
        lock (_gate) return _routeCallCount;
    }
}

internal sealed class FixedZonesRepository : IBoundaryRepository
{
    private readonly Dictionary<int, BoundaryZone> _zones;

    public FixedZonesRepository(IEnumerable<BoundaryZone> zones)
        => _zones = zones.ToDictionary(z => z.Id);

    public Task<List<BoundaryZone>> GetAllAsync() => Task.FromResult(_zones.Values.ToList());

    public Task<BoundaryZone?> GetByIdAsync(int id)
        => Task.FromResult(_zones.TryGetValue(id, out var zone) ? zone : null);

    public Task SaveAsync(BoundaryZone zone) => Task.CompletedTask;
    public Task DeleteAsync(int id) => Task.CompletedTask;
}
