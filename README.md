# AnimeSchedule Sync for Jellyfin

Sync watched anime progress from Jellyfin to AnimeSchedule.net. Each Jellyfin user can use their own **Personal** account, the **Server / Shared** account, or **None**. New users default to None.

**Requires Jellyfin 12 and .NET 10.** This is a community plugin, not an official Jellyfin or AnimeSchedule product.

## Install from Jellyfin

1. Open **Dashboard → Plugins → Repositories → Add** in the Jellyfin web app.
2. Enter **AnimeSchedule Sync** as the name and this repository URL:

```text
https://raw.githubusercontent.com/gohun04/jellyfin-plugin-animeschedule-sync/main/manifest.json
```

3. Save, open the plugin **Catalogue**, select **AnimeSchedule Sync**, and install.
4. Restart Jellyfin and open the plugin settings. Supply your own AnimeSchedule application ID, client secret, application token and redirect URI. Register the same redirect URI in AnimeSchedule; it must point to your Jellyfin server's `/AnimeSchedule/authCallback` endpoint, including any server base path.
5. Open **AnimeSchedule Connections** to connect accounts and select sync modes.

Do not upload your Jellyfin configuration XML, OAuth tokens or application secrets to this repository or issues. They are configured privately on your server.

## Multi-user behavior

- **Personal:** uses the selected Jellyfin user's own AnimeSchedule connection.
- **Server / Shared:** uses the one server account connected by an administrator.
- **None:** no anime synchronization for that user.
- Administrators decide whether users may change their own mode. Non-admins can never change someone else's assignment.
- Existing single-user installations migrate their configured user and credentials to Personal. Other users remain None.

**Shared-account warning:** everyone using Server / Shared contributes to the same external anime profile. One person's playback or rewatch may overwrite another's progress.

Season matching, manual mappings, auto-add, rewatch support, bulk sync and retries are retained. See [configuration and migration details](AnimeScheduleSync/README.md).

## Build and test

Install the .NET 10 SDK and ASP.NET Core 10 runtime, then run:

```sh
dotnet build AnimeScheduleSync -c Release
dotnet run --project AnimeScheduleSync.Tests
python3 scripts/package.py
python3 scripts/validate_catalogue.py
```

The package script produces a versioned ZIP in `packages/`, plus `manifest.json` and SHA-256 checksums. Catalogue ZIPs contain the DLL and metadata directly at their root, as Jellyfin expects. Commit the ZIP and manifest together when publishing a new version. Never replace an already published version's ZIP; increase the version first.

The regression suite uses simulated Jellyfin services and HTTP responses. No real anime account is contacted. Live installation and OAuth still need deployment testing.

## Release 0.5.1

First catalogue-ready distribution: per-user connection modes, guarded OAuth/permissions, safe migration, user-scoped retries, and a blank redirect URI for new installations. Existing saved redirect URIs are preserved.

Source is public for review; a redistribution license has not yet been selected.
