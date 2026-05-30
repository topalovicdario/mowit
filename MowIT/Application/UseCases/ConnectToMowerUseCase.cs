using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.UseCases;

public class ConnectToMowerUseCase
{
    private readonly IRobotConnection _connection;

    public ConnectToMowerUseCase(IRobotConnection connection) => _connection = connection;

    public async Task<bool> ExecuteAsync(MowerDevice device, CancellationToken ct = default)
        => await _connection.ConnectAsync(device, ct);
}
