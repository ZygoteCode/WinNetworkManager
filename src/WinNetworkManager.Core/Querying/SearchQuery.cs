using System.Text;

namespace WinNetworkManager.Core.Querying;

/// <summary>Comparison used by a structured filter token.</summary>
public enum FilterOperator : byte
{
    /// <summary><c>key:value</c> — contains / matches.</summary>
    Matches,

    /// <summary><c>key=value</c> — exact.</summary>
    Equals,

    /// <summary><c>key&gt;value</c>.</summary>
    GreaterThan,

    /// <summary><c>key&gt;=value</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>key&lt;value</c>.</summary>
    LessThan,

    /// <summary><c>key&lt;=value</c>.</summary>
    LessOrEqual,
}

/// <summary>One structured filter, e.g. <c>port:443</c> or <c>-state:listen</c>.</summary>
/// <param name="Key">Lower-case key.</param>
/// <param name="Operator">Comparison.</param>
/// <param name="Value">Raw value text.</param>
/// <param name="Negated">True when prefixed with '-'.</param>
public readonly record struct SearchFilter(string Key, FilterOperator Operator, string Value, bool Negated);

/// <summary>A free-text term; negated terms exclude matches.</summary>
/// <param name="Text">The term.</param>
/// <param name="Negated">True when prefixed with '-'.</param>
public readonly record struct SearchTerm(string Text, bool Negated);

/// <summary>Parsed search box content.</summary>
public sealed class SearchQuery
{
    /// <summary>The empty query (matches everything).</summary>
    public static SearchQuery Empty { get; } = new([], []);

    /// <summary>Creates a query.</summary>
    public SearchQuery(IReadOnlyList<SearchTerm> terms, IReadOnlyList<SearchFilter> filters)
    {
        Terms = terms;
        Filters = filters;
    }

    /// <summary>Free-text terms (all must match).</summary>
    public IReadOnlyList<SearchTerm> Terms { get; }

    /// <summary>Structured filters (all must match).</summary>
    public IReadOnlyList<SearchFilter> Filters { get; }

    /// <summary>True when there is nothing to filter on.</summary>
    public bool IsEmpty => Terms.Count == 0 && Filters.Count == 0;

    /// <summary>
    /// Evaluates the query: every term must be found by <paramref name="matchesText"/> (case-insensitive contract
    /// left to the caller) and every filter must be accepted by <paramref name="matchesFilter"/>.
    /// Unknown filter keys are reported by <paramref name="matchesFilter"/> returning null and are ignored.
    /// </summary>
    public bool Evaluate(Func<string, bool> matchesText, Func<SearchFilter, bool?> matchesFilter)
    {
        ArgumentNullException.ThrowIfNull(matchesText);
        ArgumentNullException.ThrowIfNull(matchesFilter);
        foreach (var term in Terms)
        {
            if (matchesText(term.Text) == term.Negated)
            {
                return false;
            }
        }

        foreach (var filter in Filters)
        {
            bool? result = matchesFilter(filter);
            if (result is null)
            {
                continue;
            }

            if (result.Value == filter.Negated)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Parses search text: whitespace separated tokens, double quotes for phrases, '-' for negation and
/// <c>key:value</c>, <c>key=value</c>, <c>key&gt;value</c>, <c>key&gt;=value</c>, <c>key&lt;value</c>,
/// <c>key&lt;=value</c> for filters whose key is in the accepted set. Anything else (including IPv6 literals such as
/// <c>fe80::1</c>) is free text.
/// </summary>
public static class SearchQueryParser
{
    /// <summary>Parses <paramref name="text"/> accepting only the given filter keys (case-insensitive).</summary>
    public static SearchQuery Parse(string? text, IReadOnlySet<string> knownKeys)
    {
        ArgumentNullException.ThrowIfNull(knownKeys);
        if (string.IsNullOrWhiteSpace(text))
        {
            return SearchQuery.Empty;
        }

        var terms = new List<SearchTerm>();
        var filters = new List<SearchFilter>();
        foreach (string token in Tokenize(text))
        {
            bool negated = token.Length > 1 && token[0] == '-';
            string body = negated ? token[1..] : token;
            if (TryParseFilter(body, knownKeys, out string key, out FilterOperator op, out string value))
            {
                filters.Add(new SearchFilter(key, op, value, negated));
            }
            else if (body.Length > 0)
            {
                terms.Add(new SearchTerm(body, negated));
            }
        }

        return new SearchQuery(terms, filters);
    }

    private static bool TryParseFilter(string body, IReadOnlySet<string> knownKeys, out string key, out FilterOperator op, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        op = FilterOperator.Matches;
        int i = 0;
        while (i < body.Length && char.IsAsciiLetter(body[i]))
        {
            i++;
        }

        if (i == 0 || i >= body.Length)
        {
            return false;
        }

        string candidate = body[..i].ToLowerInvariant();
        if (!knownKeys.Contains(candidate))
        {
            return false;
        }

        int valueStart;
        switch (body[i])
        {
            case ':':
                op = FilterOperator.Matches;
                valueStart = i + 1;
                break;
            case '=':
                op = FilterOperator.Equals;
                valueStart = i + 1;
                break;
            case '>' when i + 1 < body.Length && body[i + 1] == '=':
                op = FilterOperator.GreaterOrEqual;
                valueStart = i + 2;
                break;
            case '>':
                op = FilterOperator.GreaterThan;
                valueStart = i + 1;
                break;
            case '<' when i + 1 < body.Length && body[i + 1] == '=':
                op = FilterOperator.LessOrEqual;
                valueStart = i + 2;
                break;
            case '<':
                op = FilterOperator.LessThan;
                valueStart = i + 1;
                break;
            default:
                return false;
        }

        if (valueStart >= body.Length)
        {
            return false;
        }

        key = candidate;
        value = body[valueStart..];
        return true;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
