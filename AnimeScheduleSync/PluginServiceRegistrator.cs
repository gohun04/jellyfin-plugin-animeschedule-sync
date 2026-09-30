using Jellyfin.Plugin.AnimeScheduleSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AnimeScheduleSync;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AnimeScheduleRuntimeState>();
        serviceCollection.AddSingleton<AnimeScheduleClient>();
        serviceCollection.AddSingleton<AnimeScheduleManagementService>();
        serviceCollection.AddSingleton<AnimeScheduleRetryService>();

        serviceCollection.AddHostedService<AnimeScheduleSyncService>();
        serviceCollection.AddHostedService(
            provider => provider.GetRequiredService<AnimeScheduleRetryService>());
    }
}
