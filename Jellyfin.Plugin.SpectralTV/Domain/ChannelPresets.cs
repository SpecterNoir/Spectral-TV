namespace Jellyfin.Plugin.SpectralTV.Domain;

/// <summary>
/// How ready-made channel numbers are assigned when presets are applied.
/// </summary>
public enum ChannelPresetNumberingMode
{
    Legacy = 0,
    Subchannels = 1
}

/// <summary>
/// Built-in Binarygeek119 TV/movie channel lineup presets.
/// </summary>
public static class ChannelPresets
{
    public const string Binarygeek119LogoSetName = "Binarygeek119 Set";

    public static IReadOnlyList<ChannelPresetDefinition> All { get; } =
    [
        Preset(119, 119.1m, "FlashBack TV", ChannelContentType.TvShow, "TV Shows", "1970–2010 TV and movies (first-episode year for series)", "spectraltv-flashback", "Shows/FlashBack_TV.png", catalogMode: ChannelCatalogMode.Mixed, minYear: 1970, maxYear: 2010),
        Preset(120, 119.2m, "Retro TV", ChannelContentType.TvShow, "TV Shows", "1910–1969 TV and movies (first-episode year for series)", "spectraltv-retro", "Shows/Retro_TV.png", catalogMode: ChannelCatalogMode.Mixed, minYear: 1910, maxYear: 1969),
        Preset(121, 119.3m, "[OpenSwim]", ChannelContentType.TvShow, "TV Shows", "Nick, Disney, Fox Kids, and Cartoon Network style kids TV/movies; any year; TV-PG max", "spectraltv-open-swim", "Shows/[open_swim].png", catalogMode: ChannelCatalogMode.Mixed, maxRating: "TV-PG"),
        Preset(122, 119.4m, "Flip Television", ChannelContentType.TvShow, "TV Shows", "Reality TV themed shows and movies", "spectraltv-reality", "Shows/Flip_Television.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(123, 119.5m, "BinaryGeek119 News", ChannelContentType.TvShow, "TV Shows", "News", "spectraltv-news", logoPath: null),
        Preset(125, 124.1m, "Past Tense News", ChannelContentType.TvShow, "TV Shows", "Content from Jellyfin library Past Tense News only", "spectraltv-past-tense-news", "News/Past_Tense_News.png"),
        Preset(128, 124.2m, "Cops And Robbers", ChannelContentType.TvShow, "TV Shows", "Crime and cop themed TV shows and movies (genre or plot)", "spectraltv-crime", "Shows/cops_and_robbers.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(129, 124.3m, "Slappy", ChannelContentType.TvShow, "TV Shows", "Comedy TV and movies with 6pm Slappy's Toon Takeover block", "spectraltv-comedy", "Shows/Slappy.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(130, 126.1m, "Winning", ChannelContentType.TvShow, "TV Shows", "Game shows channel", "spectraltv-game-shows", "Shows/winning.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(133, 126.2m, "GET LEARNEDED", ChannelContentType.TvShow, "TV Shows", "Educational TV shows and movies", "spectraltv-education", "Shows/GET_LEARNEDED.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(134, 126.3m, "YouTube TV", ChannelContentType.TvShow, "TV Shows", "Content from Jellyfin TV library YouTube only", "spectraltv-youtube", "Shows/YouTube_TV.png"),
        Preset(203, 203.1m, "Creature Double Feature", ChannelContentType.Movie, "Movies", "Creature and monster movies and TV (genre, plot, or tags)", "spectraltv-creature", "Movies/Creature_Double_Feature.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(204, 203.2m, "Hero TV", ChannelContentType.Movie, "Movies", "Anyone who saves or protects people — heroes, rescuers, and champions", "spectraltv-hero", "Movies/Hero_TV.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(205, 203.3m, "That's Funny", ChannelContentType.Movie, "Movies", "Comedian movies and TV shows", "spectraltv-funny", "Movies/That's_Funny.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(207, 203.4m, "The Holiday Channel", ChannelContentType.Movie, "Movies", "Seasonal holiday TV and movies; off-season plays The Holiday Channel.mkv", "spectraltv-holiday", "The Holiday Channel/The Holiday Channel-plane.png", catalogMode: ChannelCatalogMode.Mixed),
    ];

    public static ChannelPresetDefinition? Find(string id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static ChannelPresetDefinition Preset(
        decimal legacyNumber,
        decimal subchannelNumber,
        string name,
        ChannelContentType contentType,
        string category,
        string description,
        string libraryTag,
        string? logoPath,
        bool useLogo = true,
        ChannelCatalogMode? catalogMode = null,
        int? minYear = null,
        int? maxYear = null,
        string? maxRating = null)
    {
        return new ChannelPresetDefinition
        {
            Id = libraryTag,
            LegacyNumber = legacyNumber,
            SubchannelNumber = subchannelNumber,
            Name = name,
            ContentType = contentType,
            Category = category,
            Description = description,
            LibraryTag = libraryTag,
            LogoRelativePath = logoPath,
            UseBinarygeek119Logo = useLogo,
            FilterJson = BuildFilterJson(libraryTag, minYear, maxYear, maxRating),
            CatalogMode = catalogMode ?? ChannelAiRules.GetByLibraryTag(libraryTag)?.DefaultCatalogMode
        };
    }

    private static string BuildFilterJson(string libraryTag, int? minYear, int? maxYear, string? maxRating)
    {
        var filter = new Dictionary<string, object?> { ["presetId"] = libraryTag };
        if (minYear.HasValue)
        {
            filter["minYear"] = minYear.Value;
        }

        if (maxYear.HasValue)
        {
            filter["maxYear"] = maxYear.Value;
        }

        if (!string.IsNullOrWhiteSpace(maxRating))
        {
            filter["maxRating"] = maxRating;
        }

        return SpectralTvJson.Serialize(filter);
    }
}

/// <summary>
/// Ready-made channel template metadata.
/// </summary>
public class ChannelPresetDefinition
{
    public string Id { get; set; } = string.Empty;
    public decimal LegacyNumber { get; set; }
    public decimal SubchannelNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public ChannelContentType ContentType { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LibraryTag { get; set; } = string.Empty;
    public string? LogoRelativePath { get; set; }
    public bool UseBinarygeek119Logo { get; set; }
    public string? FilterJson { get; set; }
    public ChannelCatalogMode? CatalogMode { get; set; }

    /// <summary>
    /// Legacy compatibility for older logo-matching code. Spectral TV no longer ships weather presets.
    /// </summary>
    public bool IsWeatherChannel => false;

    public decimal GetNumber(ChannelPresetNumberingMode mode)
        => mode == ChannelPresetNumberingMode.Subchannels ? SubchannelNumber : LegacyNumber;
}
