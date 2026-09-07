using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = Lucene.Net.Store.Directory;

internal sealed class LuceneSearchIndex : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;
    private const int MaximumFuzzyEdits = 2;
    private const string IdField = "id";
    private const string DocumentIdField = "documentId";
    private const string TitleField = "title";
    private const string HeadingField = "heading";
    private const string AliasesField = "aliases";
    private const string TextField = "text";

    private static readonly IReadOnlyDictionary<string, string[]> Synonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["пэк"] = ["производственный экологический контроль"],
            ["сэм"] = ["система экологического менеджмента"],
            ["нвос"] = ["негативное воздействие на окружающую среду"],
            ["чеклист"] = ["чек-лист", "контрольный лист", "лист проверки"],
            ["чек-лист"] = ["контрольный лист", "лист проверки"],
            ["несоответствие"] = ["нарушение"],
            ["нарушение"] = ["несоответствие"],
            ["акт-предписание"] = ["акт проверки", "предписание"]
        };

    private readonly Analyzer _analyzer = new StandardAnalyzer(Version);
    private readonly Directory _directory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LuceneSearchIndex(IConfiguration configuration)
    {
        var path = configuration["Search:LuceneDirectory"] ?? "/data/lucene-index";
        System.IO.Directory.CreateDirectory(path);
        _directory = FSDirectory.Open(path);
    }

    public async Task IndexDocumentAsync(
        string documentId,
        string sourceFileName,
        IReadOnlyList<LuceneIndexedChunk> chunks,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var writer = CreateWriter();
            writer.DeleteDocuments(new Term(DocumentIdField, documentId));
            foreach (var chunk in chunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.AddDocument(CreateDocument(documentId, sourceFileName, chunk));
            }

            writer.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var writer = CreateWriter();
            writer.DeleteAll();
            writer.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<LuceneSearchMatch>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<LuceneSearchMatch>();
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!DirectoryReader.IndexExists(_directory))
            {
                return Array.Empty<LuceneSearchMatch>();
            }

            using var reader = DirectoryReader.Open(_directory);
            var searcher = new IndexSearcher(reader) { Similarity = new BM25Similarity() };
            var hits = searcher.Search(BuildQuery(query), maxResults).ScoreDocs;
            return hits.Select(hit =>
            {
                var document = searcher.Doc(hit.Doc);
                return new LuceneSearchMatch(
                    document.Get(TitleField) ?? "Документ",
                    document.Get(HeadingField) ?? "Документ",
                    document.Get(TextField) ?? string.Empty,
                    hit.Score);
            }).Where(match => !string.IsNullOrWhiteSpace(match.Text)).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static IReadOnlyList<string> ExpandSynonyms(string query)
    {
        var values = new List<string> { query };
        foreach (var pair in Synonyms)
        {
            if (query.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                values.AddRange(pair.Value);
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IndexWriter CreateWriter() => new(_directory, new IndexWriterConfig(Version, _analyzer)
    {
        OpenMode = OpenMode.CREATE_OR_APPEND
    });

    private static Document CreateDocument(string documentId, string sourceFileName, LuceneIndexedChunk chunk)
    {
        var title = Path.GetFileNameWithoutExtension(sourceFileName);
        var aliases = $"{title} {sourceFileName} {NormalizeReference(sourceFileName)} {NormalizeReference(chunk.Heading)}";
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{documentId}\u001f{chunk.Heading}\u001f{chunk.Text}")));
        return new Document
        {
            new StringField(IdField, id, Field.Store.NO),
            new StringField(DocumentIdField, documentId, Field.Store.NO),
            new TextField(TitleField, title, Field.Store.YES),
            new TextField(HeadingField, chunk.Heading, Field.Store.YES),
            new TextField(AliasesField, aliases, Field.Store.NO),
            new TextField(TextField, chunk.Text, Field.Store.YES)
        };
    }

    private static Query BuildQuery(string query)
    {
        var result = new BooleanQuery();
        foreach (var expandedQuery in ExpandSynonyms(query))
        {
            foreach (var term in Tokenize(expandedQuery))
            {
                var termQuery = new BooleanQuery
                {
                    { Boost(new FuzzyQuery(new Term(TitleField, term), GetMaxEdits(term)), 8f), Occur.SHOULD },
                    { Boost(new FuzzyQuery(new Term(AliasesField, term), GetMaxEdits(term)), 7f), Occur.SHOULD },
                    { Boost(new FuzzyQuery(new Term(HeadingField, term), GetMaxEdits(term)), 4f), Occur.SHOULD },
                    { Boost(new FuzzyQuery(new Term(TextField, term), GetMaxEdits(term)), 1f), Occur.SHOULD }
                };
                result.Add(termQuery, Occur.SHOULD);
            }
        }

        return result;
    }

    private static IEnumerable<string> Tokenize(string value) => value
        .ToLowerInvariant()
        .Split([' ', '\t', '\r', '\n', '-', '_', '.', ',', ';', ':', '/', '\\', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
        .Where(term => term.Length >= 3)
        .Distinct(StringComparer.Ordinal);

    private static int GetMaxEdits(string term) => term.Length < 5 ? 1 : MaximumFuzzyEdits;

    private static Query Boost(Query query, float boost)
    {
        query.Boost = boost;
        return query;
    }

    private static string NormalizeReference(string value) => value
        .ToLowerInvariant()
        .Replace("№", "")
        .Replace(".", " ")
        .Replace("-", " ");

    public void Dispose()
    {
        _analyzer.Dispose();
        _directory.Dispose();
        _lock.Dispose();
    }
}

internal sealed record LuceneIndexedChunk(string Heading, string Text);
internal sealed record LuceneSearchMatch(string DocumentTitle, string SourceLabel, string Text, float Score);
