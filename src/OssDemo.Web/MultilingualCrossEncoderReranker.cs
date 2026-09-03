using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

internal sealed class MultilingualCrossEncoderReranker : IDisposable
{
    private const int MaximumCharactersPerInput = 1_500;
    private const int MaximumTokenCount = 512;
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;

    public MultilingualCrossEncoderReranker(string modelPath, string tokenizerPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = Tokenizer.FromFile(tokenizerPath);

        var requiredInputs = new[] { "input_ids", "attention_mask" };
        var missingInputs = requiredInputs.Where(input => !_session.InputMetadata.ContainsKey(input)).ToArray();
        if (missingInputs.Length > 0)
        {
            throw new InvalidOperationException($"ONNX-reranker не содержит обязательные входы: {string.Join(", ", missingInputs)}.");
        }
    }

    public IReadOnlyList<RagMatch> Rerank(string question, IReadOnlyList<RagMatch> matches, int maxMatches)
    {
        if (matches.Count == 0)
        {
            return matches;
        }

        var encodings = matches
            .Select(match => _tokenizer.Encode(
                Truncate(question),
                addSpecialTokens: true,
                input2: Truncate(match.Text),
                includeTypeIds: _session.InputMetadata.ContainsKey("token_type_ids"),
                includeAttentionMask: true)
                .First())
            .ToArray();
        var maximumLength = Math.Min(MaximumTokenCount, encodings.Max(encoding => encoding.Ids.Count));
        var inputIds = new DenseTensor<long>(new[] { matches.Count, maximumLength });
        var attentionMask = new DenseTensor<long>(new[] { matches.Count, maximumLength });
        DenseTensor<long>? tokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids")
            ? new DenseTensor<long>(new[] { matches.Count, maximumLength })
            : null;

        for (var row = 0; row < encodings.Length; row++)
        {
            var encoding = encodings[row];
            for (var column = 0; column < Math.Min(maximumLength, encoding.Ids.Count); column++)
            {
                inputIds[row, column] = encoding.Ids[column];
                attentionMask[row, column] = encoding.AttentionMask[column];
                if (tokenTypeIds is not null)
                {
                    tokenTypeIds[row, column] = encoding.TypeIds[column];
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        };
        if (tokenTypeIds is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using var results = _session.Run(inputs);
        var logits = results.FirstOrDefault(result => result.Name == "logits")?.AsTensor<float>()
            ?? throw new InvalidOperationException("ONNX-reranker не вернул logits.");

        return matches
            .Select((match, index) => match with { RankingScore = logits[index, 0] })
            .OrderByDescending(match => match.RankingScore)
            .ThenByDescending(match => match.Similarity)
            .Take(maxMatches)
            .ToArray();
    }

    public void Dispose()
    {
        _tokenizer.Dispose();
        _session.Dispose();
    }

    private static string Truncate(string value) =>
        value.Length <= MaximumCharactersPerInput ? value : value[..MaximumCharactersPerInput];
}
