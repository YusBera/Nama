using System.Text;

namespace Nama.SteamIntegration.Vdf;

/// <summary>
/// Minimal parser for Valve's text KeyValues format, used by <c>loginusers.vdf</c> and
/// <c>libraryfolders.vdf</c>. Nama only reads these files, so no writer is provided.
/// </summary>
public static class TextVdf
{
    /// <summary>Parses text KeyValues into a synthetic root object.</summary>
    /// <exception cref="InvalidDataException">The document has unbalanced braces or an unterminated string.</exception>
    public static VdfNode Parse(string text)
    {
        var root = VdfNode.NewObject();
        var position = 0;
        ParseInto(text, ref position, root, depth: 0);
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private static void ParseInto(string text, ref int position, VdfNode parent, int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("KeyValues nesting is implausibly deep; the file is probably corrupt.");

        while (true)
        {
            SkipTrivia(text, ref position);
            if (position >= text.Length) return;

            if (text[position] == '}')
            {
                position++;
                return;
            }

            var key = ReadToken(text, ref position);
            SkipTrivia(text, ref position);

            if (position >= text.Length) return;

            if (text[position] == '{')
            {
                position++;
                var child = VdfNode.NewObject();
                ParseInto(text, ref position, child, depth + 1);
                parent.Add(key, child);
            }
            else
            {
                parent.Add(key, VdfNode.FromString(ReadToken(text, ref position)));
            }
        }
    }

    /// <summary>Skips whitespace, <c>//</c> line comments and conditional <c>[$WIN32]</c> tags.</summary>
    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];

            if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
            }
            else if (c == '[')
            {
                while (position < text.Length && text[position] != ']') position++;
                if (position < text.Length) position++;
            }
            else
            {
                return;
            }
        }
    }

    private static string ReadToken(string text, ref int position)
    {
        if (text[position] == '"')
        {
            position++;
            var sb = new StringBuilder();

            while (position < text.Length)
            {
                var c = text[position];

                if (c == '\\' && position + 1 < text.Length)
                {
                    position++;
                    sb.Append(text[position] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        var other => other,
                    });
                    position++;
                    continue;
                }

                if (c == '"')
                {
                    position++;
                    return sb.ToString();
                }

                sb.Append(c);
                position++;
            }

            throw new InvalidDataException("Unterminated quoted string in KeyValues document.");
        }

        // Unquoted token, terminated by whitespace or a brace.
        var start = position;
        while (position < text.Length &&
               !char.IsWhiteSpace(text[position]) &&
               text[position] != '{' &&
               text[position] != '}')
        {
            position++;
        }

        return text[start..position];
    }
}
