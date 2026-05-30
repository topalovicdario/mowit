using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IBoundaryRepository
{
    Task<List<BoundaryZone>> GetAllAsync();
    Task<BoundaryZone?> GetByIdAsync(int id);
    Task SaveAsync(BoundaryZone zone);
    Task DeleteAsync(int id);
}
