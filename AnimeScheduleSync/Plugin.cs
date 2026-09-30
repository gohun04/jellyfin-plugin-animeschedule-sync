using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.AnimeScheduleSync.Configuration;

namespace Jellyfin.Plugin.AnimeScheduleSync;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        if (Configuration.MigrateConnections()) SaveConfiguration();
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        Services.AnimeScheduleClient.ConnectionGate.Wait();
        try
        {
            var incoming = (PluginConfiguration)configuration;
            // Dashboard settings saves must not overwrite newer OAuth tokens or assignments.
            incoming.ServerConnection = Configuration.ServerConnection;
            incoming.UserConnections = Configuration.UserConnections;
            incoming.ConnectionSchemaVersion = Configuration.ConnectionSchemaVersion;
            incoming.AllowUsersToChangeSyncMode = Configuration.AllowUsersToChangeSyncMode;
            incoming.AccessToken = incoming.RefreshToken = incoming.PendingOAuthState = incoming.PendingPkceVerifier = string.Empty;
            incoming.AccessTokenExpiresUtc = DateTime.MinValue;
            base.UpdateConfiguration(incoming);
        }
        finally { Services.AnimeScheduleClient.ConnectionGate.Release(); }
    }

    public override string Name => "AnimeSchedule Sync";

    public override Guid Id => Guid.Parse("e3fe9af5-7c4f-4e7f-9f4e-0e1a2d84f8a9");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "AnimeScheduleConnections",
            DisplayName = "AnimeSchedule Connections",
            MenuIcon = "people",
            EnableInMainMenu = true,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.connections.html"
        };
        yield return new PluginPageInfo
        {
            Name = "AnimeScheduleSync",
            DisplayName = "AnimeSchedule Sync Settings",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
        };

        yield return new PluginPageInfo
        {
            Name = "AnimeScheduleManager",
            DisplayName = "AnimeSchedule Sync",
            MenuIcon = "sync",
            EnableInMainMenu = true,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.management.html"
        };
    }
}
