using System.Globalization;

namespace DualClip.Encoding;

public sealed class FfmpegClipEditor
{
    private readonly FfmpegProcessRunner _runner = new();

    public async Task EditAsync(VideoEditRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException($"The source clip '{request.InputPath}' was not found.");
        }

        ValidateRequest(request);
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-i", request.InputPath,
        };

        if (request.TrimStartSeconds is not null && request.TrimStartSeconds.Value > 0)
        {
            arguments.AddRange(["-ss", FormatNumber(request.TrimStartSeconds.Value)]);
        }

        if (request.TrimEndSeconds is not null && request.TrimStartSeconds is not null)
        {
            arguments.AddRange(["-t", FormatNumber(request.TrimEndSeconds.Value - request.TrimStartSeconds.Value)]);
        }
        else if (request.TrimEndSeconds is not null)
        {
            arguments.AddRange(["-to", FormatNumber(request.TrimEndSeconds.Value)]);
        }

        var filterChain = BuildFilterChain(request);
        var complexFilter = BuildComplexFilter(request);

        if (!string.IsNullOrWhiteSpace(complexFilter))
        {
            arguments.AddRange(["-filter_complex", complexFilter]);
        }
        else if (!string.IsNullOrWhiteSpace(filterChain))
        {
            arguments.AddRange(["-vf", filterChain]);
        }

        var videoMap = string.IsNullOrWhiteSpace(complexFilter) ? "0:v:0" : "[vout]";

        arguments.AddRange(
        [
            "-map", videoMap,
            "-map", "0:a?",
            "-c:v", "libx264",
            "-preset", "fast",
            "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k",
            "-movflags", "+faststart",
            request.OutputPath,
        ]);

        await _runner.RunAsync(request.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRequest(VideoEditRequest request)
    {
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0)
        {
            throw new InvalidOperationException("Load the clip in the preview first so DualClip can read its size.");
        }

        if (request.TrimStartSeconds is not null && request.TrimStartSeconds.Value < 0)
        {
            throw new InvalidOperationException("Trim start must be zero or greater.");
        }

        if (request.TrimEndSeconds is not null && request.TrimEndSeconds.Value <= 0)
        {
            throw new InvalidOperationException("Trim end must be greater than zero.");
        }

        if (request.TrimStartSeconds is not null && request.TrimEndSeconds is not null &&
            request.TrimEndSeconds.Value <= request.TrimStartSeconds.Value)
        {
            throw new InvalidOperationException("Trim end must be greater than trim start.");
        }

        var hasAnyCropValue = request.CropX is not null || request.CropY is not null || request.CropWidth is not null || request.CropHeight is not null;

        if (hasAnyCropValue)
        {
            if (request.CropX is null || request.CropY is null || request.CropWidth is null || request.CropHeight is null)
            {
                throw new InvalidOperationException("Crop requires X, Y, width, and height.");
            }

            if (request.CropWidth <= 0 || request.CropHeight <= 0)
            {
                throw new InvalidOperationException("Crop width and height must be greater than zero.");
            }
        }

        if (request.ScalePercent <= 0)
        {
            throw new InvalidOperationException("Zoom must be greater than zero.");
        }

        if (request.OpacityPercent <= 0 || request.OpacityPercent > 100)
        {
            throw new InvalidOperationException("Opacity must be between 0 and 100 percent.");
        }

        var hasAnyZoomValue = request.ZoomKeyframe1TimeSeconds is not null
            || request.ZoomKeyframe2TimeSeconds is not null
            || request.ZoomKeyframe1Percent is not null
            || request.ZoomKeyframe2Percent is not null;

        if (!hasAnyZoomValue)
        {
            return;
        }

        if (request.ZoomKeyframe1TimeSeconds is null
            || request.ZoomKeyframe2TimeSeconds is null
            || request.ZoomKeyframe1Percent is null
            || request.ZoomKeyframe2Percent is null)
        {
            throw new InvalidOperationException("Zoom requires both keyframe times and both zoom percentages.");
        }

        if (request.ZoomKeyframe1TimeSeconds.Value < 0 || request.ZoomKeyframe2TimeSeconds.Value < 0)
        {
            throw new InvalidOperationException("Zoom keyframe times must be zero or greater.");
        }

        if (request.ZoomKeyframe2TimeSeconds.Value <= request.ZoomKeyframe1TimeSeconds.Value)
        {
            throw new InvalidOperationException("Zoom keyframe 2 time must be greater than keyframe 1 time.");
        }

        if (request.ZoomKeyframe1Percent.Value < 100 || request.ZoomKeyframe2Percent.Value < 100)
        {
            throw new InvalidOperationException("Zoom percentages must be 100 or greater.");
        }
    }

    private static string BuildFilterChain(VideoEditRequest request)
    {
        if (NeedsComplexFilter(request))
        {
            return string.Empty;
        }

        var filters = new List<string>();

        if (request.CropX is not null && request.CropY is not null && request.CropWidth is not null && request.CropHeight is not null)
        {
            filters.Add(
                $"crop=w={request.CropWidth.Value}:h={request.CropHeight.Value}:x={request.CropX.Value}:y={request.CropY.Value}");
        }

        if (request.ZoomKeyframe1TimeSeconds is not null
            && request.ZoomKeyframe2TimeSeconds is not null
            && request.ZoomKeyframe1Percent is not null
            && request.ZoomKeyframe2Percent is not null)
        {
            var outputWidth = request.CropWidth ?? request.SourceWidth;
            var outputHeight = request.CropHeight ?? request.SourceHeight;
            var zoomFrom = Math.Clamp(request.ZoomKeyframe1Percent.Value / 100d, 1d, 10d);
            var zoomTo = Math.Clamp(request.ZoomKeyframe2Percent.Value / 100d, 1d, 10d);
            var startTime = request.ZoomKeyframe1TimeSeconds.Value;
            var endTime = request.ZoomKeyframe2TimeSeconds.Value;

            var zoomExpression =
                $"if(lte(in_time\\,{FormatNumber(startTime)})\\,{FormatNumber(zoomFrom)}\\," +
                $"if(gte(in_time\\,{FormatNumber(endTime)})\\,{FormatNumber(zoomTo)}\\," +
                $"{FormatNumber(zoomFrom)}+(({FormatNumber(zoomTo)}-{FormatNumber(zoomFrom)})*(in_time-{FormatNumber(startTime)})/{FormatNumber(endTime - startTime)})))";

            filters.Add(
                $"zoompan=z='{zoomExpression}':d=1:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':fps={Math.Max(1, request.FpsTarget)}:s={outputWidth}x{outputHeight}");
        }

        filters.Add("setsar=1");
        return string.Join(",", filters);
    }

    private static string BuildComplexFilter(VideoEditRequest request)
    {
        if (!NeedsComplexFilter(request))
        {
            return string.Empty;
        }

        var sourceWidth = request.SourceWidth;
        var sourceHeight = request.SourceHeight;
        var cropX = request.CropX ?? 0;
        var cropY = request.CropY ?? 0;
        var cropWidth = request.CropWidth ?? sourceWidth;
        var cropHeight = request.CropHeight ?? sourceHeight;
        var scalePercent = Math.Max(1d, request.ScalePercent) / 100d;
        var scaledWidth = Math.Max(2, EnsureEven(cropWidth * scalePercent));
        var scaledHeight = Math.Max(2, EnsureEven(cropHeight * scalePercent));

        var clipFilters = new List<string>
        {
            $"crop=w={cropWidth}:h={cropHeight}:x={cropX}:y={cropY}"
        };

        if (request.ZoomKeyframe1TimeSeconds is not null
            && request.ZoomKeyframe2TimeSeconds is not null
            && request.ZoomKeyframe1Percent is not null
            && request.ZoomKeyframe2Percent is not null)
        {
            var zoomFrom = Math.Clamp(request.ZoomKeyframe1Percent.Value / 100d, 1d, 10d);
            var zoomTo = Math.Clamp(request.ZoomKeyframe2Percent.Value / 100d, 1d, 10d);
            var startTime = request.ZoomKeyframe1TimeSeconds.Value;
            var endTime = request.ZoomKeyframe2TimeSeconds.Value;

            var zoomExpression =
                $"if(lte(in_time\\,{FormatNumber(startTime)})\\,{FormatNumber(zoomFrom)}\\," +
                $"if(gte(in_time\\,{FormatNumber(endTime)})\\,{FormatNumber(zoomTo)}\\," +
                $"{FormatNumber(zoomFrom)}+(({FormatNumber(zoomTo)}-{FormatNumber(zoomFrom)})*(in_time-{FormatNumber(startTime)})/{FormatNumber(endTime - startTime)})))";

            clipFilters.Add(
                $"zoompan=z='{zoomExpression}':d=1:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':fps={Math.Max(1, request.FpsTarget)}:s={cropWidth}x{cropHeight}");
        }

        clipFilters.Add($"scale={scaledWidth}:{scaledHeight}");

        if (request.FlipHorizontal)
        {
            clipFilters.Add("hflip");
        }

        if (request.FlipVertical)
        {
            clipFilters.Add("vflip");
        }

        if (Math.Abs(request.RotationDegrees) > 0.01d)
        {
            clipFilters.Add(
                $"rotate={FormatNumber(request.RotationDegrees)}*PI/180:ow=rotw(iw):oh=roth(ih):c=none");
        }

        if (request.OpacityPercent < 100d)
        {
            clipFilters.Add("format=rgba");
            clipFilters.Add($"colorchannelmixer=aa={FormatNumber(request.OpacityPercent / 100d)}");
        }

        var overlayX = $"(W-w)/2+{FormatNumber(request.TranslateX)}";
        var overlayY = $"(H-h)/2+{FormatNumber(request.TranslateY)}";
        return
            $"[0:v]{string.Join(",", clipFilters)}[fg];" +
            $"color=c=black:s={cropWidth}x{cropHeight}:r={Math.Max(1, request.FpsTarget)}[bg];" +
            $"[bg][fg]overlay=x='{overlayX}':y='{overlayY}':format=auto,setsar=1[vout]";
    }

    private static bool NeedsComplexFilter(VideoEditRequest request)
    {
        return Math.Abs(request.RotationDegrees) > 0.01d
            || Math.Abs(request.ScalePercent - 100d) > 0.01d
            || Math.Abs(request.TranslateX) > 0.01d
            || Math.Abs(request.TranslateY) > 0.01d
            || request.FlipHorizontal
            || request.FlipVertical
            || request.OpacityPercent < 100d;
    }

    private static int EnsureEven(double value)
    {
        var rounded = Math.Max(2, (int)Math.Round(value));
        return rounded % 2 == 0 ? rounded : rounded + 1;
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
