using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MowIT.Shared.Schedules;

namespace MowIT.ScheduleServer.Storage;

public interface IScheduleStore
{
    Task<ScheduleListResponse> GetAsync(string robotId, CancellationToken ct);
    Task<ScheduleVersionResponse> GetVersionAsync(string robotId, CancellationToken ct);
    Task<ScheduleListResponse> ReplaceAsync(string robotId, ScheduleDto[] schedules, CancellationToken ct);
    Task<bool> DeleteAsync(string robotId, int scheduleId, CancellationToken ct);
}

public sealed class SqliteScheduleStore : IScheduleStore, IAsyncDisposable
{
    private readonly string _connString;

    public SqliteScheduleStore(string connString)
    {
        _connString = connString;
        InitSchema();
    }

    private IDbConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private void InitSchema()
    {
        using var c = Open();
        c.Execute(@"
            CREATE TABLE IF NOT EXISTS Robots (
                RobotId    TEXT PRIMARY KEY,
                Version    INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc TEXT    NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS Schedules (
                RobotId      TEXT    NOT NULL,
                ScheduleId   INTEGER NOT NULL,
                PayloadJson  TEXT    NOT NULL,
                PRIMARY KEY (RobotId, ScheduleId),
                FOREIGN KEY (RobotId) REFERENCES Robots(RobotId) ON DELETE CASCADE
            );
        ");
    }

    public async Task<ScheduleListResponse> GetAsync(string robotId, CancellationToken ct)
    {
        using var c = Open();

        var version = await c.QueryFirstOrDefaultAsync<(long Version, string UpdatedUtc)?>(
            "SELECT Version, UpdatedUtc FROM Robots WHERE RobotId = @robotId",
            new { robotId });

        var rows = await c.QueryAsync<string>(
            "SELECT PayloadJson FROM Schedules WHERE RobotId = @robotId",
            new { robotId });

        var schedules = rows
            .Select(j => JsonSerializer.Deserialize<ScheduleDto>(j)!)
            .ToArray();

        return new ScheduleListResponse
        {
            RobotId    = robotId,
            Version    = version?.Version ?? 0,
            UpdatedUtc = ParseUtc(version?.UpdatedUtc),
            Schedules  = schedules
        };
    }

    public async Task<ScheduleVersionResponse> GetVersionAsync(string robotId, CancellationToken ct)
    {
        using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<(long Version, string UpdatedUtc)?>(
            "SELECT Version, UpdatedUtc FROM Robots WHERE RobotId = @robotId",
            new { robotId });

        return new ScheduleVersionResponse
        {
            RobotId    = robotId,
            Version    = row?.Version ?? 0,
            UpdatedUtc = ParseUtc(row?.UpdatedUtc)
        };
    }

    public async Task<ScheduleListResponse> ReplaceAsync(string robotId, ScheduleDto[] schedules, CancellationToken ct)
    {
        using var c = (SqliteConnection)Open();
        using var tx = c.BeginTransaction();

        await c.ExecuteAsync(@"
            INSERT INTO Robots (RobotId, Version, UpdatedUtc)
            VALUES (@robotId, 1, datetime('now'))
            ON CONFLICT (RobotId) DO UPDATE
                SET Version    = Version + 1,
                    UpdatedUtc = datetime('now');",
            new { robotId }, tx);

        await c.ExecuteAsync(
            "DELETE FROM Schedules WHERE RobotId = @robotId",
            new { robotId }, tx);

        foreach (var s in schedules)
        {
            await c.ExecuteAsync(@"
                INSERT INTO Schedules (RobotId, ScheduleId, PayloadJson)
                VALUES (@robotId, @scheduleId, @payload);",
                new
                {
                    robotId,
                    scheduleId = s.Id,
                    payload    = JsonSerializer.Serialize(s)
                }, tx);
        }

        tx.Commit();
        return await GetAsync(robotId, ct);
    }

    public async Task<bool> DeleteAsync(string robotId, int scheduleId, CancellationToken ct)
    {
        using var c = (SqliteConnection)Open();
        using var tx = c.BeginTransaction();

        var rows = await c.ExecuteAsync(
            "DELETE FROM Schedules WHERE RobotId = @robotId AND ScheduleId = @scheduleId",
            new { robotId, scheduleId }, tx);

        if (rows > 0)
        {
            await c.ExecuteAsync(@"
                UPDATE Robots
                SET Version    = Version + 1,
                    UpdatedUtc = datetime('now')
                WHERE RobotId = @robotId;",
                new { robotId }, tx);
        }

        tx.Commit();
        return rows > 0;
    }

    private static DateTime ParseUtc(string? raw)
        => DateTime.TryParse(raw, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : DateTime.MinValue;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
