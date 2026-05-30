using MowIT.Domain.Interfaces;

namespace MowIT;

public static partial class MauiProgram
{
    static partial void RegisterPlatformPermissions(IServiceCollection services) =>
        services.AddSingleton<IBlePermissionService, IBlePermissionService>();
}
