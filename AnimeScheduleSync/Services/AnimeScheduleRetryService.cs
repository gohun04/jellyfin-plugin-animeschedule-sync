using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeScheduleSync.Services;

public sealed class AnimeScheduleRetryService : BackgroundService
{
    private readonly AnimeScheduleRuntimeState _state;
    private readonly AnimeScheduleManagementService _management;
    private readonly AnimeScheduleClient _client;
    private readonly ILogger<AnimeScheduleRetryService> _logger;

    public AnimeScheduleRetryService(
        AnimeScheduleRuntimeState state,
        AnimeScheduleManagementService management,
        AnimeScheduleClient client,
        ILogger<AnimeScheduleRetryService> logger)
    {
        _state = state;
        _management = management;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueRetriesAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnimeSchedule retry worker failed.");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }

    public async Task<int> ProcessDueRetriesAsync(CancellationToken cancellationToken) =>
        await ProcessRetriesAsync(forceAll: false, cancellationToken);

    public async Task<int> ProcessAllRetriesAsync(CancellationToken cancellationToken) =>
        await ProcessRetriesAsync(forceAll: true, cancellationToken);

    private async Task<int> ProcessRetriesAsync(
        bool forceAll,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableRetryQueue || (!forceAll && !config.AutoSync))
        {
            return 0;
        }

        var due = forceAll
            ? _state.GetAllRetries()
            : _state.GetDueRetries(DateTime.UtcNow);

        var processed = 0;

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var episode = _management.FindEpisode(item.EpisodeId);
            if (episode is null || Plugin.Instance?.Configuration.SyncKey(item.UserId) != item.ConnectionId)
            {
                _state.ClearRetry(item.UserId, item.ConnectionId, item.EpisodeId);
                continue;
            }

            try
            {
                await _client.SyncEpisodeAsync(
                    item.UserId, episode,
                    item.EpisodeNumber,
                    cancellationToken, item.ConnectionId);

                _state.MarkRetrySucceeded(item.UserId, item.ConnectionId, item.EpisodeId);
                processed++;
            }
            catch (Exception ex)
            {
                _state.MarkRetryFailed(item.UserId, item.ConnectionId, item.EpisodeId, ex.Message);
                _logger.LogWarning(
                    ex,
                    "AnimeSchedule retry failed for {Series} S{Season}E{Episode}.",
                    item.SeriesName,
                    item.SeasonNumber,
                    item.EpisodeNumber);
            }
        }

        return processed;
    }
}
