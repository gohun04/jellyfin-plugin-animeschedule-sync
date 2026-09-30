using Microsoft.AspNetCore.Authorization;
using Jellyfin.Plugin.AnimeScheduleSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeScheduleSync.Api;

[Authorize(Policy = "RequiresElevation")]
[ApiController]
[Route("AnimeSchedule")]
public sealed class AnimeScheduleController : ControllerBase
{
    private readonly AnimeScheduleClient _client;
    private readonly AnimeScheduleManagementService _management;
    private readonly AnimeScheduleRuntimeState _state;
    private readonly AnimeScheduleRetryService _retryService;
    private readonly ILogger<AnimeScheduleController> _logger;

    public AnimeScheduleController(
        AnimeScheduleClient client,
        AnimeScheduleManagementService management,
        AnimeScheduleRuntimeState state,
        AnimeScheduleRetryService retryService,
        ILogger<AnimeScheduleController> logger)
    {
        _client = client;
        _management = management;
        _state = state;
        _retryService = retryService;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus([FromQuery] Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Problem("AnimeSchedule Sync plugin configuration is unavailable.");
        }

        return Ok(new
        {
            connected = !string.IsNullOrWhiteSpace(config.ResolveConnection(userId)?.AccessToken),
            expiresUtc = config.ResolveConnection(userId)?.AccessTokenExpiresUtc,
            configured =
                !string.IsNullOrWhiteSpace(config.ClientId)
                && !string.IsNullOrWhiteSpace(config.ClientSecret)
                && !string.IsNullOrWhiteSpace(config.ApplicationToken)
                && !string.IsNullOrWhiteSpace(config.RedirectUri)
                && config.ResolveConnection(userId) is not null,
            mode = (config.FindUser(userId)?.Mode ?? Configuration.SyncMode.None).ToString()
        });
    }

    [HttpGet("dashboard")]
    public IActionResult GetDashboard()
    {
        var config = Plugin.Instance?.Configuration;
        var connected = config is not null && config.UserConnections.Any(x =>
            !string.IsNullOrWhiteSpace(config.ResolveConnection(x.UserId)?.AccessToken));
        var snapshot = _state.GetSnapshot(connected);

        // Use explicit camelCase property names. Jellyfin's MVC JSON settings
        // preserve PascalCase record property names in this plugin environment,
        // while the management page is JavaScript and expects camelCase.
        return Ok(new
        {
            connected = snapshot.Connected,
            successfulActions = snapshot.SuccessfulActions,
            failedActions = snapshot.FailedActions,
            addedShows = snapshot.AddedShows,
            updatedShows = snapshot.UpdatedShows,
            lastSuccessUtc = snapshot.LastSuccessUtc,
            lastFailureUtc = snapshot.LastFailureUtc,
            retryQueue = snapshot.RetryQueue.Select(x => new
            {
                userId = x.UserId,
                episodeId = x.EpisodeId,
                seriesName = x.SeriesName,
                seasonNumber = x.SeasonNumber,
                episodeNumber = x.EpisodeNumber,
                reason = x.Reason,
                attempts = x.Attempts,
                nextAttemptUtc = x.NextAttemptUtc,
                queuedUtc = x.QueuedUtc
            }).ToArray(),
            recentHistory = snapshot.RecentHistory.Select(x => new
            {
                userId = x.UserId,
                timestampUtc = x.TimestampUtc,
                level = x.Level,
                action = x.Action,
                seriesName = x.SeriesName,
                seasonNumber = x.SeasonNumber,
                episodeNumber = x.EpisodeNumber,
                animeTitle = x.AnimeTitle,
                route = x.Route,
                message = x.Message
            }).ToArray()
        });
    }

    [HttpGet("librarySeasons")]
    public IActionResult GetLibrarySeasons([FromQuery] Guid userId)
    {
        var rows = _management.GetLibrarySeasons(userId);
        return Ok(rows.Select(x => new
        {
            seriesId = x.SeriesId,
            seriesName = x.SeriesName,
            seasonNumber = x.SeasonNumber,
            episodeCount = x.EpisodeCount,
            playedCount = x.PlayedCount,
            highestPlayedEpisode = x.HighestPlayedEpisode
        }).ToArray());
    }

    [HttpGet("mappings")]
    public IActionResult GetMappings()
    {
        var rows = _management.GetMappings();
        return Ok(rows.Select(x => new
        {
            seriesId = x.SeriesId,
            seriesName = x.SeriesName,
            seasonNumber = x.SeasonNumber,
            route = x.Route,
            animeTitle = x.AnimeTitle,
            updatedUtc = x.UpdatedUtc
        }).ToArray());
    }

    [HttpPost("mappings")]
    public IActionResult SaveMapping([FromBody] MappingRequest request)
    {
        if (request.SeriesId == Guid.Empty
            || request.SeasonNumber <= 0
            || string.IsNullOrWhiteSpace(request.Route))
        {
            return BadRequest("Series ID, season number and AnimeSchedule route are required.");
        }

        var saved = _management.SaveMapping(
            request.SeriesId,
            request.SeriesName ?? string.Empty,
            request.SeasonNumber,
            request.Route,
            request.AnimeTitle ?? request.Route);

        return Ok(new
        {
            seriesId = saved.SeriesId,
            seriesName = saved.SeriesName,
            seasonNumber = saved.SeasonNumber,
            route = saved.Route,
            animeTitle = saved.AnimeTitle,
            updatedUtc = saved.UpdatedUtc
        });
    }

    [HttpDelete("mappings")]
    public IActionResult DeleteMapping(
        [FromQuery] Guid seriesId,
        [FromQuery] int seasonNumber) =>
        Ok(new
        {
            removed = _management.DeleteMapping(seriesId, seasonNumber)
        });

    [HttpGet("testMatch")]
    public async Task<IActionResult> TestMatch(
        [FromQuery] Guid seriesId,
        [FromQuery] int seasonNumber,
        CancellationToken cancellationToken)
    {
        var match = await _management.TestMatchAsync(
            seriesId,
            seasonNumber,
            cancellationToken);

        return match is null
            ? NotFound(new { message = "No AnimeSchedule match was found." })
            : Ok(new
            {
                route = match.Route,
                title = match.Title,
                episodes = match.Episodes,
                malId = match.MalId
            });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        CancellationToken cancellationToken)
    {
        var results = await _client.SearchAnimeAsync(q, cancellationToken);
        return Ok(results.Select(x => new
        {
            route = x.Route,
            title = x.Title,
            episodes = x.Episodes,
            malId = x.MalId
        }).ToArray());
    }

    [HttpPost("bulkSync")]
    public async Task<IActionResult> BulkSync(
        [FromQuery] Guid userId,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.SyncKey(userId) is null)
            return BadRequest("Select a Jellyfin user with Personal or Server / Shared sync enabled.");
        var result = await _management.BulkSyncAsync(userId, cancellationToken);
        return Ok(new
        {
            attempted = result.Attempted,
            synced = result.Synced,
            skipped = result.Skipped,
            failed = result.Failed
        });
    }

    [HttpPost("retryNow")]
    public async Task<IActionResult> RetryNow(
        CancellationToken cancellationToken) =>
        Ok(new
        {
            processed = await _retryService.ProcessAllRetriesAsync(cancellationToken)
        });

    [HttpPost("clearRetries")]
    public IActionResult ClearRetries() =>
        Ok(new
        {
            cleared = _state.ClearRetryQueue()
        });

    [HttpPost("syncSeason")]
    public async Task<IActionResult> SyncSeason(
        [FromQuery] Guid userId,
        [FromQuery] Guid seriesId,
        [FromQuery] int seasonNumber,
        CancellationToken cancellationToken)
    {
        if (seriesId == Guid.Empty || seasonNumber <= 0)
        {
            return BadRequest("A Jellyfin series ID and season number are required.");
        }

        if (Plugin.Instance?.Configuration.SyncKey(userId) is null)
            return BadRequest("Sync is disabled for this Jellyfin user.");
        var result = await _management.SyncSeasonAsync(
            userId, seriesId,
            seasonNumber,
            cancellationToken);

        return Ok(new
        {
            synced = result.Synced,
            episodeNumber = result.EpisodeNumber,
            message = result.Message
        });
    }

    [HttpGet("diagnostics")]
    public IActionResult Diagnostics()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Problem("Plugin configuration is unavailable.");
        }

        return Ok(new
        {
            pluginVersion = "0.5.3.0",
            configurationLoaded = true,
            clientIdLength = config.ClientId?.Length ?? 0,
            clientSecretStored = !string.IsNullOrWhiteSpace(config.ClientSecret),
            applicationTokenStored = !string.IsNullOrWhiteSpace(config.ApplicationToken),
            redirectUriStored = !string.IsNullOrWhiteSpace(config.RedirectUri),
            assignedUsers = config.UserConnections.Count(x => x.Mode != Configuration.SyncMode.None),
            accessTokenStored = !string.IsNullOrWhiteSpace(config.ServerConnection.AccessToken),
            refreshTokenStored = !string.IsNullOrWhiteSpace(config.ServerConnection.RefreshToken),
            accessTokenExpiresUtc = config.ServerConnection.AccessTokenExpiresUtc,
            structuredMappings = _management.GetMappings().Count,
            retryQueueEnabled = config.EnableRetryQueue
        });
    }

    public sealed record MappingRequest(
        Guid SeriesId,
        string? SeriesName,
        int SeasonNumber,
        string Route,
        string? AnimeTitle);

}
