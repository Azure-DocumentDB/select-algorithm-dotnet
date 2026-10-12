using SelectAlgorithm.Models;
using SelectAlgorithm.Services;
using SelectAlgorithm.Utilities;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit.Sdk;

namespace SelectAlgorithm.Tests;

/// <summary>Explicit paid-resource gate. Selecting Category=Live without its guards FAILS, never skips.</summary>
public class LiveIntegrationTests
{
    public static AppConfiguration Configuration(string mode, Func<string, string?> get)
    {
        if (get("RUN_LIVE_INTEGRATION") != "true" || get("ALLOW_DATABASE_WRITES") != "true" ||
            get("LIVE_AUTH_MODES") != "scram,oidc")
            throw new ConfigurationException("Live gate requires RUN_LIVE_INTEGRATION=true, ALLOW_DATABASE_WRITES=true and LIVE_AUTH_MODES=scram,oidc.");
        foreach (var key in new[] { "MONGO_URI", "MONGO_ALLOWED_HOST", "MONGO_DATABASE", "MONGO_COLLECTION_PREFIX", "MONGO_USERNAME", "MONGO_PASSWORD", "VECTOR_DATA_FILE", "QUERY_VECTOR_FILE" })
            if (string.IsNullOrWhiteSpace(get(key))) throw new ConfigurationException("Missing required live environment. See README.");
        return AppConfiguration.Read(key => key switch
        {
            "MONGO_AUTH_MODE" => mode,
            "MONGO_PASSWORD" or "MONGO_USERNAME" when mode == "oidc" => "",
            _ => get(key)
        });
    }

    public static IEnumerable<object[]> Cases => new[] { "scram", "oidc" }.SelectMany(mode =>
        ComparisonCase.All.Select(item => new object[] { mode, item.Algorithm, item.Metric }));

    [Theory, MemberData(nameof(Cases)), Trait("Category", "Live")]
    public async Task CatalogMetricOracleAndIdempotence(string mode, VectorIndexType algorithm, SimilarityMetric metric)
    {
        var stage = "configuration";
        try
        {
            var config = Configuration(mode, Environment.GetEnvironmentVariable);
            config.RequireWrites();
            var item = new ComparisonCase(algorithm, metric);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var ct = timeout.Token;
            stage = "fixture-validation";
            var fixture = await Fixture.LoadAsync(config.DataFile, config.QueryFile, ct);
            stage = "client-construction";
            using var client = new MongoClient(ClientFactory.Settings(config));
            var database = client.GetDatabase(config.Database);
            var store = new MongoDbService(database, config.Collection(item));
            var collection = database.GetCollection<BsonDocument>(config.Collection(item));
            // Seed only absent test IDs. Never overwrite a document, even a prior test's canary.
            var edited = fixture.Documents[0].DeepClone().AsBsonDocument;
            edited["HotelName"] = "live gate preservation canary";
            stage = "canary-insertion";
            await InsertIfAbsent(store, edited, ct);
            await InsertIfAbsent(store, new BsonDocument { ["_id"] = "dotnet-unrelated-canary", ["note"] = "preserve" }, ct);
            var beforeEdited = await collection.Find(new BsonDocument("_id", fixture.Documents[0]["_id"].AsString)).SingleAsync(ct);
            var beforeUnrelated = await collection.Find(new BsonDocument("_id", "dotnet-unrelated-canary")).SingleAsync(ct);
            var service = new VectorSearchService(store);
            stage = "first-index-and-query-pass";
            var first = await service.RunAsync(item, fixture, config.TopK, true, ct);
            var count = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            stage = "repeat-index-and-query-pass";
            var second = await service.RunAsync(item, fixture, config.TopK, true, ct);
            stage = "preservation-verification";
            Assert.Equal(config.TopK, first.Results); Assert.Equal(FixtureOracle.Rank(fixture, metric)[0].Id, first.Matches[0].Id);
            Assert.Equal(0, second.Inserted); Assert.True(second.IndexVerified);
            Assert.Equal(count, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct));
            Assert.True(count >= 51);
            Assert.Equal(beforeEdited, await collection.Find(new BsonDocument("_id", fixture.Documents[0]["_id"].AsString)).SingleAsync(ct));
            Assert.Equal(beforeUnrelated, await collection.Find(new BsonDocument("_id", "dotnet-unrelated-canary")).SingleAsync(ct));
            Assert.True(VectorSchema.CheckCatalog(await store.ListIndexesAsync(ct), VectorSchema.Index(item)));
        }
        catch (Exception ex) when (Program.IsExpectedFailure(ex) || ex is XunitException)
        {
            // xUnit normally prints raw exception messages, which may contain service metadata/credentials.
            throw new XunitException($"Live {mode}/{algorithm.Suffix()}/{metric} gate failed at {stage}. Check guards, namespace, credentials, network, service support and fixture. No raw exception logged.");
        }
    }

    private static async Task InsertIfAbsent(IVectorStore store, BsonDocument document, CancellationToken ct)
    {
        var id = document["_id"].AsString;
        if (await store.ContainsIdAsync(id, ct)) return;
        try { await store.InsertAsync(document, ct); }
        catch (DuplicateDocumentException) { if (!await store.ContainsIdAsync(id, ct)) throw; }
    }
}

public class LiveGuardTests
{
    [Fact] public void MissingGuardFailsInsteadOfSkipping() => Assert.Throws<ConfigurationException>(() => LiveIntegrationTests.Configuration("scram", _ => null));
    [Fact] public void RequestedWithoutEnvironmentFails() => Assert.Throws<ConfigurationException>(() => LiveIntegrationTests.Configuration("scram", key => key switch
    { "RUN_LIVE_INTEGRATION" or "ALLOW_DATABASE_WRITES" => "true", "LIVE_AUTH_MODES" => "scram,oidc", _ => null }));
    [Fact] public void PartialAuthSelectionDoesNotClaimCompleteGate() => Assert.Throws<ConfigurationException>(() => LiveIntegrationTests.Configuration("scram", key => key switch
    { "RUN_LIVE_INTEGRATION" or "ALLOW_DATABASE_WRITES" => "true", "LIVE_AUTH_MODES" => "scram", _ => "placeholder" }));
}
