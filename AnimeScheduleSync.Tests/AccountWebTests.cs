using System.Net;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.AnimeScheduleSync;
using Jellyfin.Plugin.AnimeScheduleSync.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class AccountWebTests
{
    public static async Task Run(IUserManager users, AnimeScheduleClient client, AnimeScheduleRuntimeState runtime,
        Guid alice, Guid bob, Action<bool, string> check, bool serve)
    {
        FixtureAuthentication.Alice = alice;
        FixtureAuthentication.Bob = bob;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(serve ? "http://127.0.0.1:8766" : "http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("RequiresElevation", policy => policy.RequireAuthenticatedUser().RequireRole("Administrator")));
        builder.Services.AddControllers().AddApplicationPart(typeof(Plugin).Assembly);
        await using var app = builder.Build();
        app.UsePathBase("/media");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        // Simulated Jellyfin login for browser testing. Never included in the plugin assembly.
        app.MapPost("/Users/AuthenticateByName", async (HttpRequest request) => {
            var body = await request.ReadFromJsonAsync<JsonElement>();
            var username = body.GetProperty("Username").GetString();
            if (body.GetProperty("Pw").GetString() != "test-password" || username is not ("Bob" or "Alice"))
                return Results.Unauthorized();
            return Results.Json(new Dictionary<string, string> { ["AccessToken"] = username == "Bob" ? "fixture-bob" : "fixture-alice" });
        });
        app.MapPost("/Sessions/Logout", () => Results.NoContent()).RequireAuthorization();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address + "/media/") };
        var shell = await http.GetAsync("AnimeSchedule/account");
        var html = await shell.Content.ReadAsStringAsync();
        check(shell.StatusCode == HttpStatusCode.OK && html.Contains("/media/AnimeSchedule/account.js"), "standalone sign-in shell works anonymously under a server base path");
        check(shell.Headers.CacheControl?.NoStore == true && shell.Headers.Contains("Content-Security-Policy"), "standalone shell has no-store and content security headers");
        check((await http.GetAsync("AnimeSchedule/account.js")).StatusCode == HttpStatusCode.OK
            && (await http.GetAsync("AnimeSchedule/account.css")).StatusCode == HttpStatusCode.OK, "standalone script and stylesheet are publicly loadable");
        check((await http.GetAsync("AnimeSchedule/connections/me")).StatusCode == HttpStatusCode.Unauthorized, "real HTTP middleware denies anonymous account-data requests");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"fixture-bob\"");
        var self = await http.GetFromJsonAsync<JsonElement>("AnimeSchedule/connections/me");
        check(self.GetProperty("users").GetArrayLength() == 1 && self.GetProperty("users")[0].GetProperty("name").GetString() == "Bob", "regular user can open real self-service API and sees only their own row");
        check((await http.PostAsJsonAsync("AnimeSchedule/connections/policy", new { allowUsersToChangeSyncMode = true })).StatusCode == HttpStatusCode.Forbidden, "HTTP middleware denies regular-user policy changes");
        check((await http.PostAsJsonAsync($"AnimeSchedule/connections/{alice}/mode", new { mode = "Personal" })).StatusCode == HttpStatusCode.Forbidden, "HTTP endpoint denies cross-user mode changes");
        Plugin.Instance!.Configuration.AllowUsersToChangeSyncMode = true;
        check((await http.PostAsJsonAsync($"AnimeSchedule/connections/{bob}/mode", new { mode = "Personal" })).IsSuccessStatusCode, "regular user can save their mode through HTTP when permitted");
        Plugin.Instance.Configuration.AllowUsersToChangeSyncMode = false;
        check((await http.PostAsJsonAsync($"AnimeSchedule/connections/{bob}/mode", new { mode = "None" })).StatusCode == HttpStatusCode.Forbidden, "locked self-service mode is denied through HTTP");
        http.DefaultRequestHeaders.Remove("Authorization");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"fixture-alice\"");
        var ownAdmin = await http.GetFromJsonAsync<JsonElement>("AnimeSchedule/connections/me");
        check(ownAdmin.GetProperty("users").GetArrayLength() == 1 && ownAdmin.GetProperty("users")[0].GetProperty("name").GetString() == "Alice", "self-service route shows only the caller even for an administrator");
        Plugin.Instance.Configuration.AllowUsersToChangeSyncMode = true;
        if (serve)
        {
            Console.WriteLine("Browser fixture: " + address + "/media/AnimeSchedule/account (Bob / test-password)");
            await app.WaitForShutdownAsync();
        }
        else await app.StopAsync();
    }
}

public sealed class FixtureAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public static Guid Alice;
    public static Guid Bob;
    public FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var admin = header.Contains("Token=\"fixture-alice\"", StringComparison.Ordinal);
        var regular = header.Contains("Token=\"fixture-bob\"", StringComparison.Ordinal);
        if (!admin && !regular) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(new[] {
            new Claim("Jellyfin-UserId", (admin ? Alice : Bob).ToString()),
            new Claim(ClaimTypes.Role, admin ? "Administrator" : "User")
        }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
