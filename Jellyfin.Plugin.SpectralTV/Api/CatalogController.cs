using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Jellyfin library search helpers for the SpectralTV admin UI.
/// </summary>
[ApiController]
[Route("SpectralTV/api/catalog")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CatalogController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public CatalogController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Searches Jellyfin library items for lineup and weighted-channel assignment.
    /// </summary>
    [HttpGet("search")]
    public ActionResult<IEnumerable<object>> Search(
        [FromQuery] string q,
        [FromQuery] string purpose = "programming",
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return Ok(Array.Empty<object>());
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            SearchTerm = q.Trim(),
            Limit = Math.Clamp(limit, 1, 50),
            IncludeItemTypes = purpose.Equals("filler", StringComparison.OrdinalIgnoreCase)
                ? new[] { BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video }
                : new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        var items = _libraryManager.GetItemsResult(query).Items;
        return Ok(items.Select(MapSearchResult));
    }

    /// <summary>
    /// Resolves display metadata for Jellyfin item identifiers.
    /// </summary>
    [HttpPost("lookup")]
    public ActionResult<IEnumerable<object>> Lookup([FromBody] CatalogLookupRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Ids is not { Count: > 0 })
        {
            return Ok(Array.Empty<object>());
        }

        var results = new List<object>();
        foreach (var id in request.Ids.Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null)
            {
                results.Add(MapSearchResult(item));
            }
        }

        return Ok(results);
    }

    private static object MapSearchResult(BaseItem item)
    {
        var runtime = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : (TimeSpan?)null;

        return new
        {
            id = item.Id,
            name = item.Name,
            type = item.GetBaseItemKind().ToString(),
            runtimeMinutes = runtime.HasValue ? (int)Math.Round(runtime.Value.TotalMinutes) : (int?)null,
            year = item.ProductionYear
        };
    }

}

/// <summary>
/// Request body for catalog item lookup.
/// </summary>
public class CatalogLookupRequest
{
    /// <summary>
    /// Gets or sets Jellyfin item identifiers to resolve.
    /// </summary>
    public List<Guid> Ids { get; set; } = new();
}
