using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeScheduleSync.Services;

public sealed class AnimeScheduleSyncService : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly AnimeScheduleClient _client;
    private readonly AnimeScheduleRuntimeState _state;
    private readonly ILogger<AnimeScheduleSyncService> _logger;

    public AnimeScheduleSyncService(
        IUserDataManager userDataManager,
        IUserManager userManager,
        AnimeScheduleClient client,
        AnimeScheduleRuntimeState state,
        ILogger<AnimeScheduleSyncService> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _client = client;
        _state = state;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("AnimeSchedule Sync watcher started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private async void OnUserDataSaved(
        object? sender,
        UserDataSaveEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.AutoSync)
            {
                return;
            }

            var connection = config.SyncKey(e.UserId);
            if (connection is null) return;

            if (e.Item is not Episode episode)
            {
                return;
            }

            if (e.SaveReason is not UserDataSaveReason.PlaybackFinished
                and not UserDataSaveReason.TogglePlayed)
            {
                return;
            }

            var user = _userManager.GetUserById(e.UserId);
            if (user is null)
            {
                return;
            }

            var userData = _userDataManager.GetUserData(user, episode);
            if (userData?.Played != true)
            {
                return;
            }

            if (episode.IndexNumber is not int episodeNumber
                || episodeNumber <= 0)
            {
                _logger.LogDebug(
                    "AnimeSchedule Sync: skipping {Episode} because it has no usable episode number.",
                    episode.Name);
                return;
            }

            try
            {
                await _client.SyncEpisodeAsync(
                    e.UserId, episode,
                    episodeNumber,
                    CancellationToken.None, connection);
            }
            catch (Exception ex)
            {
                _state.RecordFailure(
                    e.UserId, episode,
                    episodeNumber,
                    "auto-sync",
                    ex.Message);

                if (config.EnableRetryQueue)
                {
                    _state.EnqueueRetry(
                        e.UserId, connection, episode,
                        episodeNumber,
                        ex.Message);
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AnimeSchedule Sync failed while processing a Jellyfin watched-state change.");
        }
    }
}
