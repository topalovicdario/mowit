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
using MowIT.Infrastructure.Services;
using MowIT.Presentation.Pages;
using MowIT.Presentation.ViewModels;

namespace MowIT;

public static partial class MauiProgram
{

    // Toggle between the real GreenTitan over Bluetooth SPP and the local simulator.
    // The simulator emits the same messages as the firmware (BaseCaptured, BoundaryPointCaptured,
    // OutlineCaptured, ExitCaptured, CaptureEnd with FailReason, RobotErrorMessage) and runs the
    // commands through the same CommandPipeline + state-machine, so flipping this line lets you
    // dry-run the full UI flow before flashing the ESP32.
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

        return app;
    }

private static void RegisterInfrastructure(IServiceCollection services)
    {
        
        RegisterPlatformPermissions(services);

if (RobotFactory is SimulatorServiceFactory)
            services.AddSingleton<IBlePermissionService, NullBlePermissionService>();

var dbPath = Path.Combine(
            FileSystem.AppDataDirectory, "mowit.db3");

        services.AddSingleton(new AppDatabase(dbPath));
        services.AddSingleton<IBoundaryRepository, BoundaryRepository>();
        services.AddSingleton<IScheduleRepository, ScheduleRepository>();

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

services.AddSingleton<MowingSchedulerService>();

services.AddSingleton<IMessenger>(_ => WeakReferenceMessenger.Default);

services.AddSingleton<MowIT.Application.Logging.EventLogService>();

services.AddTransient<IMowingStrategy, BoustrophedonStrategy>();
        services.AddTransient<IMowingStrategy, SpiralInwardStrategy>();
    }

private static void RegisterPresentation(IServiceCollection services)
    {
        
        services.AddTransient<LoginPage>();
        services.AddTransient<ScanPage>();
        services.AddTransient<DashboardPage>();
        services.AddTransient<ControlPage>();
        services.AddTransient<MapPage>();
        services.AddTransient<SchedulePage>();

services.AddTransient<LoginViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ControlViewModel>();
        services.AddTransient<MapViewModel>();
        services.AddTransient<ScheduleViewModel>();
    }

static partial void RegisterPlatformPermissions(IServiceCollection services);
}
