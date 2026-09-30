# AnimeSchedule Sync — v0.5.1 for Jellyfin 12

Continues the v0.4.3 plugin. This release adds multi-user connections while retaining season matching, manual mappings, MAL XML auto-add, progress/status updates, rewatch support, bulk sync and retry tools.

## Install and migrate

1. Back up your current plugin folder and AnimeSchedule plugin configuration XML. Stop Jellyfin.
2. Replace the old AnimeSchedule plugin installation with the `AnimeSchedule Sync_0.5.1.0` folder from a manual build. Keep only one installed version of this plugin. Keep the existing configuration XML.
3. Start Jellyfin and open **AnimeSchedule Connections** from the web sidebar, or use the link in the plugin settings.
4. Confirm the migrated user's mode and connection status. Configure the shared account under **Server Connection** if desired, then assign users individually.

On first load, the old configured Jellyfin user becomes **Personal** and retains the existing OAuth access token, refresh token and expiry. No other user is enrolled. If the legacy user ID is missing or invalid, existing credentials are retained as an unassigned server connection; all users remain **None** until an administrator assigns them. Migration runs once and clears the old token fields. An OAuth attempt started before upgrading must be restarted. Unknown/new Jellyfin users always resolve to **None**, even when a server account is connected.

The plugin ID, callback route (`/AnimeSchedule/authCallback`) and AnimeSchedule application settings are unchanged. No new AnimeSchedule application is required. For rollback, restore the backed-up old configuration as well as the old plugin: the old version cannot read the new connection collection.

## Connections and permissions

- **Personal:** uses that Jellyfin user's own OAuth account. Connect or reconnect beside their name. Connecting does not change their mode.
- **Server / Shared:** inherits the single account connected by an administrator under Server Connection. Personal credentials, if retained, are inactive in this mode.
- **None:** no playback, bulk, seasonal or queued retry synchronization for that user. Stored personal credentials are retained until disconnected.
- **Allow users to change their own sync mode:** off by default. When off, only administrators can change assignments. Users can still connect/disconnect their own personal credentials. When on, users can change only their own assignment.

The Connections page shows current Jellyfin user names, selected modes and connection status. Non-admins see only their own row. Status reports stored OAuth connection availability; it does not claim to have verified the external account's display name or current token validity. The management dashboard and mapping/manual sync APIs require an administrator. Select a Jellyfin user in the manager before using bulk/season tools.

**Shared account warning:** every assigned user's playback contributes to one external anime profile. Progress may overwrite another user's progress, particularly when rewatch mode resets a completed show. The existing Never Decrease Progress option does not prevent a rewatch reset. Shared operations are serialized to avoid concurrent read/write races.

## Runtime behavior

All sync operations resolve an explicit Jellyfin user. None, absent users, deleted users and stale destinations are rejected before outgoing sync requests. Each retry carries the user, connection identity and assignment revision; changing modes, reconnecting or disconnecting prevents old work from being redirected or revived. History entries include the originating user ID. Background retries also respect global AutoSync; an administrator can still explicitly run manual retries when AutoSync is off.

Saving a mode or disconnecting waits for an already-started sync operation to finish. Once the save completes, subsequent work uses the new assignment. A request already sent to AnimeSchedule cannot be undone. An ordinary settings save preserves the newest tokens and assignments instead of replacing them with an older browser snapshot.

OAuth uses one-time, target-bound PKCE attempts that expire after ten minutes. Separate users can authorize independently. Attempts are held in memory and must be restarted after Jellyfin restarts. Tokens remain in Jellyfin's plugin configuration XML, as in the original plugin; self-service endpoints never return them.

## Changed files

| File | Change |
| --- | --- |
| `Configuration/PluginConfiguration.cs` | Modes, per-user and server credentials, assignment revisions, one-time migration |
| `Plugin.cs` | Migration at startup, Connections menu, preservation of connection state on settings saves |
| `Api/ConnectionsController.cs` | User-scoped status, mode permission enforcement, shared/personal OAuth, disconnect |
| `Api/AnimeScheduleController.cs` | Admin-only management, explicit user selection, user-aware history/status |
| `Services/AnimeScheduleClient.cs` | Explicit account resolution, isolated token refresh, serialized sync, stale-work guards |
| `Services/AnimeScheduleSyncService.cs` | Playback routes by event user |
| `Services/AnimeScheduleManagementService.cs` | Manual/bulk sync uses the selected user's watched state and account |
| `Services/AnimeScheduleRuntimeState.cs` | User/account-specific retry keys and user-tagged history |
| `Services/AnimeScheduleRetryService.cs` | Rechecks assignment before retries; respects AutoSync |
| `Configuration/connections.html` | Admin and self-service connection controls, user statuses and shared warning |
| `Configuration/config.html`, `Configuration/management.html` | Connections links, explicit manual-sync user selection |
| Project file, `Properties/AssemblyInfo.cs`, `meta.json`, `build.fish` | New embedded page, test access, v0.5.1 packaging |
| `../AnimeScheduleSync.Tests/` | Executable regression checks with simulated Jellyfin dependencies and HTTP responses |

## Build and test

Requires .NET 10 SDK; running tests also requires the ASP.NET Core 10 runtime. Jellyfin packages remain pinned to 12.0.0.

```sh
dotnet build AnimeScheduleSync/Jellyfin.Plugin.AnimeScheduleSync.csproj -c Release
dotnet run --project AnimeScheduleSync.Tests/AnimeScheduleSync.Tests.csproj
```

From inside the plugin folder, `fish build.fish` builds the installable folder. Only the plugin DLL and meta.json belong in the Jellyfin plugin folder; do not copy the test dependencies.

Validation performed: Release build and 39 regression checks passed; JavaScript syntax checked for all three pages; Connections page inspected in a browser using simulated admin and regular-user responses. Tests cover migration/XML persistence, None, personal/shared routing, permission enforcement, token isolation, OAuth state/replay, retry isolation and a mode change during an active write. Existing XML-documentation warnings remain. No live Jellyfin server or AnimeSchedule account was used, so real OAuth approval and deployment still need a smoke test after installation.

Authorization integration follows Jellyfin's [user ID claim](https://github.com/jellyfin/jellyfin/blob/master/Jellyfin.Api/Constants/InternalClaimTypes.cs), [roles](https://github.com/jellyfin/jellyfin/blob/master/Jellyfin.Api/Constants/UserRoles.cs) and `RequiresElevation` policy; the build targets the locally available Jellyfin 12 packages.
