using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AnimeScheduleSync.Services;

public sealed class AnimeScheduleManagementService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly AnimeScheduleClient _client;
    private readonly AnimeScheduleRuntimeState _state;

    public AnimeScheduleManagementService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        AnimeScheduleClient client,
        AnimeScheduleRuntimeState state)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _client = client;
        _state = state;
    }

    public IReadOnlyList<LibrarySeasonInfo> GetLibrarySeasons(Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        }).OfType<Episode>();

        return episodes
            .Where(e => e.SeriesId != Guid.Empty)
            .GroupBy(e => new
            {
                e.SeriesId,
                SeriesName = e.SeriesName ?? e.Series?.Name ?? "Unknown Series",
                SeasonNumber = e.ParentIndexNumber ?? e.Season?.IndexNumber ?? 1
            })
            .Select(group =>
            {
                var numbered = group
                    .Where(e => e.IndexNumber is > 0)
                    .ToArray();

                var played = user is null
                    ? Array.Empty<Episode>()
                    : numbered
                        .Where(e => _userDataManager.GetUserData(user, e)?.Played == true)
                        .ToArray();

                return new LibrarySeasonInfo(
                    group.Key.SeriesId,
                    group.Key.SeriesName,
                    group.Key.SeasonNumber,
                    numbered.Length,
                    played.Length,
                    played.Select(e => e.IndexNumber ?? 0).DefaultIfEmpty(0).Max());
            })
            .OrderBy(x => x.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SeasonNumber)
            .ToArray();
    }

    public IReadOnlyList<ManualMappingEntry> GetMappings()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.ManualMappingsJson))
        {
            return Array.Empty<ManualMappingEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ManualMappingEntry>>(
                       config.ManualMappingsJson,
                       JsonOptions)
                   ?? [];
        }
        catch
        {
            return Array.Empty<ManualMappingEntry>();
        }
    }

    public ManualMappingEntry SaveMapping(
        Guid seriesId,
        string seriesName,
        int seasonNumber,
        string route,
        string animeTitle)
    {
        var config = Plugin.Instance?.Configuration
                     ?? throw new InvalidOperationException("Plugin configuration is unavailable.");

        var mappings = GetMappings().ToList();
        mappings.RemoveAll(x =>
            x.SeriesId == seriesId
            && x.SeasonNumber == seasonNumber);

        var entry = new ManualMappingEntry(
            seriesId,
            seriesName.Trim(),
            seasonNumber,
            route.Trim(),
            animeTitle.Trim(),
            DateTime.UtcNow);

        mappings.Add(entry);
        config.ManualMappingsJson = JsonSerializer.Serialize(
            mappings
                .OrderBy(x => x.SeriesName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SeasonNumber),
            JsonOptions);

        Plugin.Instance!.SaveConfiguration();
        return entry;
    }

    public bool DeleteMapping(Guid seriesId, int seasonNumber)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return false;
        }

        var mappings = GetMappings().ToList();
        var removed = mappings.RemoveAll(x =>
            x.SeriesId == seriesId
            && x.SeasonNumber == seasonNumber) > 0;

        if (removed)
        {
            config.ManualMappingsJson = JsonSerializer.Serialize(mappings, JsonOptions);
            Plugin.Instance!.SaveConfiguration();
        }

        return removed;
    }

    public async Task<AnimeScheduleClient.AnimeMatchInfo?> TestMatchAsync(
        Guid seriesId,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        var episode = FindRepresentativeEpisode(seriesId, seasonNumber);
        if (episode is null)
        {
            return null;
        }

        return await _client.TestMatchAsync(episode, cancellationToken);
    }

    public async Task<BulkSyncResult> BulkSyncAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
                     ?? throw new InvalidOperationException("Plugin configuration is unavailable.");

        var connection = config.SyncKey(userId)
            ?? throw new InvalidOperationException("Sync is disabled for this user.");

        var user = _userManager.GetUserById(userId)
                   ?? throw new InvalidOperationException("Configured Jellyfin user was not found.");

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        }).OfType<Episode>()
          .Where(e => e.IndexNumber is > 0 && e.SeriesId != Guid.Empty)
          .ToArray();

        var groups = episodes.GroupBy(e => new
        {
            e.SeriesId,
            SeasonNumber = e.ParentIndexNumber ?? e.Season?.IndexNumber ?? 1
        });

        var attempted = 0;
        var synced = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var played = group
                .Where(e => _userDataManager.GetUserData(user, e)?.Played == true)
                .OrderBy(e => e.IndexNumber)
                .ToArray();

            if (played.Length == 0)
            {
                skipped++;
                continue;
            }

            var highest = played
                .OrderByDescending(e => e.IndexNumber)
                .First();

            attempted++;

            try
            {
                var changed = await _client.SyncEpisodeAsync(
                    userId, highest,
                    highest.IndexNumber!.Value,
                    cancellationToken, connection);

                if (changed) synced++; else skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                _state.RecordFailure(
                    userId, highest,
                    highest.IndexNumber!.Value,
                    "bulk-sync",
                    ex.Message);

                if (config.EnableRetryQueue)
                {
                    _state.EnqueueRetry(
                        userId, connection, highest,
                        highest.IndexNumber!.Value,
                        "Bulk sync: " + ex.Message);
                }
            }

            // Be polite to AnimeSchedule when a large library is reconciled.
            await Task.Delay(150, cancellationToken);
        }

        return new BulkSyncResult(attempted, synced, skipped, failed);
    }

    public async Task<SeasonSyncResult> SyncSeasonAsync(
        Guid userId,
        Guid seriesId,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
                     ?? throw new InvalidOperationException("Plugin configuration is unavailable.");

        var connection = config.SyncKey(userId)
            ?? throw new InvalidOperationException("Sync is disabled for this user.");

        var user = _userManager.GetUserById(userId)
                   ?? throw new InvalidOperationException("Configured Jellyfin user was not found.");

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        }).OfType<Episode>()
          .Where(e =>
              e.SeriesId == seriesId
              && (e.ParentIndexNumber ?? e.Season?.IndexNumber ?? 1) == seasonNumber
              && e.IndexNumber is > 0)
          .ToArray();

        var played = episodes
            .Where(e => _userDataManager.GetUserData(user, e)?.Played == true)
            .OrderByDescending(e => e.IndexNumber)
            .ToArray();

        if (played.Length == 0)
        {
            return new SeasonSyncResult(
                false,
                0,
                "No watched episodes were found for this Jellyfin season.");
        }

        var highest = played[0];
        var episodeNumber = highest.IndexNumber!.Value;

        var changed = await _client.SyncEpisodeAsync(
            userId, highest,
            episodeNumber,
            cancellationToken, connection);

        return new SeasonSyncResult(
            changed,
            episodeNumber,
            changed ? $"Synced through episode {episodeNumber}." : "No change sent (disabled, disconnected, unmatched, or already up to date).");
    }

    public Episode? FindEpisode(Guid episodeId) =>
        _libraryManager.GetItemById(episodeId) as Episode;

    private Episode? FindRepresentativeEpisode(Guid seriesId, int seasonNumber)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        }).OfType<Episode>()
          .Where(e =>
              e.SeriesId == seriesId
              && (e.ParentIndexNumber ?? e.Season?.IndexNumber ?? 1) == seasonNumber)
          .OrderBy(e => e.IndexNumber ?? int.MaxValue)
          .FirstOrDefault();
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record LibrarySeasonInfo(
    Guid SeriesId,
    string SeriesName,
    int SeasonNumber,
    int EpisodeCount,
    int PlayedCount,
    int HighestPlayedEpisode);

public sealed record ManualMappingEntry(
    Guid SeriesId,
    string SeriesName,
    int SeasonNumber,
    string Route,
    string AnimeTitle,
    DateTime UpdatedUtc);

public sealed record BulkSyncResult(
    int Attempted,
    int Synced,
    int Skipped,
    int Failed);


public sealed record SeasonSyncResult(
    bool Synced,
    int EpisodeNumber,
    string Message);
