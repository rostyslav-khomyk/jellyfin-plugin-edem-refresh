# Edem Playlist & EPG Refresh

Jellyfin 12.1 / .NET 10 plugin. Downloads an original M3U and XMLTV feed, adds `group-title` from `#EXTGRP`, matches channel names to unambiguous EPG IDs, cleans schedules, and queues Jellyfin's native guide refresh. Works alongside Live TV Categories.

## Install

Add this repository in Dashboard → Plugins → Manage Repositories:

`https://github.com/rostyslav-khomyk/jellyfin-plugin-edem-refresh/releases/latest/download/manifest.json`

Install **Edem Playlist & EPG Refresh**, then restart Jellyfin.

Open plugin settings. Enter your original provider M3U URL and XMLTV URL, plus the IDs of your existing M3U tuner and XMLTV provider. The IDs appear in their dashboard edit URLs. Save and run **Update Edem playlist and EPG** in Scheduled Tasks. The selected sources are switched to locally generated files only after successful processing. Other tuners/providers are untouched.

The task defaults to daily at 03:00 and server startup. Adjust triggers in Scheduled Tasks. Wait for the native guide refresh to finish before running again. If the guide refresh is already running, the update refuses to switch its inputs.

## Data handling

Provider URLs are stored only in Jellyfin's local plugin configuration, which may contain credentials. Do not publish that configuration. Stream URLs remain unchanged in generated files. Downloads accept HTTP/HTTPS and follow redirects; use HTTPS when your provider supports it.

Each update builds an immutable generation inside Jellyfin's data folder. Both output paths change in one Live TV configuration save, keeping a previous generation for rollback. Download/parse failures before publication retain the previous inputs. No external hosting, Python runtime, API key, proxy, or DSM task is needed on the NAS.

Name matching is Unicode-normalized, case-insensitive exact matching. Ambiguous/unmatched channels are retained without an invented EPG ID; valid existing IDs are preserved. The cleaner removes explicit missing-programme placeholders and expired schedules, keeps seven days, and adds English category labels. Invalid individual timestamps/durations are skipped and counted; empty playlists or empty current guides are rejected. XML external entities are disabled. XML processing streams programmes instead of loading the full guide into memory.

The plugin does not generate schedules for channels absent from the provider EPG, add category browsing itself, or repair Jellyfin's Guide grid rendering.

## Build and verify

```sh
dotnet run --project checks/Checks.csproj -c Release
dotnet build EdemRefresh.csproj -c Release
python3 package.py
```

Optional real-source check (local files only; never commit provider playlists):

```sh
dotnet run --project checks/Checks.csproj -c Release -- /path/to/provider.m3u /path/to/guide.xml
```

The checks cover group conversion, ambiguity, existing IDs, stream preservation, quoted commas, categories, expiry, placeholder filtering, invalid inputs and XML external entity rejection.

## Rollback

Disable the plugin's scheduled task and restore the original tuner/guide URLs in Dashboard → Live TV. Uninstall and restart to remove the plugin. Generated files stay in the `edem-refresh` data folder for recovery.
