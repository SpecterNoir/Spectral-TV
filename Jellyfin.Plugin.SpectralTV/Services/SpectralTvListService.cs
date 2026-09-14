using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Services;

public class SpectralTvListService
{
    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;

    public SpectralTvListService(SpectralTvDbContext db, ILibraryManager libraryManager)
    {
        _db = db;
        _libraryManager = libraryManager;
    }

    public async Task<List<SpectralTvList>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.SpectralTvLists
            .OrderBy(l => l.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<SpectralTvList?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.SpectralTvLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public IReadOnlyList<JellyfinPlaylistInfo> GetJellyfinPlaylists()
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        return _libraryManager.GetItemsResult(query).Items
            .Select(p => new JellyfinPlaylistInfo
            {
                Id = p.Id,
                Name = p.Name,
                ItemCount = GetPlaylistItemCount(p.Id)
            })
            .ToList();
    }

    public IReadOnlyList<JellyfinPlaylistInfo> GetUnregisteredJellyfinPlaylists()
    {
        var registered = _db.SpectralTvLists.Select(l => l.JellyfinPlaylistId).ToHashSet();
        return GetJellyfinPlaylists()
            .Where(p => !registered.Contains(p.Id))
            .ToList();
    }

    public int GetPlaylistItemCount(Guid jellyfinPlaylistId)
    {
        return GetPlaylistItems(jellyfinPlaylistId).Count;
    }

    public IReadOnlyList<BaseItem> GetPlaylistItems(Guid jellyfinPlaylistId)
    {
        var playlist = _libraryManager.GetItemById(jellyfinPlaylistId);
        if (playlist is null)
        {
            return Array.Empty<BaseItem>();
        }

        var query = new InternalItemsQuery
        {
            ParentId = playlist.Id,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[]
            {
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.MusicVideo,
                BaseItemKind.Audio,
                BaseItemKind.Video
            },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    public async Task<SpectralTvList> CreateAsync(SpectralTvListCreateDto dto, CancellationToken cancellationToken = default)
    {
        var playlist = _libraryManager.GetItemById(dto.JellyfinPlaylistId)
            ?? throw new InvalidOperationException("Jellyfin playlist not found.");

        if (await _db.SpectralTvLists.AnyAsync(l => l.JellyfinPlaylistId == dto.JellyfinPlaylistId, cancellationToken))
        {
            throw new InvalidOperationException("This Jellyfin playlist is already registered as a SpectralTV list.");
        }

        var entity = new SpectralTvList
        {
            Name = string.IsNullOrWhiteSpace(dto.Name) ? playlist.Name : dto.Name.Trim(),
            JellyfinPlaylistId = dto.JellyfinPlaylistId,
            PlaybackMode = dto.PlaybackMode,
            CreatedAt = DateTime.UtcNow
        };

        _db.SpectralTvLists.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<SpectralTvList?> UpdateAsync(Guid id, SpectralTvListUpdateDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _db.SpectralTvLists.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            entity.Name = dto.Name.Trim();
        }

        if (dto.PlaybackMode.HasValue)
        {
            entity.PlaybackMode = dto.PlaybackMode.Value;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.SpectralTvLists.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        _db.SpectralTvLists.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> IsReferencedAsync(Guid finTvListId, CancellationToken cancellationToken = default)
    {
        if (await _db.SlotCandidates.AnyAsync(c => c.SpectralTvListId == finTvListId, cancellationToken))
        {
            return true;
        }

        return await _db.SpecialPresentationCandidates.AnyAsync(c => c.SpectralTvListId == finTvListId, cancellationToken);
    }
}

public class JellyfinPlaylistInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int ItemCount { get; set; }
}

public class SpectralTvListCreateDto
{
    public Guid JellyfinPlaylistId { get; set; }

    public string? Name { get; set; }

    public ListPlaybackMode PlaybackMode { get; set; } = ListPlaybackMode.Sequential;
}

public class SpectralTvListUpdateDto
{
    public string? Name { get; set; }

    public ListPlaybackMode? PlaybackMode { get; set; }
}
