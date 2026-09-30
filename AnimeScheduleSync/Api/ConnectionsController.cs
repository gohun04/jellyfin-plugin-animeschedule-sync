using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Jellyfin.Plugin.AnimeScheduleSync.Configuration;
using Jellyfin.Plugin.AnimeScheduleSync.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AnimeScheduleSync.Api;

[ApiController]
[Authorize]
[Route("AnimeSchedule")]
public sealed class ConnectionsController : ControllerBase
{
    private readonly IUserManager _users;
    private readonly AnimeScheduleClient _client;
    private readonly AnimeScheduleRuntimeState _runtime;
    private static readonly ConcurrentDictionary<string, PendingConnection> Pending = new();
    private static PluginConfiguration Config => Plugin.Instance!.Configuration;
    private bool Admin => User.IsInRole("Administrator");
    private Guid Caller => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    public ConnectionsController(IUserManager users, AnimeScheduleClient client, AnimeScheduleRuntimeState runtime)
    {
        _users = users;
        _client = client;
        _runtime = runtime;
    }

    internal static bool MayChangeMode(bool admin, Guid caller, Guid target, bool allowSelf) =>
        admin || (allowSelf && caller != Guid.Empty && caller == target);

    private bool MayManage(Guid id) => Admin || (Caller != Guid.Empty && Caller == id);
    private bool Exists(Guid id) => id != Guid.Empty && _users.GetUserById(id) is not null;

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections(CancellationToken ct)
    {
        await AnimeScheduleClient.ConnectionGate.WaitAsync(ct);
        try
        {
            if (!Admin && !Exists(Caller)) return Forbid();
            var users = _users.GetUsers().Where(x => Admin || x.Id == Caller).Select(x =>
            {
                var assignment = Config.FindUser(x.Id);
                var mode = assignment?.Mode ?? SyncMode.None;
                var connection = Config.ResolveConnection(x.Id);
                var connected = !string.IsNullOrWhiteSpace(connection?.AccessToken);
                return new
                {
                    userId = x.Id, name = x.Username, mode = mode.ToString(), connected,
                    personalConnected = !string.IsNullOrWhiteSpace(assignment?.PersonalConnection.AccessToken),
                    status = mode == SyncMode.None ? "Sync disabled" : connected
                        ? mode == SyncMode.ServerShared ? "Using connected server account" : "Personal account connected"
                        : mode == SyncMode.ServerShared ? "Server account not connected" : "Personal account not connected"
                };
            }).ToArray();
            return Ok(new
            {
                isAdmin = Admin, allowUsersToChangeSyncMode = Config.AllowUsersToChangeSyncMode,
                serverConnected = !string.IsNullOrWhiteSpace(Config.ServerConnection.AccessToken), users
            });
        }
        finally { AnimeScheduleClient.ConnectionGate.Release(); }
    }

    [HttpPost("connections/{userId:guid}/mode")]
    public async Task<IActionResult> SetMode(Guid userId, [FromBody] ModeRequest request, CancellationToken ct)
    {
        await AnimeScheduleClient.ConnectionGate.WaitAsync(ct);
        try
        {
            if (!MayChangeMode(Admin, Caller, userId, Config.AllowUsersToChangeSyncMode)) return Forbid();
            if (!Exists(userId)) return NotFound();
            if (!Enum.TryParse<SyncMode>(request.Mode, out var mode) || !Enum.IsDefined(mode))
                return BadRequest("Choose None, Personal, or ServerShared.");
            var assignment = Config.FindUser(userId);
            if (assignment is null)
            {
                assignment = new UserConnection { UserId = userId };
                Config.UserConnections.Add(assignment);
            }
            if (assignment.Mode != mode)
            {
                _runtime.ClearUserRetries(userId);
                assignment.Revision = Guid.NewGuid().ToString("N");
            }
            assignment.Mode = mode;
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { saved = true });
        }
        finally { AnimeScheduleClient.ConnectionGate.Release(); }
    }

    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("connections/policy")]
    public async Task<IActionResult> SetPolicy([FromBody] PolicyRequest request, CancellationToken ct)
    {
        await AnimeScheduleClient.ConnectionGate.WaitAsync(ct);
        try
        {
            Config.AllowUsersToChangeSyncMode = request.AllowUsersToChangeSyncMode;
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { saved = true });
        }
        finally { AnimeScheduleClient.ConnectionGate.Release(); }
    }

    // A null target is the server connection. Personal OAuth never changes sync mode.
    [HttpPost("connections/authStart")]
    public async Task<IActionResult> Start([FromQuery] Guid? userId, CancellationToken ct)
    {
        await AnimeScheduleClient.ConnectionGate.WaitAsync(ct);
        try
        {
            if (userId is null ? !Admin : !MayManage(userId.Value)) return Forbid();
            if (userId.HasValue && !Exists(userId.Value)) return NotFound();
            if (!Exists(Caller)) return Forbid();
            var config = Config;
            if (string.IsNullOrWhiteSpace(config.ClientId) || string.IsNullOrWhiteSpace(config.ClientSecret)
                || string.IsNullOrWhiteSpace(config.ApplicationToken) || string.IsNullOrWhiteSpace(config.RedirectUri))
                return BadRequest("Ask an administrator to save the AnimeSchedule application settings first.");
            foreach (var item in Pending.Where(x => x.Value.ExpiresUtc < DateTime.UtcNow || x.Value.UserId == userId))
                Pending.TryRemove(item.Key, out _);
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            Pending[state] = new(userId, Caller, Admin, verifier, DateTime.UtcNow.AddMinutes(10));
            var url = "https://animeschedule.net/api/v3/oauth2/authorize?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(config.ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(config.RedirectUri)
                + "&scope=animelist&state=" + Uri.EscapeDataString(state)
                + "&code_challenge=" + Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                + "&code_challenge_method=S256";
            return Ok(new { url });
        }
        finally { AnimeScheduleClient.ConnectionGate.Release(); }
    }

    [AllowAnonymous]
    [HttpGet("authCallback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, CancellationToken ct)
    {
        await AnimeScheduleClient.ConnectionGate.WaitAsync(ct);
        try
        {
            // Consume exactly once, including errors. Random state is bound to its target and expires.
            if (string.IsNullOrWhiteSpace(state) || !Pending.TryRemove(state, out var pending)
                || pending.ExpiresUtc < DateTime.UtcNow)
                return BadRequest("This connection attempt expired or was already used. Connect again.");
            if (pending.UserId.HasValue && !Exists(pending.UserId.Value)) return BadRequest("User no longer exists.");
            if (pending.WasAdmin)
            {
                var initiator = pending.Initiator == Guid.Empty ? null : _users.GetUserById(pending.Initiator);
                if (initiator is null || !_users.GetUserDto(initiator).Policy.IsAdministrator)
                    return Forbid();
            }
            else if (pending.UserId != pending.Initiator || !Exists(pending.Initiator)) return Forbid();
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
                return BadRequest("AnimeSchedule did not approve the connection. Connect again to retry.");
            var connection = new AnimeConnection();
            try { await _client.ExchangeAuthorizationCodeAsync(connection, code, pending.Verifier, ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Problem("AnimeSchedule token exchange failed. Check the application settings and connect again.");
            }
            if (pending.UserId is Guid id)
            {
                var assignment = Config.FindUser(id);
                if (assignment is null)
                {
                    assignment = new UserConnection { UserId = id };
                    Config.UserConnections.Add(assignment);
                }
                assignment.PersonalConnection = connection;
                _runtime.ClearUserRetries(id);
            }
            else Config.ServerConnection = connection;
            Plugin.Instance!.SaveConfiguration();
            return Content("AnimeSchedule connected. Return to Jellyfin and refresh the Connections page. Your sync mode has not changed.", "text/plain");
        }
        finally { AnimeScheduleClient.ConnectionGate.Release(); }
    }

    [HttpPost("connections/disconnect")]
    public async Task<IActionResult> Disconnect([FromQuery] Guid? userId, CancellationToken ct)
    {
        await AnimeScheduleClient.ConnectionGate.WaitAsync(ct);
        try
        {
            if (userId is null ? !Admin : !MayManage(userId.Value)) return Forbid();
            if (userId.HasValue && !Exists(userId.Value)) return NotFound();
            foreach (var item in Pending.Where(x => x.Value.UserId == userId)) Pending.TryRemove(item.Key, out _);
            if (userId is Guid id)
            {
                var assignment = Config.FindUser(id);
                if (assignment is not null) assignment.PersonalConnection = new();
                _runtime.ClearUserRetries(id);
            }
            else Config.ServerConnection = new();
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { disconnected = true });
        }
        finally { AnimeScheduleClient.ConnectionGate.Release(); }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record PendingConnection(Guid? UserId, Guid Initiator, bool WasAdmin, string Verifier, DateTime ExpiresUtc);
    public sealed record ModeRequest(string Mode);
    public sealed record PolicyRequest(bool AllowUsersToChangeSyncMode);
}
