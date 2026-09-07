using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

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

var vectorMatch = new RagMatch("Векторный документ", "Раздел", "Векторный фрагмент", 0.8, 0.8, IsVectorMatch: true);
var lexicalMatch = new RagMatch("Текстовый документ", "Раздел", "Текстовый фрагмент", 0.7, 0.7, IsLexicalMatch: true);
var sharedMatch = new RagMatch("Общий документ", "Раздел", "Общий фрагмент", 0.9, 0.9, IsVectorMatch: true, IsLexicalMatch: true);
var mergedMatches = RagService.MergeCandidates(new[] { vectorMatch, sharedMatch }, new[] { lexicalMatch, sharedMatch });
AssertTrue(mergedMatches.Count == 3);
AssertTrue(mergedMatches.Single(match => match.Text == "Общий фрагмент").IsVectorMatch);
AssertTrue(mergedMatches.Single(match => match.Text == "Общий фрагмент").IsLexicalMatch);
var finalMatches = RagService.SelectFinalMatches(mergedMatches, maxMatches: 4, maxMatchesPerSearch: 2);
AssertTrue(finalMatches.Any(match => match.IsVectorMatch));
AssertTrue(finalMatches.Any(match => match.IsLexicalMatch));

var nativeMatch = new RagMatch("Документ", "Раздел", "текст", 0.35, 0.35);
var debugResponse = RagDebugResponse.Build("тест", new RagSearchResult(new[] { nativeMatch }, false, Array.Empty<string>()));
AssertTrue(debugResponse.Contains("нативного поиска RAGify", StringComparison.Ordinal));
AssertTrue(debugResponse.Contains("Текст: текст", StringComparison.Ordinal));
AssertTrue(RagDebugResponse.Build("тест", RagSearchResult.Empty).Contains("чанки не найдены", StringComparison.Ordinal));
AssertTrue(RagDebugResponse.Build("тест", new RagSearchResult(new[] { nativeMatch }, false, Array.Empty<string>()), afterRerank: true)
    .Contains("после cross-encoder rerank", StringComparison.Ordinal));

var documentOverviewPrompt = ChatPrompt.BuildSystemMessage("[S1] Документ: Изменение", true, Array.Empty<string>());
AssertTrue(documentOverviewPrompt.Contains("максимально полезный ответ", StringComparison.Ordinal));
AssertThrows(() => ChatPrompt.BuildSystemMessage(string.Empty, false, Array.Empty<string>()));
var searchRewritePrompt = ChatPrompt.BuildSearchRewriteMessage();
AssertTrue(searchRewritePrompt.Contains("ровно 8 разных", StringComparison.Ordinal));
AssertTrue(searchRewritePrompt.Contains("Не отвечай на вопрос", StringComparison.Ordinal));
var searchRewrites = RagService.ParseSearchRewrites("[\"срок актуализации чек-листа\", \"периодичность пересмотра чек-листа\"]");
AssertTrue(searchRewrites.Count == 2);
AssertTrue(searchRewrites[1] == "периодичность пересмотра чек-листа");
var uniqueSearchRewrites = RagService.ParseSearchRewrites("[\"запрос\", \"Запрос\", \"другой запрос\"]");
AssertTrue(uniqueSearchRewrites.SequenceEqual(new[] { "запрос", "другой запрос" }));

await using (var metadataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
    ---
    title: "Лесной кодекс Российской Федерации"
    source_path: "Законодательные/Лесной кодекс.md"
    category: "Законодательные документы"
    document_type: "federal_law"
    processed_by: "Docling 2.123.1"
    ---
    # Лесной кодекс
    """)))
{
    var metadata = KnowledgeDocumentMetadata.FromMarkdown(metadataStream, "repository/Лесной кодекс.md");
    AssertTrue(metadata.Title == "Лесной кодекс Российской Федерации");
    AssertTrue(metadata.Category == "Законодательные документы");
    AssertTrue(metadata.DocumentType == "federal_law");
}

var reciprocalRankMatches = RagService.MergeCandidates(
    new[] { new RagMatch("Общий", "Статья 1", "Текст", 0.8, 0.8, IsVectorMatch: true) },
    new[]
    {
        new RagMatch("Лексический", "Раздел", "Другой текст", 0.9, 0.9, IsLexicalMatch: true),
        new RagMatch("Общий", "Статья 1", "Текст", 0.7, 0.7, IsLexicalMatch: true)
    });
AssertTrue(reciprocalRankMatches[0].DocumentTitle == "Общий");
AssertTrue(reciprocalRankMatches[0].IsVectorMatch && reciprocalRankMatches[0].IsLexicalMatch);

var luceneDirectory = Path.Combine(Path.GetTempPath(), $"ossdemo-lucene-{Guid.NewGuid():N}");
try
{
    var luceneConfiguration = new TestConfiguration(new Dictionary<string, string?> { ["Search:LuceneDirectory"] = luceneDirectory });
    using var luceneIndex = new LuceneSearchIndex(luceneConfiguration);
    await luceneIndex.IndexDocumentAsync("service", "Справочник по сервису АИ ООС.md", new[]
    {
        new LuceneIndexedChunk("Производственный экологический контроль", "ПЭК применяется для контроля соблюдения природоохранных требований.", "Справочник по сервису", "Корпоративные документы", "guide"),
        new LuceneIndexedChunk("Рекультивация земель", "Рекультивация нарушенных земель проводится с учетом местных условий.")
    }, CancellationToken.None);
    AssertTrue((await luceneIndex.SearchAsync("ПЭК", 5, CancellationToken.None)).Any(match => match.Text.Contains("ПЭК", StringComparison.Ordinal)));
    AssertTrue((await luceneIndex.SearchAsync("рекултивация", 5, CancellationToken.None)).Any(match => match.SourceLabel.Contains("Рекультивация", StringComparison.Ordinal)));
    AssertTrue((await luceneIndex.SearchAsync("корпоративные", 5, CancellationToken.None)).Any(match => match.Category == "Корпоративные документы"));
}
finally
{
    if (Directory.Exists(luceneDirectory)) Directory.Delete(luceneDirectory, recursive: true);
}

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

static void AssertThrows(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("Expected InvalidOperationException.");
}

sealed class TestConfiguration(IReadOnlyDictionary<string, string?> values) : IConfiguration
{
    public string? this[string key]
    {
        get => values.GetValueOrDefault(key);
        set => throw new NotSupportedException();
    }

    public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();
    public IChangeToken GetReloadToken() => throw new NotSupportedException();
    public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
}
