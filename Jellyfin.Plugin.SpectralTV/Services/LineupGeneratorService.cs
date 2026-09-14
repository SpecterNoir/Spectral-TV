using Jellyfin.Plugin.SpectralTV.Domain;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Builds a channel from its continuous weighted programming pool. The legacy 48-slot
/// scheduler was intentionally retired because it duplicated and obscured the core workflow.
/// </summary>
public class LineupGeneratorService
{
    private readonly WeightedProgrammingService _programming;

    public LineupGeneratorService(WeightedProgrammingService programming)
    {
        _programming = programming;
    }

    public async Task BuildPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode = PlayoutBuildMode.ReplaceWindow,
        CancellationToken cancellationToken = default)
    {
        await _programming.BuildPlayoutAsync(channel, startUtc, endUtc, mode, cancellationToken);
    }
}
