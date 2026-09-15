using System.Text.RegularExpressions;

internal sealed record RagCitationValidation(
    bool IsGrounded,
    IReadOnlyList<int> SourceIndexes,
    IReadOnlyList<int> InvalidSourceNumbers);

internal static partial class RagCitationValidator
{
    public const string UnverifiedAnswer = "Не удалось сформировать ответ с проверяемыми ссылками на найденные источники. Уточните вопрос или повторите запрос.";

    [GeneratedRegex(@"\[S(?<number>\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SourceMarkerRegex();

    public static RagCitationValidation Validate(string? answer, int sourceCount)
    {
        if (string.IsNullOrWhiteSpace(answer) || sourceCount <= 0)
            return new(false, [], []);

        var sourceNumbers = SourceMarkerRegex().Matches(answer)
            .Select(match => int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();
        var invalid = sourceNumbers.Where(number => number < 1 || number > sourceCount).ToArray();
        var validIndexes = sourceNumbers.Where(number => number >= 1 && number <= sourceCount).Select(number => number - 1).ToArray();
        return new(validIndexes.Length > 0 && invalid.Length == 0, validIndexes, invalid);
    }
}
