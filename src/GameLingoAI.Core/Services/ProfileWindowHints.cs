using System.Diagnostics;
using System.Text.RegularExpressions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

public sealed record ProfileWindowSuggestions(IReadOnlyList<CaptureWindow> Windows, bool LimitReached);

/// <summary>Metadata-only bounded suggestions. Results confer no capture authorization.</summary>
public static class ProfileWindowHints
{
    public static ProfileWindowSuggestions Find(GameProfile profile, IReadOnlyList<CaptureWindow> windows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GameProfileValidator.Validate(profile);
        ArgumentNullException.ThrowIfNull(windows);
        var process = profile.Settings.ProcessHint;
        var pattern = profile.Settings.WindowTitlePattern;
        if (process is null && pattern is null)
        { return new(Array.Empty<CaptureWindow>(), false); }
        var regex = pattern is null ? null : new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
        var matches = new List<CaptureWindow>();
        var watch = Stopwatch.StartNew();
        var limited = windows.Count > 256;
        foreach (var window in windows.Take(256))
        {
            token.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds >= 100 || matches.Count >= 20)
            { limited = true; break; }
            if (process is not null && !string.Equals(process, window.ProcessDisplayHint, StringComparison.OrdinalIgnoreCase))
            { continue; }
            if (window.Title.Length > 4096)
            { limited = true; continue; }
            var titleMatches = regex is null;
            try
            { titleMatches |= regex?.IsMatch(window.Title) == true; }
            catch (RegexMatchTimeoutException)
            { limited = true; titleMatches = string.Equals(pattern, window.Title, StringComparison.Ordinal); }
            if (titleMatches)
            { matches.Add(window); }
        }
        return new(matches.AsReadOnly(), limited);
    }
}
