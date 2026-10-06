using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SqlBulkCopyHelper;

/// <summary>
/// Quoting and parsing of table and column names
/// </summary>
internal static class SqlNames
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Unquote(string part)
    {
        if (part.Length >= 2 && part[0] == '[' && part[^1] == ']')
        {
            return part[1..^1].Replace("]]", "]");
        }

        if (part.Length >= 2 && part[0] == '"' && part[^1] == '"')
        {
            return part[1..^1].Replace("\"\"", "\"");
        }

        return part;
    }

    /// <summary>
    /// Each part is quoted, also the ones that already are quoted with "". Empty parts (like in "db..Table") are kept empty.
    /// </summary>
    public static string QuoteMultipartName(List<string> parts) =>
        string.Join(".", parts.Select(part => part.Length > 0 ? Quote(Unquote(part)) : part));

    /// <summary>
    /// Splits "db.[dbo].[My.Table]" into "db", "[dbo]" and "[My.Table]". A dot inside [] or "" is part of the name.
    /// </summary>
    public static List<string> SplitMultipartName(string name)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? closingQuote = null;

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (closingQuote is not null)
            {
                current.Append(c);
                if (c != closingQuote)
                {
                    continue;
                }

                // ]] inside [] (or "" inside "") is an escaped quote and not the end of the part
                if (i + 1 < name.Length && name[i + 1] == closingQuote)
                {
                    current.Append(name[++i]);
                }
                else
                {
                    closingQuote = null;
                }
            }
            else if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                closingQuote = c switch
                {
                    '[' => ']',
                    '"' => '"',
                    _ => null
                };
                current.Append(c);
            }
        }

        if (closingQuote is not null)
        {
            throw new ArgumentException($"Table name '{name}' has a quoted part that is not terminated.", nameof(name));
        }

        parts.Add(current.ToString());
        return parts;
    }
}
