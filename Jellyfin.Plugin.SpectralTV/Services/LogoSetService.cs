using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Manages channel logo sets and copies selected logos into the SpectralTV data folder.
/// </summary>
public class LogoSetService
{
    private const string Binarygeek119Owner = "binarygeek119";
    private const string Binarygeek119Repo = "open-channel-logos";
    private const string Binarygeek119Ref = "22b4bbd3e5882d18cdf6b6c66b55c6e386405d49";
    private const string Binarygeek119TreeApi = "https://api.github.com/repos/binarygeek119/open-channel-logos/git/trees/22b4bbd3e5882d18cdf6b6c66b55c6e386405d49?recursive=1";
    private const string Binarygeek119RawRoot = "https://raw.githubusercontent.com/binarygeek119/open-channel-logos/22b4bbd3e5882d18cdf6b6c66b55c6e386405d49/";

    private readonly SpectralTvDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LogoSetService> _logger;

    public LogoSetService(
        SpectralTvDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<LogoSetService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LogoSet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.LogoSets
            .Include(s => s.Entries)
            .OrderBy(s => s.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<LogoSet> EnsureBinarygeek119SetAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _db.LogoSets
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Name == ChannelPresets.Binarygeek119LogoSetName, cancellationToken);

        if (existing is null)
        {
            existing = new LogoSet
            {
                Name = ChannelPresets.Binarygeek119LogoSetName,
                SourceUrl = $"https://github.com/{Binarygeek119Owner}/{Binarygeek119Repo}/tree/{Binarygeek119Ref}",
                SourceRef = Binarygeek119Ref,
                IsBuiltIn = true
            };
            _db.LogoSets.Add(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("SpectralTV plugin not initialized.");
        var destinationRoot = Path.Combine(plugin.LogosFolder, "binarygeek119");
        Directory.CreateDirectory(destinationRoot);

        var bundledRoot = plugin.BundledLogosFolder;
        if (Directory.Exists(bundledRoot))
        {
            CopyDirectory(bundledRoot, destinationRoot);
        }

        try
        {
            await DownloadBinarygeek119LogosAsync(destinationRoot, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh Binarygeek119 logos from GitHub; bundled/local copies will be used.");
        }

        await RefreshEntriesAsync(existing, destinationRoot, cancellationToken);
        return existing;
    }

    public async Task<LogoSet> CreateCustomSetAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Logo set name is required.", nameof(name));
        }

        var existing = await _db.LogoSets.FirstOrDefaultAsync(s => s.Name == trimmed, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var set = new LogoSet
        {
            Name = trimmed,
            SourceUrl = "custom",
            SourceRef = "local",
            IsBuiltIn = false
        };
        _db.LogoSets.Add(set);
        await _db.SaveChangesAsync(cancellationToken);

        var root = GetLogoSetFolder(set);
        Directory.CreateDirectory(root);
        return set;
    }

    public async Task<LogoSet?> UploadCustomLogoAsync(
        Guid logoSetId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Id == logoSetId, cancellationToken);
        if (set is null)
        {
            return null;
        }

        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("Only custom logo sets can be uploaded to.");
        }

        var safeName = Path.GetFileName(fileName);
        if (!IsImageFile(safeName))
        {
            throw new InvalidOperationException("Only PNG, JPG, JPEG, and WEBP logo files are supported.");
        }

        var root = GetLogoSetFolder(set);
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, safeName);
        await using (var output = File.Create(targetPath))
        {
            await content.CopyToAsync(output, cancellationToken);
        }

        await RefreshEntriesAsync(set, root, cancellationToken);
        return set;
    }

    public async Task<bool> DeleteCustomLogoAsync(Guid logoSetId, Guid logoEntryId, CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Id == logoSetId, cancellationToken);
        if (set is null || !IsCustomSet(set))
        {
            return false;
        }

        var entry = set.Entries.FirstOrDefault(e => e.Id == logoEntryId);
        if (entry is null)
        {
            return false;
        }

        if (File.Exists(entry.FilePath))
        {
            File.Delete(entry.FilePath);
        }

        _db.LogoEntries.Remove(entry);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> RepairChannelLogosAsync(LogoSet? preferredSet = null, CancellationToken cancellationToken = default)
    {
        var set = preferredSet ?? await _db.LogoSets
            .Include(s => s.Entries)
            .OrderByDescending(s => s.IsBuiltIn)
            .FirstOrDefaultAsync(cancellationToken);
        if (set is null)
        {
            return 0;
        }

        if (set.Entries.Count == 0)
        {
            var folder = GetLogoSetFolder(set);
            if (Directory.Exists(folder))
            {
                await RefreshEntriesAsync(set, folder, cancellationToken);
                set = await _db.LogoSets.Include(s => s.Entries).FirstAsync(s => s.Id == set.Id, cancellationToken);
            }
        }

        var channels = await _db.Channels.ToListAsync(cancellationToken);
        var updated = 0;
        foreach (var channel in channels)
        {
            var preset = FindPresetForChannel(channel);
            if (preset is null || !preset.UseBinarygeek119Logo || string.IsNullOrWhiteSpace(preset.LogoRelativePath))
            {
                continue;
            }

            var entry = ResolveEntry(set, preset.LogoRelativePath, preset.Name);
            if (entry is null)
            {
                continue;
            }

            channel.LogoSetId = set.Id;
            channel.LogoFileName = entry.RelativePath;
            channel.ChannelLogoPath = entry.FilePath;
            updated++;
        }

        if (updated > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return updated;
    }

    public void ApplyLogoToChannel(Channel channel, LogoSet set, string? relativePath, string channelName)
    {
        var entry = ResolveEntry(set, relativePath, channelName);
        if (entry is null)
        {
            ClearChannelLogo(channel);
            return;
        }

        channel.LogoSetId = set.Id;
        channel.LogoFileName = entry.RelativePath;
        channel.ChannelLogoPath = entry.FilePath;
    }

    public LogoSetEntry? ResolveEntry(LogoSet set, string? relativePath, string channelName)
    {
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var exact = set.Entries.FirstOrDefault(e => string.Equals(NormalizePath(e.RelativePath), NormalizePath(relativePath), StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        var normalizedName = NormalizeLogoName(channelName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        return set.Entries.FirstOrDefault(e => NormalizeLogoName(Path.GetFileNameWithoutExtension(e.RelativePath)) == normalizedName)
            ?? set.Entries.FirstOrDefault(e => NormalizeLogoName(Path.GetFileNameWithoutExtension(e.RelativePath)).Contains(normalizedName, StringComparison.Ordinal))
            ?? set.Entries.FirstOrDefault(e => normalizedName.Contains(NormalizeLogoName(Path.GetFileNameWithoutExtension(e.RelativePath)), StringComparison.Ordinal));
    }

    public async Task<LogoSet?> SetChannelLogoAsync(Guid channelId, Guid? logoSetId, string? relativePath, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        if (!logoSetId.HasValue || string.IsNullOrWhiteSpace(relativePath))
        {
            ClearChannelLogo(channel);
            await _db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var set = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Id == logoSetId.Value, cancellationToken);
        if (set is null)
        {
            return null;
        }

        var entry = set.Entries.FirstOrDefault(e => string.Equals(NormalizePath(e.RelativePath), NormalizePath(relativePath), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        channel.LogoSetId = set.Id;
        channel.LogoFileName = entry.RelativePath;
        channel.ChannelLogoPath = entry.FilePath;
        await _db.SaveChangesAsync(cancellationToken);
        return set;
    }

    public string? ResolveLogoPath(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.ChannelLogoPath) && File.Exists(channel.ChannelLogoPath))
        {
            return channel.ChannelLogoPath;
        }

        if (!channel.LogoSetId.HasValue || string.IsNullOrWhiteSpace(channel.LogoFileName))
        {
            return null;
        }

        var set = _db.LogoSets.Include(s => s.Entries).AsNoTracking().FirstOrDefault(s => s.Id == channel.LogoSetId.Value);
        var entry = set?.Entries.FirstOrDefault(e => string.Equals(NormalizePath(e.RelativePath), NormalizePath(channel.LogoFileName), StringComparison.OrdinalIgnoreCase));
        return entry?.FilePath;
    }

    public async Task<LogoSet?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.LogoSets
            .Include(s => s.Entries)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public static bool IsCustomSet(LogoSet set)
        => string.Equals(set.SourceUrl, "custom", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadBinarygeek119LogosAsync(string destinationRoot, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpectralTV/1.0");
        var treeJson = await client.GetFromJsonAsync<GitTreeResponse>(Binarygeek119TreeApi, cancellationToken);
        if (treeJson?.Tree is null)
        {
            return;
        }

        var files = treeJson.Tree
            .Where(item => item.Type == "blob" && IsImageFile(item.Path))
            .Select(item => item.Path)
            .ToList();

        foreach (var relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                var bytes = await client.GetByteArrayAsync(Binarygeek119RawRoot + EscapeUrlPath(relativePath), cancellationToken);
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not download logo {Logo}", relativePath);
            }
        }
    }

    private async Task RefreshEntriesAsync(LogoSet set, string root, CancellationToken cancellationToken)
    {
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(IsImageFile).ToList()
            : new List<string>();

        var existing = await _db.LogoEntries.Where(e => e.LogoSetId == set.Id).ToListAsync(cancellationToken);
        var byRelative = existing.ToDictionary(e => NormalizePath(e.RelativePath), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relative = NormalizePath(Path.GetRelativePath(root, file));
            seen.Add(relative);
            if (byRelative.TryGetValue(relative, out var entry))
            {
                entry.FilePath = file;
                entry.DisplayName = Path.GetFileNameWithoutExtension(file);
                continue;
            }

            _db.LogoEntries.Add(new LogoSetEntry
            {
                LogoSetId = set.Id,
                RelativePath = relative,
                FilePath = file,
                DisplayName = Path.GetFileNameWithoutExtension(file)
            });
        }

        foreach (var obsolete in existing.Where(e => !seen.Contains(NormalizePath(e.RelativePath))))
        {
            _db.LogoEntries.Remove(obsolete);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private string GetLogoSetFolder(LogoSet set)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("SpectralTV plugin not initialized.");
        return set.IsBuiltIn
            ? Path.Combine(plugin.LogosFolder, "binarygeek119")
            : Path.Combine(plugin.LogosFolder, "custom", set.Id.ToString("N"));
    }

    private static void ClearChannelLogo(Channel channel)
    {
        channel.LogoSetId = null;
        channel.LogoFileName = null;
        channel.ChannelLogoPath = null;
    }

    internal static ChannelPresetDefinition? FindPresetForChannel(Channel channel)
    {
        var byLegacy = ChannelPresets.All.FirstOrDefault(p => p.LegacyNumber == channel.Number);
        if (byLegacy is not null)
        {
            return byLegacy;
        }

        var bySubchannel = ChannelPresets.All.FirstOrDefault(p => p.SubchannelNumber == channel.Number);
        if (bySubchannel is not null)
        {
            return bySubchannel;
        }

        var normalizedName = NormalizeLogoName(channel.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var exactName = ChannelPresets.All.FirstOrDefault(p => NormalizeLogoName(p.Name) == normalizedName);
        if (exactName is not null)
        {
            return exactName;
        }

        // Old "News & Weather" names now fall back to the news preset only. Weather-specific
        // presets are intentionally no longer part of Spectral TV.
        if (normalizedName.Contains("newsweather", StringComparison.Ordinal)
            || normalizedName.Contains("newandweather", StringComparison.Ordinal))
        {
            return ChannelPresets.All.FirstOrDefault(p => p.Id == "spectraltv-news");
        }

        return ChannelPresets.All.FirstOrDefault(p =>
            normalizedName.Contains(NormalizeLogoName(p.Name), StringComparison.Ordinal)
            || NormalizeLogoName(p.Name).Contains(normalizedName, StringComparison.Ordinal));
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string NormalizeLogoName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeUrlPath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private sealed class GitTreeResponse
    {
        public List<GitTreeItem> Tree { get; set; } = new();
    }

    private sealed class GitTreeItem
    {
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
