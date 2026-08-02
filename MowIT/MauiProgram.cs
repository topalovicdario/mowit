using CommunityToolkit.Maui;
using SkiaSharp.Views.Maui.Controls.Hosting;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Services;
using MowIT.Application.UseCases;
using MowIT.Domain.Interfaces;
using MowIT.Domain.Strategies;
using MowIT.Infrastructure;
using MowIT.Infrastructure.Factories;
using MowIT.Infrastructure.Persistence;
using MowIT.Infrastructure.ScheduleSync;
using MowIT.Infrastructure.Services;
using MowIT.Infrastructure.Transport;
using MowIT.Infrastructure.Wifi;
using MowIT.Presentation.Pages;
using MowIT.Presentation.ViewModels;

namespace MowIT;

public static partial class MauiProgram
{

   
    private static readonly IRobotServiceFactory RobotFactory =
             new GreenTitanSppFactory();

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Pixie.ttf",             "Pixie");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        RegisterInfrastructure(builder.Services);
        RegisterApplication(builder.Services);
        RegisterPresentation(builder.Services);

        var app = builder.Build();

app.Services.GetRequiredService<MowingSchedulerService>();
        app.Services.GetRequiredService<GeofenceMonitor>();
        app.Services.GetRequiredService<GpsTraceService>();

        return app;
    }

private static void RegisterInfrastructure(IServiceCollection services)
    {
        
        RegisterPlatformPermissions(services);

if (RobotFactory is SimulatorServiceFactory or MultiTransportFactory)
            services.AddSingleton<IBlePermissionService, NullBlePermissionService>();

var dbPath = Path.Combine(
            FileSystem.AppDataDirectory, "mowit.db3");

        services.AddSingleton(new AppDatabase(dbPath));
        services.AddSingleton<IBoundaryRepository, BoundaryRepository>();

        services.AddSingleton<ScheduleRepository>();

        if (RobotFactory is MultiTransportFactory)
        {
            var wifi = new WifiRobotOptions();
            var syncOpts = new ScheduleSyncOptions
            {
                BaseUrl     = wifi.BaseUrl,
                RobotId     = wifi.RobotId,
                BearerToken = wifi.BearerToken
            };
            services.AddSingleton<IScheduleSyncService>(sp =>
                new HttpScheduleSyncService(
                    new HttpClient(),
                    Microsoft.Extensions.Options.Options.Create(syncOpts),
                    sp.GetRequiredService<ILogger<HttpScheduleSyncService>>(),
                    sp.GetRequiredService<MowIT.Application.Logging.EventLogService>()));
        }
        else
        {
            services.AddSingleton<IScheduleSyncService, NullScheduleSyncService>();
        }

        services.AddSingleton<IScheduleRepository>(sp =>
            new SyncingScheduleRepository(
                sp.GetRequiredService<ScheduleRepository>(),
                sp.GetRequiredService<IScheduleSyncService>()));

services.AddSingleton(new WifiRobotOptions());
        services.AddSingleton<IRobotTransportSwitch, NullRobotTransportSwitch>();

        services.AddSingleton(RobotFactory);
        RobotFactory.RegisterServices(services);
    }

private static void RegisterApplication(IServiceCollection services)
    {
        services.AddTransient<SendBoundaryUseCase>();
        services.AddTransient<SaveScheduleUseCase>();
        services.AddTransient<ConnectToMowerUseCase>();
        services.AddTransient<SendZoneToRobotUseCase>();

services.AddSingleton<GeofenceMonitor>();

        services.AddSingleton<GpsTraceService>();

services.AddSingleton<MowingSchedulerService>();

services.AddSingleton<IMessenger>(_ => WeakReferenceMessenger.Default);

services.AddSingleton<MowIT.Application.Logging.EventLogService>();

        services.AddSingleton<MowIT.Application.Services.LastMowSession>();

services.AddTransient<IMowingStrategy, BoustrophedonStrategy>();
        services.AddTransient<IMowingStrategy, SpiralInwardStrategy>();

        services.AddSingleton<MowIT.Application.Services.MowingRoutePlanner>();
    }

private static void RegisterPresentation(IServiceCollection services)
    {
        
        services.AddTransient<LoginPage>();
        services.AddTransient<ScanPage>();

        services.AddSingleton<DashboardPage>();
        services.AddSingleton<ControlPage>();
        services.AddSingleton<MapPage>();
        services.AddSingleton<SchedulePage>();

services.AddTransient<LoginViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ControlViewModel>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<ScheduleViewModel>();
    }

static partial void RegisterPlatformPermissions(IServiceCollection services);
}
