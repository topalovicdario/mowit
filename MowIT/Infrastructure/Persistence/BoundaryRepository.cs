using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Persistence;

public sealed class BoundaryRepository : IBoundaryRepository
{
    private readonly AppDatabase _db;

    public BoundaryRepository(AppDatabase db) => _db = db;

    public async Task<List<BoundaryZone>> GetAllAsync()
    {
        await _db.InitializeAsync();
        var zones  = await _db.Connection.Table<BoundaryZoneEntity>().ToListAsync();
        var points = await _db.Connection.Table<BoundaryPointEntity>().ToListAsync();

        return zones.Select(z => new BoundaryZone
        {
            Id        = z.Id,
            Name      = z.Name,
            CreatedAt = z.CreatedAt,
            Points    = points
                .Where(p => p.ZoneId == z.Id)
                .OrderBy(p => p.Order)
                .Select(p => new GpsPoint(p.Latitude, p.Longitude))
                .ToList()
        }).ToList();
    }

    public async Task<BoundaryZone?> GetByIdAsync(int id)
    {
        await _db.InitializeAsync();
        var entity = await _db.Connection.FindAsync<BoundaryZoneEntity>(id);
        if (entity is null) return null;

        var points = await _db.Connection
            .Table<BoundaryPointEntity>()
            .Where(p => p.ZoneId == id)
            .OrderBy(p => p.Order)
            .ToListAsync();

        return new BoundaryZone
        {
            Id        = entity.Id,
            Name      = entity.Name,
            CreatedAt = entity.CreatedAt,
            Points    = points.Select(p => new GpsPoint(p.Latitude, p.Longitude)).ToList()
        };
    }

    public async Task SaveAsync(BoundaryZone zone)
    {
        await _db.InitializeAsync();
        var entity = new BoundaryZoneEntity
        {
            Id        = zone.Id,
            Name      = zone.Name,
            CreatedAt = zone.CreatedAt == default ? DateTime.UtcNow : zone.CreatedAt
        };

        if (zone.Id == 0)
        {
            await _db.Connection.InsertAsync(entity);
            zone.Id = entity.Id;
        }
        else
        {
            await _db.Connection.UpdateAsync(entity);
        }

        await _db.Connection.ExecuteAsync(
            "DELETE FROM BoundaryPoints WHERE ZoneId = ?", zone.Id);

        for (int i = 0; i < zone.Points.Count; i++)
            await _db.Connection.InsertAsync(new BoundaryPointEntity
            {
                ZoneId    = zone.Id,
                Order     = i,
                Latitude  = zone.Points[i].Latitude,
                Longitude = zone.Points[i].Longitude
            });
    }

    public async Task DeleteAsync(int id)
    {
        await _db.InitializeAsync();
        await _db.Connection.ExecuteAsync("DELETE FROM BoundaryPoints WHERE ZoneId = ?", id);
        await _db.Connection.DeleteAsync<BoundaryZoneEntity>(id);
    }
}
