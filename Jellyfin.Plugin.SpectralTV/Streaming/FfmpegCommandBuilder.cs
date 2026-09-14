using System.Globalization;
using Jellyfin.Plugin.SpectralTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.SpectralTV.Streaming;

/// <summary>Builds the two FFmpeg commands Spectral TV needs: scheduled video and a safe fallback.</summary>
public class FfmpegCommandBuilder
{
    private readonly JellyfinFfmpegEncodingService _encoding;

    public FfmpegCommandBuilder(JellyfinFfmpegEncodingService encoding)
    {
        _encoding = encoding;
    }

    public IReadOnlyList<string> BuildMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, inputPath);
        var filter = _encoding.AdaptVideoFilterForEncoder(
            BuildVideoFilterChain(channel, width, height, bugImagePath),
            context.Encoder);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-vf", filter
        });
        AppendVideoEncoderArgs(args, context);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-f", "mpegts",
            "-mpegts_flags", "+initial_discontinuity",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildFallbackCommand(Channel channel, double durationSeconds)
    {
        var (width, height) = GetResolution(channel);
        var duration = Math.Clamp(durationSeconds, 30, 600).ToString("F0", CultureInfo.InvariantCulture);
        var context = CreateEncodingContext(width, height);
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            "-f", "lavfi",
            "-i", $"smptebars=size={width}x{height}:rate=30",
            "-map", "1:v",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(args, context, stillImage: true);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });

        return args;
    }

    private readonly record struct EncodingContext(
        EncodingJobInfo State,
        EncodingOptions Options,
        string Encoder,
        IReadOnlyList<string> HardwareDeviceArgs);

    private EncodingContext CreateEncodingContext(int width, int height, string? mediaPath = null)
    {
        var options = _encoding.GetEncodingOptions();
        var state = _encoding.CreateVideoEncodingState(width, height, mediaPath);
        var encoder = _encoding.GetH264VideoEncoder(state, options);
        var hardwareDeviceArgs = _encoding.GetHardwareDeviceArguments(state, options);
        return new EncodingContext(state, options, encoder, hardwareDeviceArgs);
    }

    private void AppendVideoEncoderArgs(List<string> args, EncodingContext context, bool stillImage = false)
    {
        args.Add("-c:v");
        args.Add(context.Encoder);
        args.AddRange(_encoding.GetVideoEncoderArguments(context.State, context.Options, context.Encoder, stillImage));
    }

    private static string BuildVideoFilterChain(Channel channel, int width, int height, string? bugImagePath)
    {
        var filters = new List<string>
        {
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black"
        };

        if (channel.ScanlinesEnabled && channel.AspectRatio == AspectRatioMode.FourThree)
        {
            filters.Add("format=yuv420p,geq=lum='if(not(mod(Y,4)),lum(X,Y)*0.82,lum(X,Y))'");
        }

        if (channel.BugPlacement != BugPlacementMode.None
            && !string.IsNullOrWhiteSpace(bugImagePath)
            && File.Exists(bugImagePath))
        {
            var overlay = GetBugOverlay(channel);
            filters.Add($"movie={EscapeMovie(bugImagePath)}[bug];[in][bug]overlay={overlay}[out]");
            return string.Join(',', filters).Replace("[in]", "[0:v]").Replace("[out]", string.Empty);
        }

        return string.Join(',', filters);
    }

    private static string GetBugOverlay(Channel channel)
    {
        const int margin = 24;
        return channel.BugPlacement switch
        {
            BugPlacementMode.TopLeft => $"{margin}:{margin}",
            BugPlacementMode.TopRight => $"W-w-{margin}:{margin}",
            BugPlacementMode.BottomLeft => $"{margin}:H-h-{margin}",
            BugPlacementMode.BottomRight => $"W-w-{margin}:H-h-{margin}",
            _ => $"W-w-{margin}:{margin}"
        };
    }

    private static (int Width, int Height) GetResolution(Channel channel)
        => channel.AspectRatio == AspectRatioMode.FourThree ? (1440, 1080) : (1920, 1080);

    private static string EscapeMovie(string path) => path.Replace("\\", "/").Replace(":", "\\:");
}
