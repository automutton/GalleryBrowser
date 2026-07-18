using System.Diagnostics;
using System.Globalization;
using System.IO;
using GalleryBrowser.Models;

namespace GalleryBrowser.Services;

public sealed class FfmpegService
{
    public const string DefaultSupportedExtensions = ".mp4,.mkv,.avi,.mov,.wmv,.webm,.flv,.m4v,.mpeg,.mpg,.ts";
    private string _configuredExecutablePath = string.Empty;
    private string _supportedExtensions = DefaultSupportedExtensions;

    public bool IsAvailable => ResolveExecutablePath() is not null;

    public void Configure(FfmpegSettingsDto settings)
    {
        _configuredExecutablePath = settings.ExecutablePath.Trim();
        _supportedExtensions = settings.SupportedExtensions;
    }

    public bool SupportsVideo(string path) =>
        File.Exists(path) &&
        _supportedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(extension => string.Equals(extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    public bool TryCreateThumbnail(string sourcePath, string cachePath)
    {
        var executablePath = ResolveExecutablePath();
        if (executablePath is null || !File.Exists(sourcePath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(cachePath)!,
            Path.GetFileNameWithoutExtension(cachePath) + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(GetRepresentativeTimestamp(sourcePath));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("-sn");
            startInfo.ArgumentList.Add("-dn");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add("scale=462:350:force_original_aspect_ratio=increase,crop=462:350");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(temporaryPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(90_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                return false;
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetRepresentativeTimestamp(string sourcePath)
    {
        var duration = TryGetDuration(sourcePath);
        if (duration is null || duration <= 0)
        {
            return "1";
        }

        var timestamp = Math.Clamp(duration.Value * 0.15, 0.1, Math.Max(0.1, duration.Value - 0.1));
        return timestamp.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private double? TryGetDuration(string sourcePath)
    {
        var executablePath = ResolveProbeExecutablePath();
        if (executablePath is null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = false,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            startInfo.ArgumentList.Add(sourcePath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? duration
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_configuredExecutablePath) && File.Exists(_configuredExecutablePath))
        {
            return _configuredExecutablePath;
        }

        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(bundledPath) ? bundledPath : null;
    }

    private string? ResolveProbeExecutablePath()
    {
        var ffmpegPath = ResolveExecutablePath();
        if (ffmpegPath is null)
        {
            return null;
        }

        var probePath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        return File.Exists(probePath) ? probePath : null;
    }
}
