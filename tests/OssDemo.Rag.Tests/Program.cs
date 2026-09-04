var contextMatches = RagService.SelectContextMatches(new[]
{
    new RagMatch("СТО", "1", "Первый", 0.8, 0.8),
    new RagMatch("СТО", "2", "Второй", 0.7, 0.7),
    new RagMatch("СТО", "3", "Третий", 0.6, 0.6),
    new RagMatch("ФЗ", "1", "Четвёртый", 0.5, 0.5),
    new RagMatch("ФЗ", "2", "Пятый", 0.4, 0.4)
}, maxMatches: 4, maxMatchesPerDocument: 2);
AssertTrue(contextMatches.Count == 4);
AssertTrue(contextMatches.Count(match => match.DocumentTitle == "СТО") == 2);

var nativeMatch = new RagMatch("Документ", "Раздел", "текст", 0.35, 0.35);
var debugResponse = RagDebugResponse.Build("тест", new RagSearchResult(new[] { nativeMatch }, false, Array.Empty<string>()));
AssertTrue(debugResponse.Contains("нативного гибридного поиска RAGify", StringComparison.Ordinal));
AssertTrue(debugResponse.Contains("Текст: текст", StringComparison.Ordinal));
AssertTrue(RagDebugResponse.Build("тест", RagSearchResult.Empty).Contains("чанки не найдены", StringComparison.Ordinal));
AssertTrue(RagDebugResponse.Build("тест", new RagSearchResult(new[] { nativeMatch }, false, Array.Empty<string>()), afterRerank: true)
    .Contains("после cross-encoder rerank", StringComparison.Ordinal));

var generalChatPrompt = ChatPrompt.BuildSystemMessage(string.Empty, false, Array.Empty<string>());
AssertTrue(generalChatPrompt.Contains("Поддерживай обычный диалог", StringComparison.Ordinal));
AssertTrue(generalChatPrompt.Contains("Не начинай ответ с просьбы уточнить вопрос", StringComparison.Ordinal));
AssertTrue(!generalChatPrompt.Contains("получить уточнение", StringComparison.Ordinal));
var documentOverviewPrompt = ChatPrompt.BuildSystemMessage("[S1] Документ: Изменение", true, Array.Empty<string>());
AssertTrue(documentOverviewPrompt.Contains("максимально полезный ответ", StringComparison.Ordinal));

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
    new RagMatch("Лесной кодекс", "Статья 15", "Статья 15. Районирование лесов. Лесорастительные зоны определяются в зависимости от природно-климатических условий.", 0.4, 0.4),
    new RagMatch("Приказ", "Таксация", "Таксация лесов проводится методом классов возраста.", 0.7, 0.7)
}, maxMatches: 2);
AssertTrue(rerankedMatches.Count == 2);
AssertTrue(rerankedMatches.All(match => double.IsFinite(match.RankingScore)));
AssertTrue(rerankedMatches[0].DocumentTitle == "Лесной кодекс");

Console.WriteLine("RAGify adapter checks passed.");

static void AssertTrue(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed.");
}
