var weakSemantic = new RagMatch("А", "Раздел", "текст", 0.2, false, false, 0.01);
var relevantSemantic = new RagMatch("Б", "Раздел", "текст", 0.35, false, false, 0.01);
AssertTrue(!weakSemantic.IsRelevant);
AssertTrue(relevantSemantic.IsRelevant);

var debugResponse = RagDebugResponse.Build("тест", new RagSearchResult(new[] { relevantSemantic }, false, Array.Empty<string>()));
AssertTrue(debugResponse.Contains("## RAG: найденные чанки", StringComparison.Ordinal));
AssertTrue(debugResponse.Contains("Текст: текст", StringComparison.Ordinal));
AssertTrue(RagDebugResponse.Build("тест", RagSearchResult.Empty).Contains("чанки не найдены", StringComparison.Ordinal));

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
