using System.Text;

namespace GameLingoAI.Infrastructure.Translation;

public static class PlainTextOutputSanitizer
{
    public static string Sanitize(string text, int maximumCharacters, int maximumLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        var output = new StringBuilder(Math.Min(text.Length, maximumCharacters));
        var lines = 1;
        var previousWhitespace = false;
        foreach (var character in text)
        {
            if (output.Length >= maximumCharacters)
            {
                break;
            }

            if (character is '\r' or '\n')
            {
                if (character == '\r' || output.Length == 0 || output[^1] == '\n')
                {
                    continue;
                }

                if (lines >= maximumLines)
                {
                    break;
                }

                while (output.Length > 0 && output[^1] == ' ')
                {
                    output.Length--;
                }

                output.Append('\n');
                lines++;
                previousWhitespace = false;
            }
            else if (char.IsControl(character) || char.GetUnicodeCategory(character) ==
                System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (output.Length > 0 && output[^1] != '\n' && !previousWhitespace)
                {
                    output.Append(' ');
                    previousWhitespace = true;
                }
            }
            else
            {
                output.Append(character);
                previousWhitespace = false;
            }
        }

        return output.ToString().Trim();
    }
}
