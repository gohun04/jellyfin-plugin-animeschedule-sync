using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AnimeScheduleSync.Services;

public sealed class AnimeScheduleRuntimeState
{
    private readonly object _gate = new();
    private readonly List<SyncHistoryEntry> _history = new();
    private readonly Dictionary<(Guid UserId, string ConnectionId, Guid EpisodeId), RetryQueueEntry> _retryQueue = new();

    private long _successfulActions;
    private long _failedActions;
    private long _addedShows;
    private long _updatedShows;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;

    public void RecordSuccess(
        Guid userId,
        Episode episode,
        int episodeNumber,
        string action,
        string title,
        string route,
        string message)
    {
        lock (_gate)
        {
            _successfulActions++;
            if (string.Equals(action, "added", StringComparison.OrdinalIgnoreCase))
            {
                _addedShows++;
            }
            else if (string.Equals(action, "updated", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(action, "rewatch", StringComparison.OrdinalIgnoreCase))
            {
                _updatedShows++;
            }

            _lastSuccessUtc = DateTime.UtcNow;
            AddHistory(new SyncHistoryEntry(
                userId, DateTime.UtcNow,
                "success",
                action,
                episode.SeriesName ?? episode.Series?.Name ?? episode.Name,
                episode.ParentIndexNumber ?? episode.Season?.IndexNumber ?? 1,
                episodeNumber,
                title,
                route,
                message));
        }
    }

    public void RecordInfo(
        Guid userId,
        Episode episode,
        int episodeNumber,
        string action,
        string message,
        string? title = null,
        string? route = null)
    {
        lock (_gate)
        {
            AddHistory(new SyncHistoryEntry(
                userId, DateTime.UtcNow,
                "info",
                action,
                episode.SeriesName ?? episode.Series?.Name ?? episode.Name,
                episode.ParentIndexNumber ?? episode.Season?.IndexNumber ?? 1,
                episodeNumber,
                title ?? string.Empty,
                route ?? string.Empty,
                message));
        }
    }

    public void RecordFailure(
        Guid userId,
        Episode episode,
        int episodeNumber,
        string action,
        string message,
        string? title = null,
        string? route = null)
    {
        lock (_gate)
        {
            _failedActions++;
            _lastFailureUtc = DateTime.UtcNow;
            AddHistory(new SyncHistoryEntry(
                userId, DateTime.UtcNow,
                "error",
                action,
                episode.SeriesName ?? episode.Series?.Name ?? episode.Name,
                episode.ParentIndexNumber ?? episode.Season?.IndexNumber ?? 1,
                episodeNumber,
                title ?? string.Empty,
                route ?? string.Empty,
                message));
        }
    }

    public void EnqueueRetry(Guid userId, string connectionId, Episode episode, int episodeNumber, string reason)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_retryQueue.TryGetValue((userId, connectionId, episode.Id), out var existing))
            {
                _retryQueue[(userId, connectionId, episode.Id)] = existing with
                {
                    EpisodeNumber = episodeNumber,
                    Reason = reason,
                    NextAttemptUtc = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(existing.Attempts, 5))))
                };
                return;
            }

            _retryQueue[(userId, connectionId, episode.Id)] = new RetryQueueEntry(
                userId, connectionId, episode.Id,
                episode.SeriesName ?? episode.Series?.Name ?? episode.Name,
                episode.ParentIndexNumber ?? episode.Season?.IndexNumber ?? 1,
                episodeNumber,
                reason,
                0,
                now.AddMinutes(2),
                now);
        }
    }

    public IReadOnlyList<RetryQueueEntry> GetDueRetries(DateTime utcNow)
    {
        lock (_gate)
        {
            return _retryQueue.Values
                .Where(x => x.NextAttemptUtc <= utcNow)
                .OrderBy(x => x.NextAttemptUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<RetryQueueEntry> GetAllRetries()
    {
        lock (_gate)
        {
            return _retryQueue.Values
                .OrderBy(x => x.NextAttemptUtc)
                .ToArray();
        }
    }

    public void ClearUserRetries(Guid userId)
    {
        lock (_gate)
        {
            foreach (var key in _retryQueue.Keys.Where(x => x.UserId == userId).ToArray())
                _retryQueue.Remove(key);
        }
    }

    public int ClearRetryQueue()
    {
        lock (_gate)
        {
            var count = _retryQueue.Count;
            _retryQueue.Clear();
            return count;
        }
    }

    public void MarkRetrySucceeded(Guid userId, string connectionId, Guid episodeId)
    {
        lock (_gate)
        {
            _retryQueue.Remove((userId, connectionId, episodeId));
        }
    }

    public void MarkRetryFailed(Guid userId, string connectionId, Guid episodeId, string reason)
    {
        lock (_gate)
        {
            if (!_retryQueue.TryGetValue((userId, connectionId, episodeId), out var item))
            {
                return;
            }

            var attempts = item.Attempts + 1;
            _retryQueue[(userId, connectionId, episodeId)] = item with
            {
                Attempts = attempts,
                Reason = reason,
                NextAttemptUtc = DateTime.UtcNow.AddMinutes(
                    Math.Min(60, Math.Pow(2, Math.Min(attempts + 1, 5))))
            };
        }
    }

    public void ClearRetry(Guid userId, string connectionId, Guid episodeId)
    {
        lock (_gate)
        {
            _retryQueue.Remove((userId, connectionId, episodeId));
        }
    }

    public DashboardSnapshot GetSnapshot(bool connected)
    {
        lock (_gate)
        {
            return new DashboardSnapshot(
                connected,
                _successfulActions,
                _failedActions,
                _addedShows,
                _updatedShows,
                _lastSuccessUtc,
                _lastFailureUtc,
                _retryQueue.Values
                    .OrderBy(x => x.NextAttemptUtc)
                    .ToArray(),
                _history
                    .OrderByDescending(x => x.TimestampUtc)
                    .Take(60)
                    .ToArray());
        }
    }

    private void AddHistory(SyncHistoryEntry entry)
    {
        _history.Add(entry);
        if (_history.Count > 200)
        {
            _history.RemoveRange(0, _history.Count - 200);
        }
    }
}

public sealed record SyncHistoryEntry(
    Guid UserId,
    DateTime TimestampUtc,
    string Level,
    string Action,
    string SeriesName,
    int SeasonNumber,
    int EpisodeNumber,
    string AnimeTitle,
    string Route,
    string Message);

public sealed record RetryQueueEntry(
    Guid UserId,
    string ConnectionId,
    Guid EpisodeId,
    string SeriesName,
    int SeasonNumber,
    int EpisodeNumber,
    string Reason,
    int Attempts,
    DateTime NextAttemptUtc,
    DateTime QueuedUtc);

public sealed record DashboardSnapshot(
    bool Connected,
    long SuccessfulActions,
    long FailedActions,
    long AddedShows,
    long UpdatedShows,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    IReadOnlyList<RetryQueueEntry> RetryQueue,
    IReadOnlyList<SyncHistoryEntry> RecentHistory);
