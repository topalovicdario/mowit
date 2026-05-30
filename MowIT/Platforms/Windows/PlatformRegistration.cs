using MowIT.Domain.Interfaces;
using MowIT.Infrastructure;

namespace MowIT;

public static partial class MauiProgram
{
    static partial void RegisterPlatformPermissions(IServiceCollection services) =>
        services.AddSingleton<IBlePermissionService, NullBlePermissionService>();
}
