using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EdemRefresh;

public class Configuration : BasePluginConfiguration
{
    public string PlaylistUrl { get; set; } = "";
    public string EpgUrl { get; set; } = "http://cdn.epg.one/edem.xml.gz";
    public string TunerId { get; set; } = "";
    public string GuideId { get; set; } = "";
    public string LastResult { get; set; } = "Not run yet";
    public string OutputDirectory { get; set; } = "";
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        Configuration.OutputDirectory = Path.Combine(paths.DataPath, "edem-refresh");
    }
    public override string Name => "Edem Playlist & EPG Refresh";
    public override Guid Id => Guid.Parse("0b61a5e2-d37d-4e6a-9f83-fc7818e3d82a");
    public override string Description => "Updates matched M3U groups and cleaned XMLTV together, then refreshes the guide.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "EdemRefresh", EmbeddedResourcePath = "Jellyfin.Plugin.EdemRefresh.config.html" }];
}

public sealed class RefreshTask(IServerConfigurationManager server, ITaskManager tasks, ILogger<RefreshTask> logger) : IScheduledTask
{
    public string Name => "Update Edem playlist and EPG";
    public string Key => "EdemRefresh";
    public string Description => "Download, match and clean both sources; keep previous files on failure, then refresh the Jellyfin guide.";
    public string Category => "Live TV";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }, new() { Type = TaskTriggerInfoType.StartupTrigger }];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var c = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(c.PlaylistUrl)) return;
        var live = server.GetConfiguration<LiveTvOptions>("livetv");
        var tuner = live.TunerHosts.SingleOrDefault(t => t.Id == c.TunerId && t.Type == "m3u") ?? throw new InvalidOperationException("Select an existing M3U tuner ID in plugin settings.");
        var guide = live.ListingProviders.SingleOrDefault(g => g.Id == c.GuideId && string.Equals(g.Type, "xmltv", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Select an existing XMLTV provider ID in plugin settings.");
        var guideTask = tasks.ScheduledTasks.SingleOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
        if (guideTask is null) throw new InvalidOperationException("Jellyfin guide refresh task was not found.");
        if (guideTask.State == TaskState.Running) throw new InvalidOperationException("Guide refresh is running. Retry when it finishes.");
        Directory.CreateDirectory(c.OutputDirectory);
        var generation = Path.Combine(c.OutputDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generation);
        var published = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var source = Path.Combine(generation, "source.xml");
            await Pipeline.DownloadGuide(client, c.EpgUrl, source, cancellationToken);
            progress.Report(25);
            var playlist = await Pipeline.DownloadPlaylist(client, c.PlaylistUrl, cancellationToken);
            var result = Pipeline.Convert(playlist, source, generation, DateTimeOffset.UtcNow, cancellationToken);
            progress.Report(90);
            // Switch both paths in one configuration save; immutable generations avoid half-written inputs.
            var oldTuner = tuner.Url;
            var oldGuide = guide.Path;
            tuner.Url = Path.Combine(generation, "playlist.m3u");
            guide.Path = Path.Combine(generation, "guide.xml");
            try { server.SaveConfiguration("livetv", live); }
            catch { tuner.Url = oldTuner; guide.Path = oldGuide; throw; }
            published = true;
            File.Delete(source);
            c.LastResult = $"{DateTimeOffset.UtcNow:u}: {result}";
            plugin.SaveConfiguration();
            tasks.QueueScheduledTask(guideTask.ScheduledTask, new TaskOptions());
            logger.LogInformation("Edem refresh succeeded: {Result}", result);
            progress.Report(100);
            // Keep last generation for rollback; don't remove directories still referenced by another tuner.
            var protectedDirs = live.TunerHosts.Select(t => Path.GetDirectoryName(t.Url)).Concat(live.ListingProviders.Select(g => Path.GetDirectoryName(g.Path))).Append(Path.GetDirectoryName(oldTuner)).Append(Path.GetDirectoryName(oldGuide)).ToHashSet();
            foreach (var dir in Directory.GetDirectories(c.OutputDirectory))
                if (!protectedDirs.Contains(dir) && Guid.TryParseExact(Path.GetFileName(dir), "N", out _)) try { Directory.Delete(dir, true); } catch (IOException) { logger.LogWarning("Could not remove an old Edem generation."); }
        }
        catch (Exception ex)
        {
            if (!published) { try { Directory.Delete(generation, true); } catch (IOException) { } }
            // Don't record exception text: provider URLs may carry credentials.
            c.LastResult = $"{DateTimeOffset.UtcNow:u}: Update failed; see scheduled task status. Previous configuration retained unless publication completed.";
            plugin.SaveConfiguration();
            if (ex is OperationCanceledException) throw;
            throw new InvalidOperationException("Edem update failed. Check source URLs and tuner/provider IDs; no credentials are included in this error.");
        }
    }
}
