using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AnimeScheduleSync.Api;

/// <summary>Standalone sign-in shell; all account data stays behind authenticated APIs.</summary>
[ApiController]
[AllowAnonymous]
[Route("AnimeSchedule")]
public sealed class AccountPageController : ControllerBase
{
    [HttpGet("account")]
    public IActionResult Account()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.AnimeScheduleSync.Configuration.account.html")!;
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd().Replace("__BASE_PATH__", WebUtility.HtmlEncode(Request.PathBase.Value ?? string.Empty)), "text/html");
    }

    [HttpGet("account.js")]
    public IActionResult Script() => Asset("account.js", "text/javascript");

    [HttpGet("account.css")]
    public IActionResult Styles() => Asset("account.css", "text/css");

    private IActionResult Asset(string name, string contentType)
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(typeof(Plugin).Assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.AnimeScheduleSync.Configuration." + name)!, contentType);
    }
}
