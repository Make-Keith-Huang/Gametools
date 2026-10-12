using System.Text;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

public sealed class OcrTextNormalizer
{
    private const int MaximumTextCharacters = 4096;

    public static NormalizedSubtitle? Normalize(
        OcrResult result,
        StabilizationContext context,
        int maximumSpans)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSpans);

        var order = context.RegionOrder
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index);
        var spans = result.Spans
            .OrderBy(span => order.GetValueOrDefault(span.RegionId, int.MaxValue))
            .ThenBy(span => span.RegionId)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .Take(maximumSpans)
            .ToArray();
        if (spans.Length == 0)
        {
            return null;
        }

        var cjk = IsCjkLanguage(context.Language);
        var output = new StringBuilder(Math.Min(MaximumTextCharacters, spans.Sum(span => span.Text.Length + 1)));
        foreach (var region in spans.GroupBy(span => span.RegionId).OrderBy(group => order.GetValueOrDefault(group.Key, int.MaxValue)))
        {
            var lines = CreateLines(region);
            foreach (var line in lines)
            {
                if (output.Length > 0)
                {
                    output.Append('\n');
                }

                AppendLine(output, line.Spans, cjk);
                if (output.Length >= MaximumTextCharacters)
                {
                    output.Length = MaximumTextCharacters;
                    break;
                }
            }

            if (output.Length >= MaximumTextCharacters)
            {
                break;
            }
        }

        var text = ApplyReplacements(CollapseWhitespace(output.ToString()), context.ArtifactReplacements);
        text = NormalizePunctuationSpacing(text, cjk).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var left = spans.Min(span => span.Bounds.X);
        var top = spans.Min(span => span.Bounds.Y);
        var right = spans.Max(span => span.Bounds.X + span.Bounds.Width);
        var bottom = spans.Max(span => span.Bounds.Y + span.Bounds.Height);
        return new NormalizedSubtitle(
            text,
            spans,
            new NormalizedRegion(left, top, right - left, bottom - top),
            spans.Average(span => span.Confidence));
    }

    private static TextLine[] CreateLines(IEnumerable<OcrTextSpan> source)
    {
        var lines = new List<TextLine>();
        foreach (var span in source.OrderBy(item => item.Bounds.Y).ThenBy(item => item.Bounds.X))
        {
            var line = lines
                .Select(candidate => (candidate, overlap: VerticalOverlap(candidate.Bounds, span.Bounds)))
                .Where(item => item.overlap >= 0.45)
                .OrderByDescending(item => item.overlap)
                .Select(item => item.candidate)
                .FirstOrDefault();
            if (line is null)
            {
                lines.Add(new TextLine(span));
            }
            else
            {
                line.Add(span);
            }
        }

        return lines.OrderBy(line => line.Bounds.Y).ToArray();
    }

    private static void AppendLine(StringBuilder output, IReadOnlyList<OcrTextSpan> spans, bool cjk)
    {
        string? previous = null;
        foreach (var span in spans.OrderBy(item => item.Bounds.X))
        {
            var text = NormalizeFragment(span.Text, cjk);
            if (text.Length == 0)
            {
                continue;
            }

            if (previous is not null && NeedsSeparator(previous, text, cjk))
            {
                output.Append(' ');
            }

            output.Append(text);
            previous = text;
        }
    }

    private static string NormalizeFragment(string value, bool cjk)
    {
        var normalized = value.Normalize(cjk ? NormalizationForm.FormC : NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character switch
            {
                '“' or '”' when !cjk => '"',
                '‘' or '’' when !cjk => '\'',
                _ => character
            });
        }

        return builder.ToString().Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (character == '\n')
            {
                while (builder.Length > 0 && builder[^1] == ' ')
                {
                    builder.Length--;
                }

                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.Append('\n');
                }

                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0 && builder[^1] != '\n';
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string ApplyReplacements(string value, IReadOnlyList<OcrArtifactReplacement> replacements)
    {
        foreach (var replacement in replacements)
        {
            value = value.Replace(replacement.Artifact, replacement.Replacement, StringComparison.Ordinal);
        }

        return value;
    }

    private static string NormalizePunctuationSpacing(string value, bool cjk)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == ' ' && index + 1 < value.Length && IsClosingPunctuation(value[index + 1]))
            {
                continue;
            }

            if (character == ' ' && builder.Length > 0 && IsOpeningPunctuation(builder[^1]))
            {
                continue;
            }

            if (cjk && character == ' ' && builder.Length > 0 && index + 1 < value.Length &&
                IsCjkCharacter(builder[^1]) && IsCjkCharacter(value[index + 1]))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool NeedsSeparator(string left, string right, bool cjk)
    {
        var leftCharacter = left[^1];
        var rightCharacter = right[0];
        if (IsOpeningPunctuation(leftCharacter) || IsClosingPunctuation(rightCharacter) ||
            leftCharacter is '\'' or '-' or '—' || rightCharacter is '\'' or '-')
        {
            return false;
        }

        if (cjk && (IsClosingPunctuation(leftCharacter) || IsOpeningPunctuation(rightCharacter)))
        {
            return false;
        }

        return !cjk || !IsCjkCharacter(leftCharacter) || !IsCjkCharacter(rightCharacter);
    }

    private static bool IsOpeningPunctuation(char value) => "([{<「『【（“".Contains(value, StringComparison.Ordinal);

    private static bool IsClosingPunctuation(char value) => ".,!?;:%)]}>，。！？；：」』】）…".Contains(value, StringComparison.Ordinal);

    private static bool IsCjkLanguage(string language) =>
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    private static bool IsCjkCharacter(char value) =>
        value is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\u3040' and <= '\u30FF';

    private static double VerticalOverlap(NormalizedRegion left, NormalizedRegion right)
    {
        var overlap = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return overlap / Math.Min(left.Height, right.Height);
    }

    private sealed class TextLine
    {
        public TextLine(OcrTextSpan span)
        {
            Spans.Add(span);
            Bounds = span.Bounds;
        }

        public List<OcrTextSpan> Spans { get; } = new();

        public NormalizedRegion Bounds { get; private set; }

        public void Add(OcrTextSpan span)
        {
            Spans.Add(span);
            var left = Math.Min(Bounds.X, span.Bounds.X);
            var top = Math.Min(Bounds.Y, span.Bounds.Y);
            var right = Math.Max(Bounds.X + Bounds.Width, span.Bounds.X + span.Bounds.Width);
            var bottom = Math.Max(Bounds.Y + Bounds.Height, span.Bounds.Y + span.Bounds.Height);
            Bounds = new NormalizedRegion(left, top, right - left, bottom - top);
        }
    }
}
