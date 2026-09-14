using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Services;

public class ChannelService
{
    private readonly SpectralTvDbContext _db;
    private readonly LogoSetService _logoSets;

    public ChannelService(SpectralTvDbContext db, LogoSetService logoSets)
    {
        _db = db;
        _logoSets = logoSets;
    }

    public async Task<List<Channel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Channels
            .Where(c => c.ContentType == ChannelContentType.TvShow || c.ContentType == ChannelContentType.Movie)
            .OrderBy(c => c.Number)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Channel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Channels
            .Where(c => c.ContentType == ChannelContentType.TvShow || c.ContentType == ChannelContentType.Movie)
            .Include(c => c.DefaultLineup!)
                .ThenInclude(l => l.Slots)
                .ThenInclude(s => s.Candidates)
            .Include(c => c.Overrides)
                .ThenInclude(o => o.Slots)
                .ThenInclude(s => s.Candidates)
            .Include(c => c.LogoSet)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Channel?> GetByNumberAsync(decimal number, CancellationToken cancellationToken = default)
    {
        if (!ChannelNumbers.TryNormalize(number, out var normalized))
        {
            return null;
        }

        return await _db.Channels.FirstOrDefaultAsync(
            c => c.Number == normalized
                && c.Enabled
                && (c.ContentType == ChannelContentType.TvShow || c.ContentType == ChannelContentType.Movie),
            cancellationToken);
    }

    public async Task<Channel> CreateAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        channel.Number = NormalizeChannelNumber(channel.Number);
        channel.ContentType = ChannelContentType.TvShow;
        channel.WeatherLocationQuery = null;
        _db.Channels.Add(channel);
        _db.ChannelProgrammingSettings.Add(new ChannelProgrammingSettings
        {
            ChannelId = channel.Id,
            Enabled = true,
            FillerEnabled = false,
            FillerChancePercent = 75,
            MinFillerItems = 1,
            MaxFillerItems = 2,
            MaxFillerSeconds = 180,
            FillerRepeatWindow = 12
        });
        await BindChannelLogoAsync(channel, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return channel;
    }

    public async Task<Channel?> UpdateAsync(Guid id, Channel updated, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        existing.Number = NormalizeChannelNumber(updated.Number);
        existing.Name = updated.Name;
        existing.Enabled = updated.Enabled;
        existing.ContentType = ChannelContentType.TvShow;
        existing.AspectRatio = updated.AspectRatio;
        existing.ScanlinesEnabled = updated.ScanlinesEnabled;
        existing.LogoSetId = updated.LogoSetId;
        existing.LogoFileName = updated.LogoFileName;
        if (!existing.LogoSetId.HasValue || string.IsNullOrWhiteSpace(existing.LogoFileName))
        {
            existing.ChannelLogoPath = null;
        }
        else if (!string.IsNullOrWhiteSpace(updated.ChannelLogoPath))
        {
            existing.ChannelLogoPath = updated.ChannelLogoPath;
        }

        await BindChannelLogoAsync(existing, cancellationToken);
        existing.BugPlacement = updated.BugPlacement;
        existing.WeatherLocationQuery = null;

        await _db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        _db.Channels.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SaveAnchorAsync(Guid channelId, object anchor, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return;
        }

        channel.PlayoutAnchorJson = SpectralTvJson.Serialize(anchor);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<T?> GetAnchorAsync<T>(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null || string.IsNullOrWhiteSpace(channel.PlayoutAnchorJson))
        {
            return default;
        }

        return SpectralTvJson.Deserialize<T>(channel.PlayoutAnchorJson);
    }

    private static decimal NormalizeChannelNumber(decimal number)
    {
        if (!ChannelNumbers.TryNormalize(number, out var normalized))
        {
            throw new ArgumentException("Channel number must be at least 1 and use at most one decimal digit (.0 through .9).");
        }

        return normalized;
    }

    private async Task BindChannelLogoAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (!channel.LogoSetId.HasValue)
        {
            return;
        }

        var logoSet = await _db.LogoSets
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == channel.LogoSetId, cancellationToken);

        if (logoSet is null)
        {
            return;
        }

        _logoSets.TryBindChannelLogo(channel, logoSet);
    }
}
