using System.Globalization;
using System.Text;
using GameLingoAI.Core.Configuration;

namespace GameLingoAI.Core.Models;

public enum OverlayTextAlignment { Left, Center, Right }
public enum OverlayAnchor { Top, Center, Bottom }

/// <summary>Client bounds are physical screen pixels, including negative multi-monitor coordinates.</summary>
public sealed record OverlayTargetState(PixelBounds ClientBounds, uint Dpi, bool IsVisible, bool IsForeground = true);

/// <summary>Pure rendering limits and placement; fonts are names, never file/URI references.</summary>
public static class OverlayLayout
{
    public static IReadOnlyList<ConfigurationError> Validate(OverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<ConfigurationError>();
        void Range(string field, double value, double min, double max)
        {
            if (!double.IsFinite(value) || value < min || value > max)
            {
                errors.Add(new ConfigurationError($"overlay.{field}", $"Value must be between {min} and {max}."));
            }
        }

        Range("fontSize", options.FontSize, 8, 96);
        Range("outlineThickness", options.OutlineThickness, 0, 4);
        Range("maximumLines", options.MaximumLines, 1, 6);
        Range("maximumCharacters", options.MaximumCharacters, 1, 4_000);
        Range("backgroundOpacity", options.BackgroundOpacity, 0, 1);
        Range("maximumWidthRatio", options.MaximumWidthRatio, 0.1, 1);
        Range("horizontalOffsetDips", options.HorizontalOffsetDips, -500, 500);
        Range("verticalOffsetDips", options.VerticalOffsetDips, -500, 500);
        if (string.IsNullOrWhiteSpace(options.FontFamily) || options.FontFamily.Length > 64 ||
            options.FontFamily.Any(character => !(char.IsLetterOrDigit(character) || character is ' ' or '-' or '_')))
        {
            errors.Add(new ConfigurationError("overlay.fontFamily", "Use a local font family name, not a path or URI."));
        }
        if (!IsColor(options.Foreground) || !IsColor(options.OutlineColor))
        {
            errors.Add(new ConfigurationError("overlay.colors", "Colors must use #RRGGBB."));
        }
        if (!Enum.IsDefined(options.Alignment) || !Enum.IsDefined(options.Anchor))
        {
            errors.Add(new ConfigurationError("overlay.layout", "Choose a supported alignment and anchor."));
        }
        return errors;
    }

    public static PixelBounds Place(PixelBounds client, uint effectiveOverlayDpi, OverlayOptions options)
    {
        if (effectiveOverlayDpi == 0 || Validate(options).Count != 0)
        {
            throw new ArgumentException("Invalid overlay layout or DPI.");
        }
        var scale = effectiveOverlayDpi / 96d;
        var width = Math.Clamp((int)Math.Round(client.Width * options.MaximumWidthRatio), 1, client.Width);
        var height = Math.Clamp((int)Math.Ceiling((options.MaximumLines * options.FontSize * 1.35 + 24) * scale), 1, client.Height);
        var left = client.Left + (client.Width - width) / 2d + options.HorizontalOffsetDips * scale;
        var top = options.Anchor switch
        {
            OverlayAnchor.Top => client.Top,
            OverlayAnchor.Center => client.Top + (client.Height - height) / 2d,
            _ => client.Top + client.Height - height
        };
        top += options.VerticalOffsetDips * scale;
        return new PixelBounds((int)Math.Round(Math.Clamp(left, client.Left, (double)client.Left + client.Width - width)),
            (int)Math.Round(Math.Clamp(top, client.Top, (double)client.Top + client.Height - height)), width, height);
    }

    /// <summary>Preserves literal markup as text, strips unsafe controls, and never cuts a UTF-16 surrogate pair.</summary>
    public static string Sanitize(string text, OverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Validate(options).Count != 0)
        { throw new ArgumentException("Invalid overlay rendering options."); }
        var output = new StringBuilder(Math.Min(text.Length, options.MaximumCharacters));
        var lines = 1;
        var lastWasCarriageReturn = false;
        // A pathological all-control input must not bypass the visible-character budget forever.
        foreach (var rune in text.AsSpan(0, Math.Min(text.Length, 32_000)).EnumerateRunes())
        {
            if (rune.Value is '\r' or '\n')
            {
                if (rune.Value == '\n' && lastWasCarriageReturn)
                { lastWasCarriageReturn = false; continue; }
                lastWasCarriageReturn = rune.Value == '\r';
                if (++lines > options.MaximumLines || output.Length >= options.MaximumCharacters)
                { break; }
                output.Append('\n');
                continue;
            }
            lastWasCarriageReturn = false;
            if (rune.Value == '\t')
            {
                if (output.Length >= options.MaximumCharacters)
                { break; }
                output.Append(' ');
                continue;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                continue;
            }
            if (output.Length + rune.Utf16SequenceLength > options.MaximumCharacters)
            { break; }
            output.Append(rune.ToString());
        }
        return output.ToString().Trim();
    }

    private static bool IsColor(string color)
    {
        if (color is not { Length: 7 } || color[0] != '#')
        { return false; }
        for (var index = 1; index < color.Length; index++)
        {
            if (!char.IsAsciiHexDigit(color[index]))
            { return false; }
        }
        return true;
    }
}

/// <summary>Session binding prevents updates from an old session even if their sequence is larger.</summary>
public sealed class OverlayDisplayState
{
    private SessionId? _session;
    private long _sequence = -1;
    public TranslationResult? Current { get; private set; }
    public void Bind(SessionId session)
    {
        _session = session;
        _sequence = -1;
        Current = null;
    }
    public bool TryUpdate(TranslationResult result)
    {
        if (result.Metadata.SessionId != _session || result.Sequence <= _sequence)
        { return false; }
        _sequence = result.Sequence;
        Current = result;
        return true;
    }
    public void Hide() => Current = null;
    public void Unbind() { _session = null; _sequence = -1; Current = null; }
}
