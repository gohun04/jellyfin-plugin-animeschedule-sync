using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Serialization;
using System.Net;
using Jellyfin.Plugin.AnimeScheduleSync;
using Jellyfin.Plugin.AnimeScheduleSync.Api;
using Jellyfin.Plugin.AnimeScheduleSync.Configuration;
using Jellyfin.Plugin.AnimeScheduleSync.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

var passed = 0;
void Check(bool result, string description)
{
    if (!result) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description); passed++;
}
var alice = Guid.NewGuid(); var bob = Guid.NewGuid(); var guest = Guid.NewGuid();
var legacy = new PluginConfiguration { JellyfinUserId = alice.ToString(), AccessToken = "legacy", RefreshToken = "refresh", PendingOAuthState = "old-state" };
Check(legacy.MigrateConnections(), "legacy migration runs once");
Check(legacy.FindUser(alice)?.Mode == SyncMode.Personal && legacy.ResolveConnection(alice)?.AccessToken == "legacy", "existing user retains personal credentials");
Check(legacy.ResolveConnection(bob) is null && legacy.AccessToken == "" && legacy.PendingOAuthState == "", "migration does not enroll other users and clears legacy secrets/state");
Check(!legacy.MigrateConnections(), "migration is idempotent");
var orphan = new PluginConfiguration { JellyfinUserId = "invalid", AccessToken = "orphan" };
orphan.MigrateConnections();
Check(orphan.ServerConnection.AccessToken == "orphan" && orphan.ResolveConnection(alice) is null, "orphan credentials retained without enrolling users");
var xml = new XmlSerializer(typeof(PluginConfiguration));
using var sw = new StringWriter(); xml.Serialize(sw, legacy);
var restored = (PluginConfiguration)xml.Deserialize(new StringReader(sw.ToString()))!;
Check(restored.ResolveConnection(alice)?.AccessToken == "legacy" && restored.SyncKey(alice) == legacy.SyncKey(alice), "connections and identities survive XML round trip");
Check(!ConnectionsController.MayChangeMode(false, alice, alice, false), "self mode changes denied when disabled");
Check(ConnectionsController.MayChangeMode(false, alice, alice, true), "self mode changes allowed when enabled");
Check(!ConnectionsController.MayChangeMode(false, alice, bob, true), "users cannot change another user's mode");
Check(ConnectionsController.MayChangeMode(true, alice, bob, false), "administrators may change assignments");
Check(typeof(AnimeScheduleController).GetCustomAttribute<AuthorizeAttribute>()?.Policy == "RequiresElevation", "legacy management endpoints require administrator authorization");
Check(typeof(ConnectionsController).GetCustomAttribute<AuthorizeAttribute>() is not null && typeof(ConnectionsController).GetMethod("Callback")!.GetCustomAttribute<AllowAnonymousAttribute>() is not null, "connection API requires login except state-bound OAuth callback");

var temp = Path.Combine(Path.GetTempPath(), "animeschedule-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var paths = Stub<IApplicationPaths>.Make((m,a) => m.ReturnType == typeof(string) ? temp : null);
    var serializer = Stub<IXmlSerializer>.Make((m,a) => m.Name.StartsWith("Deserialize") ? new PluginConfiguration() : null);
    var plugin = new Plugin(paths, serializer);
    var config = plugin.Configuration;
    config.ApplicationToken = "application";
    config.RedirectUri = "https://jellyfin.example/AnimeSchedule/authCallback";
    config.ClientId = "client"; config.ClientSecret = "secret";
    var people = new[] { new User("Alice", "auth", "reset") { Id = alice }, new User("Bob", "auth", "reset") { Id = bob }, new User("Guest", "auth", "reset") { Id = guest } };
    var users = Stub<IUserManager>.Make((m,a) => m.Name switch {
        "GetUserById" => people.FirstOrDefault(x => x.Id == (Guid)a![0]!),
        "GetUsers" => people,
        "GetUserDto" => new UserDto { Policy = new UserPolicy { IsAdministrator = ((User)a![0]!).Id == alice } },
        _ => null
    });
    var runtime = new AnimeScheduleRuntimeState();
    var handler = new FakeAnimeHttp();
    var client = new AnimeScheduleClient(NullLogger<AnimeScheduleClient>.Instance, runtime, users, new HttpClient(handler));
    var controller = new ConnectionsController(users, client, runtime);
    void Identity(Guid id, bool admin = false) => controller.ControllerContext = new ControllerContext {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("Jellyfin-UserId", id.ToString()), new Claim(ClaimTypes.Role, admin ? "Administrator" : "User") }, "test")) }
    };
    Identity(alice, true);
    config.UserConnections.Add(new() { UserId = alice, Mode = SyncMode.Personal, PersonalConnection = new() { AccessToken = "alice", AccessTokenExpiresUtc = DateTime.UtcNow.AddHours(1) } });
    config.UserConnections.Add(new() { UserId = bob, Mode = SyncMode.ServerShared });
    config.ServerConnection.AccessToken = "shared"; config.ServerConnection.AccessTokenExpiresUtc = DateTime.UtcNow.AddHours(1);
    Check(config.ResolveConnection(guest) is null, "new Jellyfin users default to None");
    var episode = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), SeriesName = "Test", SeasonId = Guid.NewGuid(), IndexNumber = 3, ParentIndexNumber = 1 };
    var series = new Series { Id = episode.SeriesId, Name = "Test" };
    var season = new Season { Id = episode.SeasonId, SeriesId = series.Id, IndexNumber = 1, Name = "Season 1" };
    var library = Stub<ILibraryManager>.Make((m,a) => m.Name switch {
        "GetItemById" => (Guid)a![0]! == series.Id ? series : (Guid)a[0]! == season.Id ? season : episode,
        "GetItemList" => new List<BaseItem> { episode },
        _ => null
    });
    // Jellyfin resolves Episode.Series through its static library service.
    typeof(BaseItem).GetProperty("LibraryManager", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null, library);
    config.SeasonRouteOverrides = "Test|1=test";
    Check(!await client.SyncEpisodeAsync(guest, episode, 3, default) && handler.Requests.Count == 0, "None makes no outbound requests");
    Check(await client.SyncEpisodeAsync(alice, episode, 3, default), "personal playback sync completes");
    Check(handler.Writes.Last() == "alice", "personal sync writes only with personal credentials");
    Check(await client.SyncEpisodeAsync(bob, episode, 3, default) && handler.Writes.Last() == "shared", "shared sync uses server credentials");
    var oldKey = config.SyncKey(alice)!;
    runtime.EnqueueRetry(alice, oldKey, episode, 3, "failure");
    runtime.EnqueueRetry(bob, config.SyncKey(bob)!, episode, 3, "failure");
    Check(runtime.GetAllRetries().Count == 2, "two users watching the same episode retain separate retries");
    await controller.SetMode(alice, new("None"), default);
    Check(runtime.GetAllRetries().Count == 1 && runtime.GetAllRetries()[0].UserId == bob, "mode changes clear only that user's retries");
    await controller.SetMode(alice, new("Personal"), default);
    var count = handler.Requests.Count;
    Check(!await client.SyncEpisodeAsync(alice, episode, 3, default, oldKey) && count == handler.Requests.Count, "old work cannot revive after switching away and back");
    Identity(bob);
    Check(await controller.SetMode(bob, new("None"), default) is ForbidResult, "actual API denies self mode changes while locked");
    config.AllowUsersToChangeSyncMode = true;
    Check(await controller.SetMode(alice, new("None"), default) is ForbidResult, "actual API rejects another user's assignment");
    Check(await controller.Start(null, default) is ForbidResult, "regular user cannot connect server account");
    Check(await controller.Disconnect(alice, default) is ForbidResult, "regular user cannot disconnect another personal account");
    var data = JsonSerializer.Serialize(((OkObjectResult)await controller.GetConnections(default)).Value);
    Check(data.Contains("Bob") && !data.Contains("Alice") && !data.Contains("AccessToken"), "self connection status excludes other users and credentials");
    Check(await controller.SetMode(bob, new("None"), default) is OkObjectResult, "actual API permits enabled self mode changes");
    Check(await controller.SetMode(bob, new("99"), default) is BadRequestObjectResult, "invalid modes rejected");
    count = handler.Requests.Count;
    Check(!await client.SyncEpisodeAsync(bob, episode, 3, default) && count == handler.Requests.Count, "disabled formerly shared user makes no requests");
    // OAuth starts are independent, consume state once and do not enroll users.
    var start = (OkObjectResult)await controller.Start(bob, default);
    var startData = JsonSerializer.SerializeToElement(start.Value);
    var url = new Uri(startData.GetProperty("url").GetString()!);
    var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query)["state"].ToString();
    Check(await controller.Callback("code", "wrong", null, default) is BadRequestObjectResult, "OAuth rejects incorrect state");
    Check(await controller.Callback("code", state, null, default) is ContentResult, "OAuth binds personal token exchange to its originating user");
    Check(config.FindUser(bob)!.PersonalConnection.AccessToken == "new-token" && config.FindUser(bob)!.Mode == SyncMode.None && config.ServerConnection.AccessToken == "shared", "personal OAuth does not change mode or shared credentials");
    Check(await controller.Callback("code", state, null, default) is BadRequestObjectResult, "OAuth state cannot be replayed");
    Identity(alice, true);
    var stale = new PluginConfiguration { RedirectUri = config.RedirectUri, ApplicationToken = config.ApplicationToken, ClientId = config.ClientId, ClientSecret = config.ClientSecret, SeasonRouteOverrides = config.SeasonRouteOverrides, AutoSync = false, UserConnections = new(), ServerConnection = new() };
    plugin.UpdateConfiguration(stale);
    config = plugin.Configuration;
    Check(config.FindUser(bob)!.PersonalConnection.AccessToken == "new-token" && config.ServerConnection.AccessToken == "shared" && !config.AutoSync, "settings save preserves latest connections while applying ordinary settings");
    // Refresh updates the selected token only.
    config.FindUser(alice)!.PersonalConnection.AccessTokenExpiresUtc = DateTime.MinValue;
    config.FindUser(alice)!.PersonalConnection.RefreshToken = "alice-refresh";
    await client.SyncEpisodeAsync(alice, episode, 3, default);
    Check(config.FindUser(alice)!.PersonalConnection.AccessToken == "new-token" && config.ServerConnection.AccessToken == "shared", "token refresh is isolated to selected account");
    // Background retries re-resolve current assignments and never redirect old work.
    var userData = Stub<IUserDataManager>.Make((m,a) => null);
    var management = new AnimeScheduleManagementService(library, users, userData, client, runtime);
    var retries = new AnimeScheduleRetryService(runtime, management, client, NullLogger<AnimeScheduleRetryService>.Instance);
    runtime.EnqueueRetry(bob, "obsolete-account", episode, 3, "failure");
    count = handler.Requests.Count;
    await retries.ProcessAllRetriesAsync(default);
    Check(count == handler.Requests.Count && runtime.GetAllRetries().Count == 0, "retry worker drops disabled or stale-account work without network calls");
    runtime.EnqueueRetry(alice, config.SyncKey(alice)!, episode, 3, "failure");
    count = handler.Requests.Count;
    await retries.ProcessDueRetriesAsync(default);
    Check(count == handler.Requests.Count && runtime.GetAllRetries().Count == 1, "background retries respect global AutoSync off");
    await retries.ProcessAllRetriesAsync(default);
    Check(runtime.GetAllRetries().Count == 0 && handler.Writes.Last() == "new-token", "manual retry uses the original user's current matching account");
    // A mode change waits for already-started external writes; subsequent work is disabled.
    handler.WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    handler.ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var inFlight = client.SyncEpisodeAsync(alice, episode, 3, default);
    await handler.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var disable = controller.SetMode(alice, new("None"), default);
    Check(!disable.IsCompleted, "mode change waits for an in-flight account write");
    handler.ReleaseWrite.SetResult();
    await inFlight; await disable;
    count = handler.Requests.Count;
    Check(!await client.SyncEpisodeAsync(alice, episode, 3, default) && count == handler.Requests.Count, "no further sync occurs after disabling completes");
    Console.WriteLine($"All {passed} checks passed. No live service was contacted.");
}
finally { Directory.Delete(temp, true); }

public class Stub<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    public static T Make(Func<MethodInfo, object?[]?, object?> handler) { var proxy = Create<T, Stub<T>>(); ((Stub<T>)(object)proxy).Handler = handler; return proxy; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
public sealed class FakeAnimeHttp : HttpMessageHandler
{
    public List<string> Requests { get; } = new();
    public List<string> Writes { get; } = new();
    public TaskCompletionSource? WriteEntered;
    public TaskCompletionSource? ReleaseWrite;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.AbsolutePath);
        if (request.Method == HttpMethod.Put)
        {
            Writes.Add(request.Headers.Authorization!.Parameter!);
            WriteEntered?.TrySetResult();
            if (ReleaseWrite is not null) await ReleaseWrite.Task.WaitAsync(cancellationToken);
        }
        var path = request.RequestUri.AbsolutePath;
        var json = path.EndsWith("/oauth2/token") ? "{\"access_token\":\"new-token\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}"
            : path.Contains("/anime/") ? "{\"route\":\"test\",\"title\":\"Test\",\"episodes\":12}"
            : path.EndsWith("/animelists/oauth") ? "{\"shows\":{\"test\":{\"route\":\"test\"}}}"
            : "{\"episodesSeen\":0,\"listStatus\":\"watching\"}";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test-etag\"");
        return response;
    }
}
