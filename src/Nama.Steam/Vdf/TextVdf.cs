namespace Nama.Steam.Vdf;

/// <summary>
/// Minimal reader for Valve's text KeyValues format, as used by <c>loginusers.vdf</c> and
/// <c>libraryfolders.vdf</c>.
/// <para>
/// Read-only by design. Nama never rewrites these files, so there is no round-trip
/// obligation and no need for a full implementation — quoted keys, quoted values, nested
/// braces and <c>//</c> comments cover everything Steam actually writes.
/// </para>
/// </summary>
public static class TextVdf
{
    /// <summary>A parsed text-VDF object: ordered children, each either a value or a nested object.</summary>
    public sealed class Node
    {
        private readonly List<KeyValuePair<string, Node>> _children = [];

        public string? Value { get; init; }

        public IReadOnlyList<KeyValuePair<string, Node>> Children => _children;

        public bool IsObject => Value is null;

        internal void Add(string key, Node child) => _children.Add(new KeyValuePair<string, Node>(key, child));

        public Node? this[string key]
        {
            get
            {
                foreach (var (childKey, child) in _children)
                {
                    if (string.Equals(childKey, key, StringComparison.OrdinalIgnoreCase)) return child;
                }

                return null;
            }
        }

        public string? GetString(string key) => this[key]?.Value;

        public long? GetInt64(string key) =>
            long.TryParse(GetString(key), out var value) ? value : null;

        /// <summary>Nested objects in file order, as (key, node) pairs.</summary>
        public IEnumerable<(string Key, Node Node)> Objects()
        {
            foreach (var (key, child) in _children)
            {
                if (child.IsObject) yield return (key, child);
            }
        }
    }

    public static Node Parse(string text)
    {
        var position = 0;
        var root = new Node();
        ParseInto(root, text, ref position, depth: 0);
        return root;
    }

    public static Node ParseFile(string path) => Parse(File.ReadAllText(path));

    private static void ParseInto(Node parent, string text, ref int position, int depth)
    {
        if (depth > 32) throw new VdfFormatException("Text VDF nesting deeper than 32 levels.");

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
            if (key is null) return;

            SkipTrivia(text, ref position);
            if (position >= text.Length) return;

            if (text[position] == '{')
            {
                position++;
                var child = new Node();
                ParseInto(child, text, ref position, depth + 1);
                parent.Add(key, child);
            }
            else
            {
                var value = ReadToken(text, ref position);
                if (value is null) return;
                parent.Add(key, new Node { Value = value });
            }
        }
    }

    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            else if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] is not ('\n' or '\r')) position++;
            }
            else
            {
                return;
            }
        }
    }

    private static string? ReadToken(string text, ref int position)
    {
        SkipTrivia(text, ref position);
        if (position >= text.Length) return null;

        if (text[position] != '"')
        {
            // Unquoted token: runs to the next whitespace or brace.
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}'))
            {
                position++;
            }

            return position > start ? text[start..position] : null;
        }

        position++; // opening quote
        var builder = new System.Text.StringBuilder();

        while (position < text.Length && text[position] != '"')
        {
            if (text[position] == '\\' && position + 1 < text.Length)
            {
                position++;
                builder.Append(text[position] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    var c => c, // covers \\ and \"
                });
            }
            else
            {
                builder.Append(text[position]);
            }

            position++;
        }

        position++; // closing quote
        return builder.ToString();
    }
}
