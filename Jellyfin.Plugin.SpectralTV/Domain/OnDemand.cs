namespace Jellyfin.Plugin.SpectralTV.Domain;

/// <summary>
/// How a live automatic channel chooses the next program source.
/// </summary>
public enum LiveSelectionMode
{
    /// <summary>Keep sources close to equal airtime regardless of configured weight.</summary>
    Balanced = 0,

    /// <summary>Keep sources close to their configured airtime weights.</summary>
    Weighted = 1,

    /// <summary>Choose the next source randomly. Episode order inside each source is configured separately.</summary>
    Random = 2
}

/// <summary>
/// High-level programming model for an on-demand channel.
/// </summary>
public enum OnDemandProgrammingMode
{
    /// <summary>Sources are selected according to the channel rotation rule.</summary>
    DynamicSources = 0,

    /// <summary>Sources are consumed in the exact configured order.</summary>
    FixedSequence = 1
}

/// <summary>
/// How an on-demand channel chooses the next source.
/// </summary>
public enum OnDemandRotationMode
{
    Alternating = 0,
    Blocks = 1,
    BalancedRandom = 2,
    WeightedRandom = 3,
    Random = 4,
    ShuffleCycle = 5,
    CustomPattern = 6
}

/// <summary>
/// Definition for a resumable smart playlist/channel. Playback state is intentionally separate
/// from the definition so every Jellyfin user can have independent progress through the same recipe.
/// </summary>
public class OnDemandChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public OnDemandProgrammingMode ProgrammingMode { get; set; } = OnDemandProgrammingMode.DynamicSources;

    public OnDemandRotationMode RotationMode { get; set; } = OnDemandRotationMode.Alternating;

    /// <summary>
    /// JSON array of OnDemandSource ids used when RotationMode is CustomPattern. Repeating the same
    /// source id is allowed, which makes patterns such as A,A,B,C,B possible without duplicating sources.
    /// </summary>
    public string CustomPatternJson { get; set; } = "[]";

    public bool FillerEnabled { get; set; } = true;

    public bool FillerBeforeFirstProgram { get; set; } = true;

    public bool FillerBetweenPrograms { get; set; } = true;

    public bool FillerOnSourceChangeOnly { get; set; }

    public int MinFillerItems { get; set; } = 1;

    public int MaxFillerItems { get; set; } = 1;

    public int FillerRepeatWindow { get; set; } = 10;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One show, season, episode, or movie inside an on-demand channel recipe.
/// </summary>
public class OnDemandSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid JellyfinItemId { get; set; }

    /// <summary>Relative selection weight for WeightedRandom.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>How many consecutive programs to play from this source when using Blocks.</summary>
    public int BlockSize { get; set; } = 1;

    /// <summary>Whether a series/season advances in story order or picks episodes randomly.</summary>
    public ProgramPlaybackMode PlaybackMode { get; set; } = ProgramPlaybackMode.Sequential;

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }
}

/// <summary>
/// Promo, bumper, commercial, or station-id source for an on-demand channel.
/// </summary>
public class OnDemandFillerSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid JellyfinItemId { get; set; }

    public FillerContentKind Kind { get; set; } = FillerContentKind.Promo;

    public int Weight { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }
}

/// <summary>
/// Per-user (or per-profile-key) cursor through an on-demand channel. The viewing client can use a
/// Jellyfin user id as ProgressKey. Keeping the key opaque also makes testing and future profiles easy.
/// </summary>
public class OnDemandProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string ProgressKey { get; set; } = string.Empty;

    /// <summary>Index into alternating, block, fixed, or custom rotation cycles.</summary>
    public int PatternIndex { get; set; }

    public Guid? LastSourceId { get; set; }

    /// <summary>Per-source next episode indexes.</summary>
    public string SourceCursorJson { get; set; } = "{}";

    /// <summary>Per-source pick counts used by BalancedRandom.</summary>
    public string SourcePickCountJson { get; set; } = "{}";

    /// <summary>Remaining source ids for ShuffleCycle.</summary>
    public string ShuffleBagJson { get; set; } = "[]";

    /// <summary>Recent filler ids used for no-repeat behavior.</summary>
    public string RecentFillerJson { get; set; } = "[]";

    /// <summary>Currently active main Jellyfin item, reserved for client resume integration.</summary>
    public Guid? CurrentItemId { get; set; }

    /// <summary>Playback position within CurrentItemId, reserved for client resume integration.</summary>
    public long CurrentPositionTicks { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
