using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RAGify.Abstractions;
using Tokenizers.HuggingFace.Tokenizer;

internal sealed class MultilingualMiniLmEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int EmbeddingDimension = 384;
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;

    public MultilingualMiniLmEmbeddingProvider(string modelPath, string tokenizerPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = Tokenizer.FromFile(tokenizerPath);

        var requiredInputs = new[] { "input_ids", "attention_mask", "token_type_ids" };
        var missingInputs = requiredInputs.Where(input => !_session.InputMetadata.ContainsKey(input)).ToArray();
        if (missingInputs.Length > 0)
        {
            throw new InvalidOperationException($"ONNX-модель не содержит обязательные входы: {string.Join(", ", missingInputs)}.");
        }
    }

    public int Dimension => EmbeddingDimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.Run(() => Embed(text), cancellationToken);

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var embeddings = new float[texts.Count][];
        for (var index = 0; index < texts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings[index] = await EmbedAsync(texts[index], cancellationToken);
        }

        return embeddings;
    }

    public void Dispose()
    {
        _tokenizer.Dispose();
        _session.Dispose();
    }

    private float[] Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Текст для векторизации не должен быть пустым.", nameof(text));
        }

        var encoding = _tokenizer.Encode(text, addSpecialTokens: true, includeTypeIds: true, includeAttentionMask: true).First();
        var tokenCount = encoding.Ids.Count;
        var shape = new[] { 1, tokenCount };
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", CreateTensor(encoding.Ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", CreateTensor(encoding.AttentionMask, shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", CreateTensor(encoding.TypeIds, shape))
        };

        using var results = _session.Run(inputs);
        var output = results.FirstOrDefault(result => result.Name == "last_hidden_state")
            ?? throw new InvalidOperationException("ONNX-модель не вернула last_hidden_state.");
        var hiddenStates = output.AsTensor<float>();
        var embedding = new float[EmbeddingDimension];
        var attendedTokens = 0;

        for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
        {
            if (encoding.AttentionMask[tokenIndex] == 0)
            {
                continue;
            }

            attendedTokens++;
            for (var dimensionIndex = 0; dimensionIndex < EmbeddingDimension; dimensionIndex++)
            {
                embedding[dimensionIndex] += hiddenStates[0, tokenIndex, dimensionIndex];
            }
        }

        if (attendedTokens == 0)
        {
            throw new InvalidOperationException("Токенизатор не создал ни одного значимого токена.");
        }

        var squaredLength = 0d;
        for (var index = 0; index < embedding.Length; index++)
        {
            embedding[index] /= attendedTokens;
            squaredLength += embedding[index] * embedding[index];
        }

        var length = Math.Sqrt(squaredLength);
        if (length == 0)
        {
            throw new InvalidOperationException("ONNX-модель вернула нулевой вектор.");
        }

        for (var index = 0; index < embedding.Length; index++)
        {
            embedding[index] /= (float)length;
        }

        return embedding;
    }

    private static DenseTensor<long> CreateTensor(IReadOnlyList<uint> values, int[] shape) =>
        new(values.Select(value => (long)value).ToArray(), shape);
}
