using MowIT.Domain.Interfaces;
using MowIT.Platforms.Android;

namespace MowIT;

public static partial class MauiProgram
{
    static partial void RegisterPlatformPermissions(IServiceCollection services) =>
        services.AddSingleton<IBlePermissionService, AndroidBlePermissionService>();
}
