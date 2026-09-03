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

AssertTrue(RagService.TryExtractArticleHeading("Статья 15. Районирование лесов о чем говорит?", out var articleHeading));
AssertTrue(articleHeading == "Статья 15. Районирование лесов");
AssertTrue(!RagService.TryExtractArticleHeading("Расскажите о районировании лесов", out _));

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

var rerankerDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../mmarco-mMiniLMv2-L12-H384-v1"));
using var reranker = new MultilingualCrossEncoderReranker(
    Path.Combine(rerankerDirectory, "onnx", "model_O1.onnx"),
    Path.Combine(rerankerDirectory, "tokenizer.json"));
var rerankedMatches = reranker.Rerank("Что говорит статья 15 о районировании лесов?", new[]
{
    new RagMatch("Лесной кодекс", "Статья 15", "Статья 15. Районирование лесов. Лесорастительные зоны определяются в зависимости от природно-климатических условий.", 0.4, false, false, 0),
    new RagMatch("Приказ", "Таксация", "Таксация лесов проводится методом классов возраста.", 0.7, false, false, 0)
}, maxMatches: 2);
AssertTrue(rerankedMatches.Count == 2);
AssertTrue(rerankedMatches.All(match => double.IsFinite(match.RankingScore)));
AssertTrue(rerankedMatches[0].DocumentTitle == "Лесной кодекс");

Console.WriteLine("RAGify adapter checks passed.");

static void AssertTrue(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed.");
}
