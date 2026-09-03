var weakSemantic = new RagMatch("А", "Раздел", "текст", 0.2, false, false, 0.01);
var relevantSemantic = new RagMatch("Б", "Раздел", "текст", 0.35, false, false, 0.01);
AssertTrue(!weakSemantic.IsRelevant);
AssertTrue(relevantSemantic.IsRelevant);

var semanticMatches = new[]
{
    new RagMatch("СТО 16-005-2025", "Порядок", "Срок актуализации", 0.61, false, false, 0),
    new RagMatch("Федеральный закон № 7-ФЗ", "Статья 1", "Определение", 0.55, false, false, 0)
};
var lexicalMatches = new[]
{
    new RagMatch("Федеральный закон № 7-ФЗ", "Статья 16", "Плата за НВОС", 0, true, true, 0),
    new RagMatch("СТО 16-005-2025", "Порядок", "Срок актуализации", 0, true, true, 0)
};
var mergedMatches = RagService.MergeAndRank(semanticMatches, lexicalMatches, 8);
AssertTrue(mergedMatches.Count == 3);
AssertTrue(mergedMatches[0].DocumentTitle == "СТО 16-005-2025");
AssertTrue(mergedMatches.Single(match => match.Text == "Срок актуализации").HasLexicalMatch);

var contextMatches = RagService.SelectContextMatches(new[]
{
    new RagMatch("СТО", "1", "Первый", 0.8, false, false, 0),
    new RagMatch("СТО", "2", "Второй", 0.7, false, false, 0),
    new RagMatch("СТО", "3", "Третий", 0.6, false, false, 0),
    new RagMatch("ФЗ", "1", "Четвёртый", 0.5, false, false, 0),
    new RagMatch("ФЗ", "2", "Пятый", 0.4, false, false, 0)
}, maxMatches: 4, maxMatchesPerDocument: 2);
AssertTrue(contextMatches.Count == 4);
AssertTrue(contextMatches.Count(match => match.DocumentTitle == "СТО") == 2);

var debugResponse = RagDebugResponse.Build("тест", new RagSearchResult(new[] { relevantSemantic }, false, Array.Empty<string>()));
AssertTrue(debugResponse.Contains("## RAG: найденные чанки", StringComparison.Ordinal));
AssertTrue(debugResponse.Contains("Текст: текст", StringComparison.Ordinal));
AssertTrue(RagDebugResponse.Build("тест", RagSearchResult.Empty).Contains("чанки не найдены", StringComparison.Ordinal));

var clarificationPrompt = ChatPrompt.BuildSystemMessage(string.Empty, false, Array.Empty<string>(), null);
AssertTrue(clarificationPrompt.Contains("получить уточнение", StringComparison.Ordinal));
AssertTrue(clarificationPrompt.Contains("максимум два коротких предложения", StringComparison.Ordinal));

var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Models", "paraphrase-multilingual-MiniLM-L12-v2");
using var embeddingProvider = new MultilingualMiniLmEmbeddingProvider(
    Path.Combine(modelDirectory, "model_O1.onnx"),
    Path.Combine(modelDirectory, "tokenizer.json"));
var embedding = await embeddingProvider.EmbedAsync("Требования экологического законодательства");
AssertTrue(embedding.Length == 384);
AssertTrue(embedding.All(float.IsFinite));

Console.WriteLine("RAGify adapter checks passed.");

static void AssertTrue(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed.");
}
