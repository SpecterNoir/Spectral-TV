using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>Stores user-uploaded channel logos. Legacy third-party logo catalogs are not supported.</summary>
public class LogoSetService
{
    private const string CustomSource = "spectraltv://custom";
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private readonly SpectralTvDbContext _db;

    public LogoSetService(SpectralTvDbContext db)
    {
        _db = db;
    }

    public async Task<List<LogoSet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.LogoSets.Include(set => set.Entries).AsNoTracking().OrderBy(set => set.Name).ToListAsync(cancellationToken);
    }

    public async Task<LogoSet?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.LogoSets.Include(set => set.Entries).AsNoTracking().FirstOrDefaultAsync(set => set.Id == id, cancellationToken);
    }

    public async Task<LogoSet> CreateCustomSetAsync(string name, CancellationToken cancellationToken = default)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "My Channel Logos" : name.Trim();
        var existing = await _db.LogoSets.Include(set => set.Entries)
            .FirstOrDefaultAsync(set => set.Name == cleanName && set.SourceUrl == CustomSource, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Spectral TV is not initialized.");
        Directory.CreateDirectory(plugin.LogosFolder);
        var set = new LogoSet
        {
            Name = cleanName,
            SourceUrl = CustomSource,
            StoragePath = Path.Combine(plugin.LogosFolder, Guid.NewGuid().ToString("N")),
            LastSyncedAt = DateTime.UtcNow
        };
        Directory.CreateDirectory(set.StoragePath);
        _db.LogoSets.Add(set);
        await _db.SaveChangesAsync(cancellationToken);
        return set;
    }

    public async Task<LogoSetEntry> UploadCustomLogoAsync(
        Guid setId,
        Stream stream,
        string fileName,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(item => item.Entries).FirstOrDefaultAsync(item => item.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo collection not found.");
        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("Only your own logo collection can receive uploads.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Logo must be a PNG, JPG, or WebP image.");
        }

        Directory.CreateDirectory(set.StoragePath);
        var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
        var storedName = $"{safeBaseName}-{Guid.NewGuid():N}{extension}";
        var destination = Path.Combine(set.StoragePath, storedName);
        await using (var output = File.Create(destination))
        {
            await stream.CopyToAsync(output, cancellationToken);
        }

        var entry = new LogoSetEntry
        {
            LogoSetId = set.Id,
            FileName = storedName,
            RelativePath = storedName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? safeBaseName : displayName.Trim()
        };
        _db.LogoSetEntries.Add(entry);
        set.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task DeleteCustomLogoAsync(Guid setId, Guid entryId, CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(item => item.Entries).FirstOrDefaultAsync(item => item.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo collection not found.");
        var entry = set.Entries.FirstOrDefault(item => item.Id == entryId)
            ?? throw new InvalidOperationException("Logo not found.");
        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("This logo collection is read-only.");
        }

        var path = ResolveLogoPath(set, entry.RelativePath);
        if (path is not null && File.Exists(path)) File.Delete(path);
        var channels = await _db.Channels.Where(channel => channel.LogoSetId == setId && channel.LogoFileName == entry.FileName).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            channel.LogoSetId = null;
            channel.LogoFileName = null;
            channel.ChannelLogoPath = null;
        }
        _db.LogoSetEntries.Remove(entry);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCustomSetAsync(Guid setId, CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(item => item.Entries).FirstOrDefaultAsync(item => item.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo collection not found.");
        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("This logo collection is read-only.");
        }

        var channels = await _db.Channels.Where(channel => channel.LogoSetId == setId).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            channel.LogoSetId = null;
            channel.LogoFileName = null;
            channel.ChannelLogoPath = null;
        }
        _db.LogoSets.Remove(set);
        await _db.SaveChangesAsync(cancellationToken);
        if (Directory.Exists(set.StoragePath)) Directory.Delete(set.StoragePath, true);
    }

    public bool TryBindChannelLogo(Channel channel, LogoSet logoSet)
    {
        var entry = logoSet.Entries.FirstOrDefault(item => item.FileName.Equals(channel.LogoFileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        channel.ChannelLogoPath = ResolveLogoPath(logoSet, entry.RelativePath);
        return channel.ChannelLogoPath is not null;
    }

    public string? ResolveLogoPath(LogoSet set, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(set.StoragePath) || string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = Path.GetFullPath(set.StoragePath);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    public static bool IsCustomSet(LogoSet set) => string.Equals(set.SourceUrl, CustomSource, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string value)
    {
        var cleaned = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "channel-logo" : cleaned;
    }
}
