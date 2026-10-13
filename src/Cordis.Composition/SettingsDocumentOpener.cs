using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cordis.Composition;

internal static class SettingsDocumentOpener
{
    internal static async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS())
        {
            await RunAsync("open", ["-t", path], cancellationToken);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var wsl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")) ||
                RuntimeInformation.OSDescription.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
            if (!wsl)
            {
                await RunAsync("xdg-open", [path], cancellationToken);
                return;
            }

            path = (await RunAsync("wslpath", ["-w", path], cancellationToken)).TrimEnd('\r', '\n');
            if (path.Length == 0)
                throw new InvalidOperationException("wslpath returned no Windows path.");
        }
        else if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The host has no supported native text opener.");

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        var target = new Uri(path, UriKind.Absolute).AbsoluteUri;
        target = Regex.Replace(
            target,
            "(?:%[89A-F][0-9A-F])+",
            match => Uri.UnescapeDataString(match.Value),
            RegexOptions.IgnoreCase);
        target = target.Replace(",", "%2C", StringComparison.Ordinal).Replace("=", "%3D", StringComparison.Ordinal);
        await RunAsync("explorer.exe", [target], cancellationToken, explorer: true);
    }

    private static async Task<string> RunAsync(
        string command,
        string[] arguments,
        CancellationToken cancellationToken,
        bool explorer = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = !explorer,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The native opener did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(explorer && process.ExitCode == 1))
                throw new InvalidOperationException($"{command} exited {process.ExitCode}: {error}");
        }

        return output;
    }
}
