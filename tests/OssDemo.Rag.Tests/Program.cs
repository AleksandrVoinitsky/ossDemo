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

var first = new RankedChunk(Guid.NewGuid(), "А", "Раздел", "текст", 0);
var second = new RankedChunk(Guid.NewGuid(), "Б", "Раздел", "текст", 0);
var fused = ReciprocalRankFusion.Merge(new[] { first, second }, new[] { second, first }, take: 2);
AssertEqual(2, fused.Count);
AssertEqual(first.Id, fused[0].Id);

Console.WriteLine("RAG parser and RRF checks passed.");

static void AssertEqual<T>(T? expected, T? actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void AssertTrue(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed.");
}
