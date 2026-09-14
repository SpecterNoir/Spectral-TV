namespace Jellyfin.Plugin.SpectralTV.Domain;

public enum ChannelContentType
{
    TvShow = 0,
    Movie = 1,
    MusicVideo = 2,
    Music = 3,
    Weather = 4
}

public enum AspectRatioMode
{
    SixteenNine = 0,
    FourThree = 1
}

public enum BugPlacementMode
{
    Auto = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 3,
    BottomRight = 4,
    None = 5
}

public enum FillerKind
{
    None = 0,
    PreRoll = 1,
    MidRoll = 2,
    PostRoll = 3,
    Promo = 4,
    Bumper = 5,
    Commercial = 6,
    StationId = 7
}

/// <summary>
/// How a weighted programming source advances through its contents.
/// </summary>
public enum ProgramPlaybackMode
{
    Sequential = 0,
    Random = 1
}

/// <summary>
/// The editorial role of an item in a channel's separate filler pool.
/// </summary>
public enum FillerContentKind
{
    Promo = 0,
    Bumper = 1,
    Commercial = 2,
    StationId = 3
}

public enum LineupOverrideKind
{
    DayOfWeek = 0,
    SpecificDate = 1
}

public enum SlotCandidateKind
{
    JellyfinItem = 0,
    Collection = 1,
    FilterQuery = 2,
    Playlist = 3
}

public enum ListPlaybackMode
{
    Sequential = 0,
    Random = 1
}

public enum CommercialBreakMode
{
    ChaptersThenTimer = 0,
    TimerOnly = 1,
    ChaptersOnly = 2
}

public enum VirtualContentSource
{
    None = 0,
    WeatherStar = 1,
    MusicArtSlide = 2
}

public enum CommercialSource
{
    Jellyfin = 0,
    CommercialBrainz = 1
}

public enum CommercialPoolMode
{
    JellyfinOnly = 0,
    CommercialBrainzOnly = 1,
    Both = 2
}

public enum ChannelCatalogMode
{
    TvOnly = 0,
    MovieOnly = 1,
    Mixed = 2,
    MusicVideoOnly = 3
}
