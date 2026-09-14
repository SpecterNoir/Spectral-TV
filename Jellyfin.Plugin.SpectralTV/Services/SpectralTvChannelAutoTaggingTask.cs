using Jellyfin.Plugin.SpectralTV.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Weekly scheduled task that tags Jellyfin library items with spectraltv-* channel tags.
/// </summary>
public class SpectralTvChannelAutoTaggingTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpectralTvChannelAutoTaggingTask> _logger;

    public SpectralTvChannelAutoTaggingTask(IServiceScopeFactory scopeFactory, ILogger<SpectralTvChannelAutoTaggingTask> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "SpectralTV AI Channel Auto-Tagging";

    public string Key => "SpectralTVAiChannelAutoTagging";

    public string Description =>
        "Tags movies, series, and music videos with spectraltv-* channel tags using built-in channel rules so AI catalog loading is faster.";

    public string Category => "SpectralTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        if (!settings.AutoTagChannelsWeekly)
        {
            _logger.LogInformation("SpectralTV channel auto-tagging skipped because weekly auto-tagging is disabled.");
            progress.Report(100);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var tagging = scope.ServiceProvider.GetRequiredService<SpectralTvChannelTaggingService>();
        await tagging.RunAsync(fullRetag: false, progress, cancellationToken).ConfigureAwait(false);
    }
}
