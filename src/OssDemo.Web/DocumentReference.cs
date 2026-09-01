using System.Globalization;
using System.Text.RegularExpressions;

internal sealed record DocumentReference(
    string? Kind,
    string? Number,
    string? Issuer,
    DateOnly? Date,
    string SearchTerms)
{
    public bool HasRequisites => !string.IsNullOrWhiteSpace(Number) || Date is not null;
}

internal static partial class DocumentReferenceParser
{
    public static DocumentReference Parse(string value)
    {
        var text = value.Trim();
        var kind = FindKind(text);
        var number = NumberPattern().Match(text).Groups["number"].Value;
        if (string.IsNullOrWhiteSpace(number)) number = ContextualNumberPattern().Match(text).Groups["number"].Value;
        if (string.IsNullOrWhiteSpace(number) && BareNumberPattern().Match(text) is { Success: true } bareNumber)
        {
            number = bareNumber.Groups["number"].Value;
        }

        var issuer = FindIssuer(text);
        var date = TryParseDate(DatePattern().Match(text).Groups["date"].Value);
        return new DocumentReference(kind, EmptyToNull(number), issuer, date, BuildSearchTerms(text, kind, number, issuer, date));
    }

    private static string? FindKind(string text)
    {
        if (Contains(text, "гост")) return "gost";
        if (Contains(text, "сто")) return "sto";
        if (Contains(text, "федеральн") && Contains(text, "закон")) return "federal_law";
        if (Contains(text, "постановлен")) return "government_resolution";
        if (Contains(text, "приказ")) return "order";
        return null;
    }

    private static string? FindIssuer(string text)
    {
        foreach (var (needle, value) in new[]
                 {
                     ("правительств", "Правительство РФ"),
                     ("минприроды", "Минприроды России"),
                     ("минэкономразвит", "Минэкономразвития России"),
                     ("росприроднадзор", "Росприроднадзор"),
                     ("минсельхоз", "Минсельхоз России")
                 })
        {
            if (Contains(text, needle)) return value;
        }

        return null;
    }

    private static string BuildSearchTerms(string text, string? kind, string number, string? issuer, DateOnly? date)
    {
        var result = text;
        if (!string.IsNullOrWhiteSpace(number)) result = result.Replace(number, " ", StringComparison.Ordinal);
        if (date is not null) result = DatePattern().Replace(result, " ");
        foreach (var term in new[] { "гост", "сто", "федеральный закон", "постановление", "приказ", issuer ?? string.Empty })
        {
            result = Regex.Replace(result, Regex.Escape(term), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return Regex.Replace(result, @"\s+", " ").Trim();
    }

    private static DateOnly? TryParseDate(string value) => DateOnly.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var date) ? date : null;
    private static bool Contains(string value, string fragment) => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"(?:№|\bN\b)\s*(?<number>[0-9]+(?:[-/][0-9А-Яа-яA-Za-z]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b(?:гост(?:\s+р)?|сто|федеральн\w*\s+закон|постановлен\w*|приказ)\s+(?:правительств\w*(?:\s+рф)?\s*)?(?<number>\d+[\dА-Яа-яA-Za-z/-]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextualNumberPattern();

    [GeneratedRegex(@"^\s*(?<number>\d{1,8}(?:[-/]\d+[А-Яа-яA-Za-z]*)?)\s*[?!.]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumberPattern();

    [GeneratedRegex(@"(?<date>\d{1,2}[.]\d{1,2}[.]\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
