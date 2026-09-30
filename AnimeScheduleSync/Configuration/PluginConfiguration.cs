using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeScheduleSync.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public int ConnectionSchemaVersion { get; set; }
    public bool AllowUsersToChangeSyncMode { get; set; }
    public AnimeConnection ServerConnection { get; set; } = new();
    public List<UserConnection> UserConnections { get; set; } = new();

    // Missing users are deliberately never enrolled automatically.
    public UserConnection? FindUser(Guid id) => UserConnections.FirstOrDefault(x => x.UserId == id);
    public AnimeConnection? ResolveConnection(Guid id) => FindUser(id) switch
    {
        { Mode: SyncMode.Personal } user => user.PersonalConnection,
        { Mode: SyncMode.ServerShared } => ServerConnection,
        _ => null
    };

    public string? SyncKey(Guid id)
    {
        var connection = ResolveConnection(id);
        return connection is null ? null : connection.Id + ":" + FindUser(id)!.Revision;
    }

    public bool MigrateConnections()
    {
        if (ConnectionSchemaVersion >= 1) return false;
        var legacy = new AnimeConnection
        {
            AccessToken = AccessToken, RefreshToken = RefreshToken,
            AccessTokenExpiresUtc = AccessTokenExpiresUtc
        };
        if (Guid.TryParse(JellyfinUserId, out var id) && id != Guid.Empty)
        {
            // Preserve the one explicitly enrolled user's account; never enroll others.
            if (FindUser(id) is null)
                UserConnections.Add(new UserConnection { UserId = id, Mode = SyncMode.Personal, PersonalConnection = legacy });
        }
        else if (!string.IsNullOrWhiteSpace(AccessToken) || !string.IsNullOrWhiteSpace(RefreshToken))
        {
            // Retain an unassigned legacy connection without giving anyone access.
            if (string.IsNullOrWhiteSpace(ServerConnection.AccessToken)) ServerConnection = legacy;
        }
        AccessToken = RefreshToken = PendingOAuthState = PendingPkceVerifier = string.Empty;
        AccessTokenExpiresUtc = DateTime.MinValue;
        ConnectionSchemaVersion = 1;
        return true;
    }

    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client secret generated when the AnimeSchedule application is created.
    /// This is distinct from the application Token used for non-OAuth API calls.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The Token shown on AnimeSchedule's application settings page.
    /// Used as the bearer token for non-OAuth API requests.
    /// </summary>
    public string ApplicationToken { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Legacy user ID retained only for migration. Runtime routing uses UserConnections.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    public bool AutoSync { get; set; } = true;

    public bool NeverDecreaseProgress { get; set; } = true;

    public bool SetWatchingStatus { get; set; } = true;

    public bool SetCompletedStatus { get; set; } = true;

    /// <summary>
    /// Prefer provider IDs attached to Jellyfin Season objects for season 2+.
    /// Anime databases commonly model each season/cour as a separate title.
    /// </summary>
    public bool SeasonAwareMatching { get; set; } = true;

    /// <summary>
    /// Optional manual season-to-AnimeSchedule route overrides.
    /// One rule per line, for example:
    /// My Anime|2=my-anime-second-season
    /// Rules are matched by Jellyfin series name and season number.
    /// </summary>
    public string SeasonRouteOverrides { get; set; } = string.Empty;

    /// <summary>
    /// v0.4 structured mappings keyed by Jellyfin series ID + season number.
    /// Stored as JSON to keep BasePluginConfiguration serialization simple.
    /// </summary>
    public string ManualMappingsJson { get; set; } = "[]";

    /// <summary>
    /// Automatically retry transient AnimeSchedule/network failures.
    /// </summary>
    public bool EnableRetryQueue { get; set; } = true;

    /// <summary>
    /// If a matched anime is not already on the AnimeSchedule list, try to
    /// create it using AnimeSchedule's documented GET ETag -> PUT flow.
    /// </summary>
    public bool AutoAddMissingShows { get; set; } = true;

    /// <summary>
    /// If a completed anime's episode 1 is played again, treat that as the
    /// start of a rewatch and reset AnimeSchedule progress to episode 1.
    /// AnimeSchedule v3 has no native rewatch-count field.
    /// </summary>
    public bool EnableRewatchMode { get; set; } = true;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresUtc { get; set; } = DateTime.MinValue;

    public string PendingOAuthState { get; set; } = string.Empty;

    public string PendingPkceVerifier { get; set; } = string.Empty;
}

public enum SyncMode { None = 0, Personal = 1, ServerShared = 2 }

public sealed class AnimeConnection
{
    // Changes on reconnect/disconnect so queued work cannot move between accounts.
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiresUtc { get; set; }
}

public sealed class UserConnection
{
    public Guid UserId { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public SyncMode Mode { get; set; } = SyncMode.None;
    public AnimeConnection PersonalConnection { get; set; } = new();
}
