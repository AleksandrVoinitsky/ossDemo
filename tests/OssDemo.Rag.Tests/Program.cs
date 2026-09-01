var resolution = DocumentReferenceParser.Parse("постановление 373");
AssertEqual("government_resolution", resolution.Kind);
AssertEqual("373", resolution.Number);

var governmentResolution = DocumentReferenceParser.Parse("Что за постановление Правительства РФ № 373?");
AssertEqual("government_resolution", governmentResolution.Kind);
AssertEqual("373", governmentResolution.Number);
AssertEqual("Правительство РФ", governmentResolution.Issuer);

var bareNumber = DocumentReferenceParser.Parse("373");
AssertEqual("373", bareNumber.Number);
AssertTrue(bareNumber.HasRequisites);

var gost = DocumentReferenceParser.Parse("ГОСТ Р 54104-2010");
AssertEqual("gost", gost.Kind);
AssertEqual("54104-2010", gost.Number);

var order = DocumentReferenceParser.Parse("Приказ Минприроды № 561");
AssertEqual("order", order.Kind);
AssertEqual("561", order.Number);
AssertEqual("Минприроды России", order.Issuer);

var freeText = DocumentReferenceParser.Parse("Как организовать производственный экологический контроль?");
AssertTrue(!freeText.HasRequisites);

var contextualQuery = ChatSearchQuery.Build(
    new[]
    {
        new ChatHistoryMessage("user", "Перечень значимых экологических аспектов филиала ПАО «Ташпом» при эксплуатации что это?"),
        new ChatHistoryMessage("assistant", "Неподтверждённая реплика модели не должна влиять на поиск."),
        new ChatHistoryMessage("user", "Карасайский")
    },
    "Расскажи где искать",
    4_000);
AssertTrue(contextualQuery.Contains("значимых экологических аспектов", StringComparison.Ordinal));
AssertTrue(contextualQuery.Contains("Карасайский", StringComparison.Ordinal));
AssertTrue(!contextualQuery.Contains("Неподтверждённая", StringComparison.Ordinal));

var first = new RankedChunk(Guid.NewGuid(), "А", "Раздел", "текст", 0);
var second = new RankedChunk(Guid.NewGuid(), "Б", "Раздел", "текст", 0);
var fused = ReciprocalRankFusion.Merge(new[] { first, second }, new[] { second, first }, take: 2);
AssertEqual(2, fused.Count);
AssertEqual(first.Id, fused[0].Id);

var weakSemantic = new RagMatch("А", "Раздел", "текст", 0.2, false, false, 0.01);
var lexicalMatch = new RagMatch("Б", "Раздел", "текст", 0.1, true, false, 0.01);
var hybridMatch = new RagMatch("Г", "Раздел", "текст", 0.35, true, false, 0.01);
var exactDocumentMatch = new RagMatch("В", "Раздел", "текст", 0.1, false, true, 0.01);
AssertTrue(!weakSemantic.IsRelevant);
AssertTrue(!lexicalMatch.IsRelevant);
AssertTrue(hybridMatch.IsRelevant);
AssertTrue(exactDocumentMatch.IsRelevant);
AssertTrue(!RagSearchSelection.HasEnoughRelevantMatches(new[] { new RagSearchResult(new[] { hybridMatch }, false, Array.Empty<string>()) }));
AssertTrue(!RagSearchSelection.HasEnoughRelevantMatches(new[] { new RagSearchResult(new[] { hybridMatch, hybridMatch }, false, Array.Empty<string>()) }));
AssertTrue(RagSearchSelection.HasEnoughRelevantMatches(new[] { new RagSearchResult(new[] { hybridMatch, exactDocumentMatch }, false, Array.Empty<string>()) }));

Console.WriteLine("RAG parser and RRF checks passed.");

static void AssertEqual<T>(T? expected, T? actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void AssertTrue(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed.");
}
