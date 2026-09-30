using Jellyfin.Plugin.AnimeScheduleSync.Configuration;
using MediaBrowser.Controller.Library;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeScheduleSync.Services;

public sealed class AnimeScheduleClient
{
    private const string ApiBase = "https://animeschedule.net/api/v3";
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnimeScheduleClient> _logger;
    private readonly AnimeScheduleRuntimeState _state;
    private readonly IUserManager _users;
    public static SemaphoreSlim ConnectionGate { get; } = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AnimeScheduleClient(
        ILogger<AnimeScheduleClient> logger,
        AnimeScheduleRuntimeState state,
        IUserManager users) : this(logger, state, users, new HttpClient()) { }

    internal AnimeScheduleClient(ILogger<AnimeScheduleClient> logger, AnimeScheduleRuntimeState state,
        IUserManager users, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _logger = logger;
        _state = state;
        _users = users;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Jellyfin-AnimeSchedule-Sync/0.5.3-Jellyfin12");
    }

    public async Task ExchangeAuthorizationCodeAsync(
        AnimeConnection connection,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        var config = RequireConfiguration();

        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config.RedirectUri,
            ["code_verifier"] = verifier
        };

        using var request = CreateOAuthTokenRequest(fields, config);

        var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        await StoreTokenResponseAsync(connection, response, cancellationToken);
    }

    private HttpRequestMessage CreateOAuthTokenRequest(
        Dictionary<string, string> fields,
        Configuration.PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ClientSecret))
        {
            throw new InvalidOperationException(
                "AnimeSchedule OAuth Client Secret is not configured.");
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            ApiBase + "/oauth2/token")
        {
            Content = new FormUrlEncodedContent(fields)
        };

        // Match the working anime-schedule-rs implementation:
        // BasicClient(client_id, client_secret) + Authorization Code + PKCE.
        // RFC 6749 2.3.1 form-url-encodes both credentials before Basic auth.
        var encodedClientId = System.Net.WebUtility.UrlEncode(config.ClientId);
        var encodedClientSecret = System.Net.WebUtility.UrlEncode(config.ClientSecret);
        var basicBytes = Encoding.UTF8.GetBytes(
            encodedClientId + ":" + encodedClientSecret);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(basicBytes));

        _logger.LogInformation(
            "AnimeSchedule OAuth token request using Client ID + Client Secret with PKCE. ClientIdLength={ClientIdLength}, ClientSecretLength={ClientSecretLength}, HasCodeVerifier={HasCodeVerifier}",
            config.ClientId.Length,
            config.ClientSecret.Length,
            fields.ContainsKey("code_verifier"));

        return request;
    }

    public async Task<bool> SyncEpisodeAsync(Guid userId, Episode episode, int watchedEpisodeNumber,
        CancellationToken cancellationToken, string? expectedConnectionId = null)
    {
        await ConnectionGate.WaitAsync(cancellationToken);
        try
        {
            var connection = RequireConfiguration().ResolveConnection(userId);
            if (connection is null || _users.GetUserById(userId) is null) return false;
            if (expectedConnectionId is not null && RequireConfiguration().SyncKey(userId) != expectedConnectionId) return false;
            return await SyncEpisodeCoreAsync(userId, connection, episode, watchedEpisodeNumber, cancellationToken);
        }
        finally { ConnectionGate.Release(); }
    }

    private static void EnsureSelected(Guid userId, AnimeConnection connection)
    {
        var current = RequireConfiguration().ResolveConnection(userId);
        if (current is null || current.Id != connection.Id)
            throw new OperationCanceledException("The user's sync connection changed.");
    }

    private async Task<bool> SyncEpisodeCoreAsync(
        Guid userId, AnimeConnection connection,
        Episode episode,
        int watchedEpisodeNumber,
        CancellationToken cancellationToken)
    {
        var config = RequireConfiguration();

        if (string.IsNullOrWhiteSpace(config.ApplicationToken))
        {
            _logger.LogWarning(
                "AnimeSchedule Sync: Application Token is not configured.");
            return false;
        }

        var anime = await FindAnimeAsync(episode, cancellationToken);
        if (anime is null)
        {
            _logger.LogWarning(
                "AnimeSchedule Sync: could not identify {Series}, season {Season}.",
                episode.SeriesName ?? episode.Name,
                episode.ParentIndexNumber);

            _state.RecordFailure(
                userId, episode,
                watchedEpisodeNumber,
                "match",
                "Could not identify a matching AnimeSchedule title.");
            return false;
        }

        _logger.LogInformation(
            "AnimeSchedule Sync: resolved {Series} season {Season} episode {Episode} -> {Title} ({Route}).",
            episode.SeriesName ?? episode.Series?.Name ?? episode.Name,
            episode.ParentIndexNumber ?? episode.Season?.IndexNumber ?? 1,
            watchedEpisodeNumber,
            anime.Value.Title,
            anime.Value.Route);

        var accessToken = await GetValidAccessTokenAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogWarning(
                "AnimeSchedule Sync: AnimeSchedule account is not connected.");
            return false;
        }

        // Determine membership from the user's actual list rather than guessing
        // from the specific-entry response. This makes Add/Update deterministic.
        var alreadyOnList = await IsAnimeOnListAsync(
            anime.Value.Route,
            accessToken,
            cancellationToken);

        // AnimeSchedule requires a valid ETag on every PUT. Get it immediately
        // before the write, whether this is an add or an update.
        var current = await GetAnimeListEntryAsync(
            anime.Value.Route,
            accessToken,
            cancellationToken);

        if (!alreadyOnList)
        {
            if (!config.AutoAddMissingShows)
            {
                _logger.LogInformation(
                    "AnimeSchedule Sync: {Title} is not on the list and auto-add is disabled.",
                    anime.Value.Title);
                return false;
            }

            // A missing AnimeSchedule list entry currently returns HTTP 200 with
            // an empty body and no ETag, despite the Add/Update endpoint requiring
            // a valid ETag. Use AnimeSchedule's documented MAL XML import endpoint
            // to create the missing entry, then re-read it to obtain a real ETag.
            if (current is null || string.IsNullOrWhiteSpace(current.Value.ETag))
            {
                EnsureSelected(userId, connection);
                var imported = await TryImportMissingAnimeViaMalAsync(
                    anime.Value,
                    watchedEpisodeNumber,
                    accessToken,
                    cancellationToken);

                if (!imported)
                {
                    _logger.LogWarning(
                        "AnimeSchedule Sync: could not auto-add {Title} ({Route}). No usable MAL ID/import path was available.",
                        anime.Value.Title,
                        anime.Value.Route);
                    return false;
                }

                current = await GetAnimeListEntryAsync(
                    anime.Value.Route,
                    accessToken,
                    cancellationToken);

                if (current is null || string.IsNullOrWhiteSpace(current.Value.ETag))
                {
                    _logger.LogWarning(
                        "AnimeSchedule Sync: imported {Title}, but AnimeSchedule still did not return an ETag afterward.",
                        anime.Value.Title);
                    return false;
                }
            }

            var addStatus =
                config.SetCompletedStatus
                && anime.Value.Episodes > 0
                && watchedEpisodeNumber >= anime.Value.Episodes
                    ? "completed"
                    : "watching";

            // The import already creates the show. This follow-up PUT normalizes
            // exact progress/status using the freshly obtained real ETag.
            EnsureSelected(userId, connection);
            await UpdateAnimeListEntryAsync(
                anime.Value.Route,
                watchedEpisodeNumber,
                addStatus,
                current.Value.ETag,
                accessToken,
                cancellationToken,
                isNewEntry: true);

            _logger.LogInformation(
                "AnimeSchedule Sync: added {Title} ({Route}) to AnimeSchedule as {Status} at episode {Episode}.",
                anime.Value.Title,
                anime.Value.Route,
                addStatus,
                watchedEpisodeNumber);

            _state.RecordSuccess(
                userId, episode,
                watchedEpisodeNumber,
                "added",
                anime.Value.Title,
                anime.Value.Route,
                $"Added to {addStatus} at episode {watchedEpisodeNumber}.");
            return true;
        }

        if (current is null)
        {
            _logger.LogWarning(
                "AnimeSchedule Sync: existing list entry {Title} ({Route}) could not be read with an ETag.",
                anime.Value.Title,
                anime.Value.Route);
            return false;
        }

        var rewatchStart =
            config.EnableRewatchMode
            && watchedEpisodeNumber == 1
            && string.Equals(
                current.Value.ListStatus,
                "completed",
                StringComparison.OrdinalIgnoreCase);

        if (rewatchStart)
        {
            EnsureSelected(userId, connection);
            await UpdateAnimeListEntryAsync(
                anime.Value.Route,
                1,
                "watching",
                current.Value.ETag,
                accessToken,
                cancellationToken);

            _logger.LogInformation(
                "AnimeSchedule Sync: started rewatch of {Title}; progress reset to episode 1 and status set to Watching.",
                anime.Value.Title);

            _state.RecordSuccess(
                userId, episode,
                watchedEpisodeNumber,
                "rewatch",
                anime.Value.Title,
                anime.Value.Route,
                "Started rewatch at episode 1.");
            return true;
        }

        if (config.NeverDecreaseProgress
            && current.Value.EpisodesSeen >= watchedEpisodeNumber)
        {
            _logger.LogDebug(
                "AnimeSchedule Sync: {Title} is already at episode {Current}; not decreasing to {Watched}.",
                anime.Value.Title,
                current.Value.EpisodesSeen,
                watchedEpisodeNumber);
            return false;
        }

        var status = current.Value.ListStatus;

        if (config.SetCompletedStatus
            && anime.Value.Episodes > 0
            && watchedEpisodeNumber >= anime.Value.Episodes)
        {
            status = "completed";
        }
        else if (config.SetWatchingStatus)
        {
            status = "watching";
        }

        EnsureSelected(userId, connection);
        await UpdateAnimeListEntryAsync(
            anime.Value.Route,
            watchedEpisodeNumber,
            status,
            current.Value.ETag,
            accessToken,
            cancellationToken);

        _logger.LogInformation(
            "AnimeSchedule Sync: updated {Title} ({Route}) to episode {Episode} ({Status}).",
            anime.Value.Title,
            anime.Value.Route,
            watchedEpisodeNumber,
            status);

        _state.RecordSuccess(
            userId, episode,
            watchedEpisodeNumber,
            "updated",
            anime.Value.Title,
            anime.Value.Route,
            $"Updated to episode {watchedEpisodeNumber} ({status}).");
        return true;
    }

    public async Task<AnimeMatchInfo?> TestMatchAsync(
        Episode episode,
        CancellationToken cancellationToken)
    {
        var match = await FindAnimeAsync(episode, cancellationToken);
        return match is null
            ? null
            : new AnimeMatchInfo(
                match.Value.Route,
                match.Value.Title,
                match.Value.Episodes,
                match.Value.MalId);
    }

    public async Task<IReadOnlyList<AnimeSearchResult>> SearchAnimeAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<AnimeSearchResult>();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ApiBase + "/anime?q=" + Uri.EscapeDataString(query.Trim()));

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RequireConfiguration().ApplicationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var objects = new List<JsonElement>();
        CollectAnimeObjects(doc.RootElement, objects);

        return objects
            .Select(obj => new AnimeSearchResult(
                GetString(obj, "route") ?? string.Empty,
                GetString(obj, "title") ?? string.Empty,
                GetInt(obj, "episodes"),
                TryGetMalId(obj)))
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Route)
                && !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => x.Route, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(25)
            .ToArray();
    }

    private async Task<AnimeMatch?> FindAnimeAsync(
        Episode episode,
        CancellationToken cancellationToken)
    {
        var config = RequireConfiguration();
        var series = episode.Series;
        if (series is null)
        {
            return null;
        }

        var season = episode.Season;
        var seasonNumber = episode.ParentIndexNumber ?? season?.IndexNumber ?? 1;
        var seasonName = season?.Name ?? episode.SeasonName;

        // v0.4 structured mapping wins and is keyed by Jellyfin's stable
        // Series ID + season number. The old text rules remain a fallback.
        var overrideRoute = TryGetStructuredRouteOverride(
            config.ManualMappingsJson,
            series.Id,
            seasonNumber)
            ?? TryGetSeasonRouteOverride(
                config.SeasonRouteOverrides,
                series.Name,
                seasonNumber);

        if (!string.IsNullOrWhiteSpace(overrideRoute))
        {
            var overridden = await GetAnimeByRouteAsync(
                overrideRoute,
                cancellationToken);

            if (overridden is not null)
            {
                _logger.LogInformation(
                    "AnimeSchedule Sync: manual override matched {Series} season {Season} to {Title} ({Route}).",
                    series.Name,
                    seasonNumber,
                    overridden.Value.Title,
                    overridden.Value.Route);
                return overridden;
            }

            _logger.LogWarning(
                "AnimeSchedule Sync: manual override route {Route} for {Series} season {Season} was not found on AnimeSchedule.",
                overrideRoute,
                series.Name,
                seasonNumber);
        }

        if (config.SeasonAwareMatching && seasonNumber > 1)
        {
            // Best source: IDs attached directly to Jellyfin's Season object.
            // AniList/AniDB/MAL normally use distinct IDs for later anime seasons.
            var seasonQueries = BuildProviderQueries(season?.ProviderIds);

            foreach (var providerQuery in seasonQueries)
            {
                var match = await FindAnimeByQueryAsync(
                    providerQuery.Query,
                    series.Name,
                    $"season {seasonNumber} {providerQuery.Provider}",
                    cancellationToken,
                    seasonNumber);

                if (match is not null)
                {
                    _logger.LogInformation(
                        "AnimeSchedule Sync: matched {Series} season {Season} to {Title} ({Route}) using season {Provider} ID {ProviderId}.",
                        series.Name,
                        seasonNumber,
                        match.Value.Title,
                        match.Value.Route,
                        providerQuery.Provider,
                        providerQuery.ProviderId);
                    return match;
                }
            }

            // Do NOT reuse the parent-series provider IDs here. They very often
            // identify season 1. Instead search explicit later-season titles.
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(seasonName)
                && !string.Equals(
                    seasonName,
                    $"Season {seasonNumber}",
                    StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(seasonName);
                candidates.Add($"{series.Name} {seasonName}");
            }

            candidates.Add($"{series.Name} Season {seasonNumber}");
            candidates.Add($"{series.Name} {OrdinalSeasonName(seasonNumber)} Season");
            candidates.Add($"{series.Name} Part {seasonNumber}");

            foreach (var candidate in candidates
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var match = await FindAnimeByQueryAsync(
                    "q=" + Uri.EscapeDataString(candidate),
                    candidate,
                    $"season-title '{candidate}'",
                    cancellationToken,
                    seasonNumber);

                if (match is not null)
                {
                    _logger.LogInformation(
                        "AnimeSchedule Sync: matched {Series} season {Season} to {Title} ({Route}) using season-aware title search.",
                        series.Name,
                        seasonNumber,
                        match.Value.Title,
                        match.Value.Route);
                    return match;
                }
            }

            _logger.LogWarning(
                "AnimeSchedule Sync: no season-specific match for {Series} season {Season}. Add a Season Route Override if Jellyfin has no season-level anime IDs.",
                series.Name,
                seasonNumber);
            return null;
        }

        // Season 1 / single-season series.
        foreach (var providerQuery in BuildProviderQueries(series.ProviderIds))
        {
            var match = await FindAnimeByQueryAsync(
                providerQuery.Query,
                series.Name,
                $"series {providerQuery.Provider}",
                cancellationToken,
                1);

            if (match is not null)
            {
                return match;
            }
        }

        return await FindAnimeByQueryAsync(
            "q=" + Uri.EscapeDataString(series.Name),
            series.Name,
            "series title",
            cancellationToken,
            1);
    }

    private static string? TryGetStructuredRouteOverride(
        string json,
        Guid seriesId,
        int seasonNumber)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var mappings = JsonSerializer.Deserialize<List<ManualMappingEntry>>(
                json,
                AnimeScheduleManagementService.JsonOptions);

            return mappings?
                .FirstOrDefault(x =>
                    x.SeriesId == seriesId
                    && x.SeasonNumber == seasonNumber)?
                .Route;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetSeasonRouteOverride(
        string rules,
        string seriesName,
        int seasonNumber)
    {
        if (string.IsNullOrWhiteSpace(rules))
        {
            return null;
        }

        foreach (var rawLine in rules.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var equalsIndex = rawLine.IndexOf('=');
            var pipeIndex = rawLine.LastIndexOf('|');

            if (equalsIndex <= 0
                || pipeIndex <= 0
                || pipeIndex >= equalsIndex)
            {
                continue;
            }

            var configuredSeries = rawLine[..pipeIndex].Trim();
            var configuredSeasonText =
                rawLine[(pipeIndex + 1)..equalsIndex].Trim();
            var route = rawLine[(equalsIndex + 1)..].Trim();

            if (string.Equals(
                    configuredSeries,
                    seriesName,
                    StringComparison.OrdinalIgnoreCase)
                && int.TryParse(configuredSeasonText, out var configuredSeason)
                && configuredSeason == seasonNumber
                && !string.IsNullOrWhiteSpace(route))
            {
                return route;
            }
        }

        return null;
    }

    private static string OrdinalSeasonName(int seasonNumber) =>
        seasonNumber switch
        {
            1 => "1st",
            2 => "2nd",
            3 => "3rd",
            _ => seasonNumber + "th"
        };

    private static List<ProviderQuery> BuildProviderQueries(
        IReadOnlyDictionary<string, string>? providers)
    {
        var result = new List<ProviderQuery>();

        if (providers is null)
        {
            return result;
        }

        foreach (var pair in providers)
        {
            if (!long.TryParse(pair.Value, out _))
            {
                continue;
            }

            var key = pair.Key.Replace("_", string.Empty)
                              .Replace("-", string.Empty)
                              .ToLowerInvariant();

            if (key.Contains("anilist", StringComparison.Ordinal))
            {
                result.Add(new ProviderQuery(
                    "AniList",
                    pair.Value,
                    "anilist-ids=" + Uri.EscapeDataString(pair.Value)));
            }
            else if (key.Contains("anidb", StringComparison.Ordinal))
            {
                result.Add(new ProviderQuery(
                    "AniDB",
                    pair.Value,
                    "anidb-ids=" + Uri.EscapeDataString(pair.Value)));
            }
            else if (key is "mal" or "myanimelist"
                     || key.Contains("myanimelist", StringComparison.Ordinal))
            {
                result.Add(new ProviderQuery(
                    "MyAnimeList",
                    pair.Value,
                    "mal-ids=" + Uri.EscapeDataString(pair.Value)));
            }
        }

        // Prefer AniList, then AniDB, then MAL for deterministic matching.
        return result
            .OrderBy(q => q.Provider switch
            {
                "AniList" => 0,
                "AniDB" => 1,
                "MyAnimeList" => 2,
                _ => 99
            })
            .ToList();
    }

    private async Task<AnimeMatch?> FindAnimeByQueryAsync(
        string query,
        string fallbackTitle,
        string source,
        CancellationToken cancellationToken,
        int? expectedSeasonNumber = null)
    {
        using var request =
            new HttpRequestMessage(HttpMethod.Get, ApiBase + "/anime?" + query);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RequireConfiguration().ApplicationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AnimeSchedule lookup via {Source} failed with HTTP {Status}.",
                source,
                response.StatusCode);

            if ((int)response.StatusCode >= 500)
            {
                throw new HttpRequestException(
                    $"AnimeSchedule lookup via {source} returned HTTP {(int)response.StatusCode}.");
            }

            return null;
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var candidates = new List<JsonElement>();
        CollectAnimeObjects(doc.RootElement, candidates);

        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates
            .Select(obj => new
            {
                Object = obj,
                Score = ScoreAnimeCandidate(
                    obj,
                    fallbackTitle,
                    expectedSeasonNumber)
            })
            .OrderByDescending(v => v.Score)
            .First();

        var route = GetString(best.Object, "route");
        var title = GetString(best.Object, "title");

        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        _logger.LogDebug(
            "AnimeSchedule Sync: lookup via {Source} chose {Title} ({Route}) from {Count} candidate(s), score {Score}.",
            source,
            title ?? fallbackTitle,
            route,
            candidates.Count,
            best.Score);

        return new AnimeMatch(
            route,
            string.IsNullOrWhiteSpace(title) ? fallbackTitle : title,
            GetInt(best.Object, "episodes"),
            TryGetMalId(best.Object));
    }

    private async Task<AnimeMatch?> GetAnimeByRouteAsync(
        string route,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ApiBase + "/anime/" + Uri.EscapeDataString(route));

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RequireConfiguration().ApplicationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var obj = FindFirstObjectWithRoute(doc.RootElement);
        if (obj is null)
        {
            return null;
        }

        var actualRoute = GetString(obj.Value, "route");
        var title = GetString(obj.Value, "title");

        if (string.IsNullOrWhiteSpace(actualRoute))
        {
            return null;
        }

        return new AnimeMatch(
            actualRoute,
            string.IsNullOrWhiteSpace(title) ? route : title,
            GetInt(obj.Value, "episodes"),
            TryGetMalId(obj.Value));
    }

    private static void CollectAnimeObjects(
        JsonElement element,
        List<JsonElement> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("route", out _)
                && (element.TryGetProperty("title", out _)
                    || element.TryGetProperty("episodes", out _)))
            {
                results.Add(element.Clone());
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectAnimeObjects(property.Value, results);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectAnimeObjects(item, results);
            }
        }
    }

    private static int ScoreAnimeCandidate(
        JsonElement obj,
        string expectedTitle,
        int? seasonNumber)
    {
        var title = GetString(obj, "title") ?? string.Empty;
        var route = GetString(obj, "route") ?? string.Empty;
        var haystack = NormalizeForMatch(title + " " + route);
        var expected = NormalizeForMatch(expectedTitle);

        var score = 0;

        if (haystack == expected)
        {
            score += 100;
        }
        else if (!string.IsNullOrWhiteSpace(expected)
                 && haystack.Contains(expected, StringComparison.Ordinal))
        {
            score += 50;
        }

        if (seasonNumber is > 1)
        {
            var n = seasonNumber.Value;
            var ordinal = NormalizeForMatch(OrdinalSeasonName(n) + " season");

            var markers = new[]
            {
                NormalizeForMatch($"season {n}"),
                ordinal,
                NormalizeForMatch($"part {n}"),
                NormalizeForMatch($"season-{n}")
            };

            if (markers.Any(m =>
                    !string.IsNullOrWhiteSpace(m)
                    && haystack.Contains(m, StringComparison.Ordinal)))
            {
                score += 60;
            }

            // Penalize a very obvious season-1 result when we are looking
            // for a later season.
            if (haystack.Contains("season1", StringComparison.Ordinal)
                || haystack.Contains("1stseason", StringComparison.Ordinal))
            {
                score -= 80;
            }
        }

        return score;
    }

    private static string NormalizeForMatch(string value) =>
        new string(
            value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

    private static string? TryGetMalId(JsonElement obj)
    {
        if (!obj.TryGetProperty("websites", out var websites)
            || websites.ValueKind != JsonValueKind.Object
            || !websites.TryGetProperty("mal", out var mal)
            || mal.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = mal.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Handles both a raw numeric ID and URLs such as
        // https://myanimelist.net/anime/5114/...
        var match = Regex.Match(value, @"(?:/anime/)?(?<id>\d+)");
        return match.Success ? match.Groups["id"].Value : null;
    }

    private async Task<bool> TryImportMissingAnimeViaMalAsync(
        AnimeMatch anime,
        int watchedEpisodeNumber,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(anime.MalId))
        {
            // Fetch the full anime object because search results may omit Websites.
            var full = await GetAnimeByRouteAsync(
                anime.Route,
                cancellationToken);

            if (full is null || string.IsNullOrWhiteSpace(full.Value.MalId))
            {
                return false;
            }

            anime = full.Value;
        }

        var now = DateTime.UtcNow;
        var watched = Math.Max(0, watchedEpisodeNumber);
        var status =
            anime.Episodes > 0 && watched >= anime.Episodes
                ? "Completed"
                : "Watching";

        // Standard MyAnimeList XML export shape. AnimeSchedule's documented
        // /animelists/oauth import endpoint accepts a MAL XML file and, when
        // overwrite-mal-list is omitted, only adds entries that do not exist.
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("myanimelist",
                new XElement("myinfo",
                    new XElement("user_export_type", "1")),
                new XElement("anime",
                    new XElement("series_animedb_id", anime.MalId),
                    new XElement("series_title", anime.Title),
                    new XElement("series_episodes", anime.Episodes),
                    new XElement("my_id", "0"),
                    new XElement("my_watched_episodes", watched),
                    new XElement("my_start_date", now.ToString("yyyy-MM-dd")),
                    new XElement("my_finish_date", "0000-00-00"),
                    new XElement("my_score", "0"),
                    new XElement("my_status", status),
                    new XElement("my_rewatching", "0"),
                    new XElement("my_rewatching_ep", "0"),
                    new XElement("update_on_import", "1"))));

        var xml =
            doc.Declaration + Environment.NewLine
            + doc.Root!.ToString(SaveOptions.DisableFormatting);

        // Some upload validators determine MIME type from file contents rather
        // than trusting the multipart Content-Type header. A real MAL export
        // begins with an XML declaration, so ensure the generated file does too.

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            ApiBase + "/animelists/oauth");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        var multipart = new MultipartFormDataContent();

        // Mirror reqwest::multipart::Part exactly:
        //   field name: mal-list
        //   filename:   list.xml
        //   mime:       text/xml
        //
        // Set Content-Disposition explicitly instead of letting
        // MultipartFormDataContent infer it, because AnimeSchedule validates
        // the uploaded file type before parsing the XML.
        var xmlContent = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        xmlContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");

        xmlContent.Headers.ContentDisposition =
            new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "\"mal-list\"",
                FileName = "\"list.xml\""
            };

        multipart.Add(xmlContent);

        // Deliberately omit overwrite-mal-list so the import only adds
        // entries that are not already present.
        request.Content = multipart;

        using var response =
            await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "AnimeSchedule Sync: MAL XML import for {Title} (MAL {MalId}) failed with HTTP {Status}. Body={Body}",
                anime.Title,
                anime.MalId,
                (int)response.StatusCode,
                TruncateForLog(body));
            return false;
        }

        _logger.LogInformation(
            "AnimeSchedule Sync: imported missing {Title} via MAL ID {MalId}; requesting real AnimeSchedule ETag next.",
            anime.Title,
            anime.MalId);

        return true;
    }

    private async Task<bool> IsAnimeOnListAsync(
        string route,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // AnimeSchedule returns:
        // {
        //   "userId": "...",
        //   "shows": { "<route>": { ListAnime... }, ... },
        //   "customLists": [...]
        // }
        //
        // Route keys are authoritative. Do not recursively guess membership.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ApiBase + "/animelists/oauth?limit=200&offset=0");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        using var response =
            await _httpClient.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("shows", out var shows)
            || shows.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning(
                "AnimeSchedule Sync: /animelists/oauth response did not contain the expected shows map.");
            return false;
        }

        foreach (var show in shows.EnumerateObject())
        {
            if (string.Equals(
                    show.Name,
                    route,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Defensive fallback: current API objects also contain route.
            if (show.Value.ValueKind == JsonValueKind.Object
                && show.Value.TryGetProperty("route", out var routeValue)
                && routeValue.ValueKind == JsonValueKind.String
                && string.Equals(
                    routeValue.GetString(),
                    route,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ListEntry?> GetAnimeListEntryAsync(
        string route,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ApiBase + "/animelists/oauth/" + Uri.EscapeDataString(route));

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var etag =
            response.Headers.ETag?.ToString()
            ?? (response.Headers.TryGetValues("ETag", out var etagValues)
                ? etagValues.FirstOrDefault()
                : null)
            ?? string.Empty;

        // Read the body once so diagnostics can safely explain API behavior.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation(
                "AnimeSchedule Sync: specific list GET for {Route} returned 404. ETagPresent={ETagPresent}.",
                route,
                !string.IsNullOrWhiteSpace(etag));

            if (string.IsNullOrWhiteSpace(etag))
            {
                return null;
            }

            return new ListEntry(
                0,
                "watching",
                etag);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AnimeSchedule Sync: specific list GET for {Route} returned HTTP {Status}. Body={Body}",
                route,
                (int)response.StatusCode,
                TruncateForLog(body));
            return null;
        }

        if (string.IsNullOrWhiteSpace(etag))
        {
            var headerNames = string.Join(
                ",",
                response.Headers.Select(h => h.Key));

            _logger.LogWarning(
                "AnimeSchedule Sync: specific list GET for {Route} returned HTTP {Status} but NO ETag. Headers=[{Headers}] Body={Body}",
                route,
                (int)response.StatusCode,
                headerNames,
                TruncateForLog(body));

            // Do not crash the watched-state event. The official API requires
            // a valid ETag for PUT, so there is no safe write we can make.
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new ListEntry(
            GetInt(root, "episodesSeen"),
            GetString(root, "listStatus") ?? "watching",
            etag);
    }

    private static string TruncateForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        const int max = 800;
        return value.Length <= max
            ? value
            : value[..max] + "...";
    }

    private async Task UpdateAnimeListEntryAsync(
        string route,
        int episodesSeen,
        string listStatus,
        string etag,
        string accessToken,
        CancellationToken cancellationToken,
        bool isNewEntry = false)
    {
        var payload = isNewEntry
            ? JsonSerializer.Serialize(new
            {
                episodesSeen,
                listStatus,
                startDate = DateTime.UtcNow.ToString("O")
            })
            : JsonSerializer.Serialize(new
            {
                episodesSeen,
                listStatus
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            ApiBase + "/animelists/oauth/" + Uri.EscapeDataString(route))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("ETag", etag);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetValidAccessTokenAsync(
        AnimeConnection connection,
        CancellationToken cancellationToken)
    {
        var config = RequireConfiguration();

        if (!string.IsNullOrWhiteSpace(connection.AccessToken)
            && connection.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return connection.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            return connection.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            config = RequireConfiguration();

            if (!string.IsNullOrWhiteSpace(connection.AccessToken)
                && connection.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(2))
            {
                return connection.AccessToken;
            }

            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = connection.RefreshToken
            };

            using var request = CreateOAuthTokenRequest(fields, config);

            var response = await _httpClient.SendAsync(
                request,
                cancellationToken);

            await StoreTokenResponseAsync(connection, response, cancellationToken);

            return connection.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static JsonElement? FindFirstObjectWithRoute(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("route", out _))
            {
                return element.Clone();
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindFirstObjectWithRoute(property.Value);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindFirstObjectWithRoute(item);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private async Task StoreTokenResponseAsync(
        AnimeConnection connection,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AnimeSchedule token endpoint returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "AnimeSchedule token response did not include access_token.");
        }

        
        connection.AccessToken = accessToken;

        var refreshToken = GetString(root, "refresh_token");
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            connection.RefreshToken = refreshToken;
        }

        var expiresIn = GetInt(root, "expires_in");
        connection.AccessTokenExpiresUtc =
            DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600);

        Plugin.Instance!.SaveConfiguration();
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n))
        {
            return n;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out n))
        {
            return n;
        }

        return 0;
    }

    private static Configuration.PluginConfiguration RequireConfiguration() =>
        Plugin.Instance?.Configuration
        ?? throw new InvalidOperationException(
            "AnimeSchedule Sync plugin configuration is unavailable.");

    public sealed record AnimeMatchInfo(
        string Route,
        string Title,
        int Episodes,
        string? MalId);

    public sealed record AnimeSearchResult(
        string Route,
        string Title,
        int Episodes,
        string? MalId);

    private readonly record struct AnimeMatch(
        string Route,
        string Title,
        int Episodes,
        string? MalId);

    private readonly record struct ProviderQuery(
        string Provider,
        string ProviderId,
        string Query);

    private readonly record struct ListEntry(
        int EpisodesSeen,
        string ListStatus,
        string ETag);
}
