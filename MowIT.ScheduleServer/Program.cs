using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MowIT.ScheduleServer.Auth;
using MowIT.ScheduleServer.Storage;
using MowIT.Shared.Schedules;
using MowIT.Shared.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RobotTokenOptions>(
    builder.Configuration.GetSection("RobotTokens"));

var dbPath = builder.Configuration.GetValue<string>("Database:Path") ?? "schedules.db";
var connStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Mode       = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
    Cache      = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
}.ToString();

builder.Services.AddSingleton<IRobotTokenStore, InMemoryRobotTokenStore>();
builder.Services.AddSingleton<IScheduleStore>(_ => new SqliteScheduleStore(connStr));
builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();
builder.Services.AddSingleton<ICommandQueue, InMemoryCommandQueue>();
builder.Services.AddSingleton<IRobotEventLog, InMemoryRobotEventLog>();

builder.Services
    .AddAuthentication(RobotAuth.Scheme)
    .AddScheme<RobotTokenAuthOptions, RobotTokenAuthHandler>(RobotAuth.Scheme, _ => { });

builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/robots/active", (ITelemetryStore telemetry) =>
        Results.Ok(new ActiveRobotsResponse { Robots = telemetry.GetActive() }))
   .RequireAuthorization();

var robots = app.MapGroup("/robots/{robotId}")
                .RequireAuthorization();

robots.MapGet("/schedules", async (
        string robotId,
        IScheduleStore store,
        CancellationToken ct) =>
    Results.Ok(await store.GetAsync(robotId, ct)));

robots.MapGet("/schedules/version", async (
        string robotId,
        IScheduleStore store,
        CancellationToken ct) =>
    Results.Ok(await store.GetVersionAsync(robotId, ct)));

robots.MapPost("/schedules", async (
        string robotId,
        [FromBody] ScheduleUploadRequest req,
        IScheduleStore store,
        CancellationToken ct) =>
{
    var result = await store.ReplaceAsync(robotId, req.Schedules ?? Array.Empty<ScheduleDto>(), ct);
    return Results.Ok(result);
});

robots.MapDelete("/schedules/{scheduleId:int}", async (
        string robotId,
        int scheduleId,
        IScheduleStore store,
        CancellationToken ct) =>
{
    var ok = await store.DeleteAsync(robotId, scheduleId, ct);
    return ok ? Results.NoContent() : Results.NotFound();
});

robots.MapPost("/telemetry", (
        string robotId,
        [FromBody] TelemetryDto telemetry,
        ITelemetryStore store) =>
{
    store.Update(robotId, telemetry);
    return Results.NoContent();
});

robots.MapGet("/telemetry", (
        string robotId,
        ITelemetryStore store) =>
    Results.Ok(store.Get(robotId)));

robots.MapPost("/commands", (
        string robotId,
        [FromBody] RobotCommandDto command,
        ICommandQueue queue) =>
{
    queue.Enqueue(robotId, command);
    return Results.Accepted();
});

robots.MapGet("/commands", (
        string robotId,
        ICommandQueue queue) =>
    Results.Ok(new CommandListResponse { Commands = queue.Drain(robotId) }));

robots.MapPost("/events", (
        string robotId,
        [FromBody] RobotEventDto evt,
        IRobotEventLog log) =>
    Results.Ok(log.Append(robotId, evt)));

robots.MapGet("/events", (
        string robotId,
        long? since,
        IRobotEventLog log) =>
    Results.Ok(log.GetSince(robotId, since ?? 0)));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();

public partial class Program { }
