using SQLite;

namespace MowIT.Infrastructure.Persistence;

public sealed class AppDatabase
{
    private SQLiteAsyncConnection _db;
    private readonly string _dbPath;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public AppDatabase(string dbPath)
    {
        _dbPath = dbPath;

if (File.Exists(dbPath) && !HasValidSQLiteHeader(dbPath))
            File.Delete(dbPath);

        _db = new SQLiteAsyncConnection(dbPath);
    }

private static bool HasValidSQLiteHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Read(header) == 16 &&
                   header.SequenceEqual("SQLite format 3\0"u8);
        }
        catch { return false; }
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;          

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;      
            try
            {
                await CreateTablesAsync();
            }
            catch (SQLiteException)
            {
                
                await _db.CloseAsync();
                if (File.Exists(_dbPath))
                    File.Delete(_dbPath);
                _db = new SQLiteAsyncConnection(_dbPath);
                await CreateTablesAsync();
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task CreateTablesAsync()
    {
        await _db.CreateTableAsync<BoundaryZoneEntity>();
        await _db.CreateTableAsync<BoundaryPointEntity>();
        await _db.CreateTableAsync<MowingScheduleEntity>();
        
        try { await _db.ExecuteAsync("ALTER TABLE MowingSchedules ADD COLUMN ZoneId INTEGER DEFAULT 0"); }
        catch {  }
    }

    public SQLiteAsyncConnection Connection => _db;
}
