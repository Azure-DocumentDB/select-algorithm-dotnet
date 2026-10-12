using SelectAlgorithm.Models;
using SelectAlgorithm.Services;
using MongoDB.Bson;

namespace SelectAlgorithm.Tests;

public class WorkflowTests
{
    internal static Fixture SyntheticFixture()
    {
        var query = new double[1536]; query[0] = 1;
        var docs = Enumerable.Range(1, 50).Select(i =>
        {
            var vector = new double[1536]; vector[0] = 1.0 / i; vector[1] = (i - 1.0) / i;
            return new BsonDocument { ["_id"] = "dotnet-hotel-" + i, ["HotelId"] = i.ToString(),
                ["HotelName"] = "synthetic " + i, [Fixture.VectorField] = new BsonArray(vector) };
        }).ToList();
        return new Fixture(docs, query);
    }
    internal static ComparisonCase Case => new(VectorIndexType.IVF, SimilarityMetric.COS);
    public static IEnumerable<object[]> Cases => ComparisonCase.All.Select(c => new object[] { c.Algorithm, c.Metric });

    [Theory, MemberData(nameof(Cases))]
    public async Task EnsureIndexAndRerunPreserveUserEditsAndUnrelatedRecords(VectorIndexType algorithm, SimilarityMetric metric)
    {
        var item = new ComparisonCase(algorithm, metric); var fixture = SyntheticFixture(); var fake = new FakeStore();
        var userEdit = fixture.Documents[0].DeepClone().AsBsonDocument; userEdit["HotelName"] = "user edited";
        fake.Documents.Add("dotnet-hotel-1", userEdit);
        fake.Documents.Add("unrelated", new BsonDocument("_id", "unrelated"));
        var service = new VectorSearchService(fake);
        var first = await service.RunAsync(item, fixture, 7, true, default);
        var second = await service.RunAsync(item, fixture, 2, true, default);
        Assert.Equal(49, first.Inserted); Assert.Equal(0, second.Inserted); Assert.Equal(51, fake.Documents.Count);
        Assert.Equal("user edited", fake.Documents["dotnet-hotel-1"]["HotelName"]);
        Assert.True(fake.Documents.ContainsKey("unrelated")); Assert.Single(fake.Indexes); Assert.Equal(1, fake.CreateCalls);
        Assert.True(VectorSchema.CheckCatalog(fake.Indexes, VectorSchema.Index(item)));
        Assert.Equal(2, second.Results); Assert.Equal(2, fake.LastPipeline![0]["$search"]["cosmosSearch"]["k"].AsInt32);
    }
    [Fact] public async Task PopulatedCollectionStillGetsMissingIndex()
    {
        var fake = new FakeStore(); var fixture = SyntheticFixture();
        foreach (var doc in fixture.Documents) fake.Documents.Add(doc["_id"].AsString, doc);
        var result = await new VectorSearchService(fake).RunAsync(Case, fixture, 5, true, default);
        Assert.Equal(0, result.Inserted); Assert.Single(fake.Indexes);
    }
    [Fact] public async Task InsertionFailureIsNeverSwallowed()
    {
        var fake = new FakeStore { InsertFailure = new InvalidOperationException("sentinel secret") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VectorSearchService(fake).RunAsync(Case, SyntheticFixture(), 5, true, default));
        Assert.Equal(0, fake.CreateCalls); Assert.Null(fake.LastPipeline);
    }
    [Fact] public async Task UnrelatedDuplicateKeyFailureIsNotSuccess() => await Assert.ThrowsAsync<DuplicateDocumentException>(() =>
        new VectorSearchService(new FakeStore { InsertFailure = new DuplicateDocumentException() }).RunAsync(Case, SyntheticFixture(), 5, true, default));
    [Fact] public async Task SameIdInsertRaceIsPreserved()
    {
        var fake = new FakeStore { RaceInsert = true };
        var result = await new VectorSearchService(fake).RunAsync(Case, SyntheticFixture(), 5, true, default);
        Assert.Equal(0, result.Inserted); Assert.Equal(50, fake.Documents.Count);
    }
    [Fact] public async Task NoOptInMeansNoStoreCalls()
    {
        var fake = new FakeStore();
        await Assert.ThrowsAsync<ConfigurationException>(() => new VectorSearchService(fake).RunAsync(Case, SyntheticFixture(), 5, false, default));
        Assert.Equal(0, fake.CatalogCalls); Assert.Empty(fake.Documents);
    }
    [Theory][InlineData("name")][InlineData("dimensions")][InlineData("kind")][InlineData("similarity")][InlineData("field")]
    public async Task ConflictingIndexesFailBeforeInsertAndNeverDrop(string field)
    {
        var bad = VectorSchema.Index(Case);
        if (field == "name") bad["name"] = "user_index";
        else if (field == "field") bad["key"] = new BsonDocument("OtherField", "cosmosSearch");
        else bad["cosmosSearchOptions"][field] = field == "dimensions" ? new BsonInt32(42) : new BsonString("wrong");
        var fake = new FakeStore(); fake.Indexes.Add(bad);
        await Assert.ThrowsAsync<IndexConflictException>(() => new VectorSearchService(fake).RunAsync(Case, SyntheticFixture(), 5, true, default));
        Assert.Empty(fake.Documents); Assert.Same(bad, fake.Indexes[0]); Assert.Equal(0, fake.CreateCalls);
    }
    [Theory][InlineData("unique")][InlineData("sparse")][InlineData("hidden")][InlineData("partialFilterExpression")]
    [InlineData("expireAfterSeconds")][InlineData("collation")][InlineData("unknownSemantic")]
    public void AdditionalIndexSemanticsAreConflicts(string option)
    {
        var index = VectorSchema.Index(Case); index[option] = true;
        Assert.Throws<IndexConflictException>(() => VectorSchema.CheckCatalog([index], VectorSchema.Index(Case)));
    }
    [Fact] public void FullCatalogIsCheckedAfterMatchingIndex()
    {
        var expected = VectorSchema.Index(Case); var conflict = expected.DeepClone().AsBsonDocument; conflict["name"] = "unrelated_vector";
        Assert.Throws<IndexConflictException>(() => VectorSchema.CheckCatalog([expected, conflict], expected));
    }
    [Theory][InlineData("{}")] [InlineData("{name:'broken',key:1}")]
    public void MalformedCatalogFails(string json) => Assert.Throws<IndexConflictException>(() => VectorSchema.CheckCatalog([BsonDocument.Parse(json)], VectorSchema.Index(Case)));
    [Theory][InlineData("unique")][InlineData("sparse")][InlineData("hidden")]
    public void MalformedBooleanCatalogSemanticsFail(string key)
    {
        var actual = VectorSchema.Index(Case); actual[key] = "false";
        Assert.Throws<IndexConflictException>(() => VectorSchema.CheckCatalog([actual], VectorSchema.Index(Case)));
    }
    [Fact] public void UnknownVectorOptionsFail()
    {
        var actual = VectorSchema.Index(Case); actual["cosmosSearchOptions"]["unknown"] = 1;
        Assert.Throws<IndexConflictException>(() => VectorSchema.CheckCatalog([actual], VectorSchema.Index(Case)));
    }

    [Fact] public void ExplicitFalseOptionsAndUnrelatedOrdinaryIndexArePreserved()
    {
        var expected = VectorSchema.Index(Case); var actual = expected.DeepClone().AsBsonDocument;
        actual["unique"] = false; actual["sparse"] = false; actual["hidden"] = false;
        var ordinary = new BsonDocument { ["name"] = "user_unique", ["key"] = new BsonDocument("HotelName", 1), ["unique"] = true };
        Assert.True(VectorSchema.CheckCatalog([actual, ordinary], expected));
    }
    [Fact] public async Task CreatedIndexMustActuallyAppearInCatalog() => await Assert.ThrowsAsync<VerificationException>(() =>
        new VectorSearchService(new FakeStore { HideCreatedIndex = true }).RunAsync(Case, SyntheticFixture(), 5, true, default));
    [Fact] public async Task SearchFailuresPropagate() => await Assert.ThrowsAsync<InvalidOperationException>(() =>
        new VectorSearchService(new FakeStore { SearchFailure = new InvalidOperationException("unsupported") }).RunAsync(Case, SyntheticFixture(), 5, true, default));
    [Fact] public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VectorSearchService(new FakeStore()).RunAsync(Case, SyntheticFixture(), 5, true, cts.Token));
    }
    [Theory][InlineData("missing")][InlineData("string")][InlineData("zero")][InlineData("duplicate")]
    public async Task MalformedLateRecordPreventsEveryWrite(string mode)
    {
        var fixture = SyntheticFixture(); var last = fixture.Documents[^1];
        if (mode == "missing") last.Remove(Fixture.VectorField);
        if (mode == "string") last[Fixture.VectorField] = "bad";
        if (mode == "zero") last[Fixture.VectorField] = new BsonArray(new double[1536]);
        if (mode == "duplicate") last["_id"] = fixture.Documents[0]["_id"];
        var fake = new FakeStore();
        await Assert.ThrowsAsync<ConfigurationException>(() => new VectorSearchService(fake).RunAsync(Case, fixture, 5, true, default));
        Assert.Empty(fake.Documents); Assert.Equal(0, fake.CatalogCalls);
    }
    [Theory][InlineData(0)][InlineData(1535)][InlineData(1537)]
    public void WrongDimensionRejected(int size) => Assert.Throws<ConfigurationException>(() => Fixture.ValidateVector(new double[size]));
    [Theory][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.MaxValue)]
    public void InvalidNormRejected(double n) { var q = SyntheticFixture().Query; q[0] = n; Assert.Throws<ConfigurationException>(() => Fixture.ValidateVector(q)); }
    [Fact] public void ZeroRejected() => Assert.Throws<ConfigurationException>(() => Fixture.ValidateVector(new double[1536]));

    internal sealed class FakeStore : IVectorStore
    {
        public Dictionary<string, BsonDocument> Documents { get; } = [];
        public List<BsonDocument> Indexes { get; } = [];
        public Exception? InsertFailure { get; init; }
        public Exception? SearchFailure { get; init; }
        public bool RaceInsert { get; init; }
        public bool HideCreatedIndex { get; init; }
        public Func<List<BsonDocument>, List<BsonDocument>>? MutateResults { get; init; }
        public int CreateCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public BsonDocument[]? LastPipeline { get; private set; }
        public Task<bool> ContainsIdAsync(string id, CancellationToken ct) => Task.FromResult(Documents.ContainsKey(id));
        public Task InsertAsync(BsonDocument document, CancellationToken ct)
        {
            if (InsertFailure != null) throw InsertFailure;
            Documents.Add(document["_id"].AsString, document.DeepClone().AsBsonDocument);
            if (RaceInsert) throw new DuplicateDocumentException();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<BsonDocument>> ListIndexesAsync(CancellationToken ct)
        { CatalogCalls++; return Task.FromResult<IReadOnlyList<BsonDocument>>(Indexes); }
        public Task CreateIndexAsync(BsonDocument index, CancellationToken ct)
        { CreateCalls++; if (!HideCreatedIndex) Indexes.Add(index); return Task.CompletedTask; }
        public Task<IReadOnlyList<BsonDocument>> SearchAsync(BsonDocument[] pipeline, CancellationToken ct)
        {
            if (SearchFailure != null) throw SearchFailure;
            LastPipeline = pipeline;
            var q = pipeline[0]["$search"]["cosmosSearch"]["vector"].AsBsonArray.Select(v => v.ToDouble()).ToArray();
            var k = pipeline[0]["$search"]["cosmosSearch"]["k"].AsInt32;
            var metric = Enum.Parse<SimilarityMetric>(Indexes[0]["cosmosSearchOptions"]["similarity"].AsString);
            var fixture = new Fixture(Documents.Values.Where(d => d.Contains(Fixture.VectorField)).ToArray(), q);
            var rows = FixtureOracle.Rank(fixture, metric).Take(k).Select(r => new BsonDocument { ["_id"] = r.Id, ["score"] = r.Score }).ToList();
            return Task.FromResult<IReadOnlyList<BsonDocument>>(MutateResults?.Invoke(rows) ?? rows);
        }
    }
}
