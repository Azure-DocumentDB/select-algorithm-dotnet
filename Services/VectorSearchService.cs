using SelectAlgorithm.Models;
using MongoDB.Bson;

namespace SelectAlgorithm.Services;

public sealed class VectorSearchService(IVectorStore store)
{
    public async Task<SearchReport> RunAsync(ComparisonCase item, Fixture fixture, int topK, bool allowWrites, CancellationToken ct,
        int readinessAttempts = 12, TimeSpan? readinessDelay = null)
    {
        if (!allowWrites) throw new ConfigurationException("Database writes require explicit opt-in.");
        if (topK is < 1 or > 50 || readinessAttempts is < 1 or > 12) throw new ConfigurationException("Invalid query bounds.");
        Fixture.ValidateVector(fixture.Query);
        // Validate the entire input before the first store call: malformed later records never cause partial loading.
        if (fixture.Documents.Count != 50 || fixture.Documents.Any(d => !d.TryGetValue("_id", out var id) || !id.IsString || string.IsNullOrWhiteSpace(id.AsString)) ||
            fixture.Documents.Select(d => d["_id"].AsString).Distinct().Count() != 50) throw new ConfigurationException("Malformed fixture IDs.");
        foreach (var doc in fixture.Documents)
        {
            if (!doc.TryGetValue(Fixture.VectorField, out var v) || !v.IsBsonArray || v.AsBsonArray.Any(n => !n.IsNumeric))
                throw new ConfigurationException("Malformed fixture vector.");
            Fixture.ValidateVector(v.AsBsonArray.Select(n => n.ToDouble()).ToArray());
        }
        var index = VectorSchema.Index(item);
        VectorSchema.CheckCatalog(await store.ListIndexesAsync(ct), index);
        var inserted = 0;
        foreach (var document in fixture.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var id = document["_id"].AsString;
            if (await store.ContainsIdAsync(id, ct)) continue;
            try { await store.InsertAsync(document, ct); inserted++; }
            catch (DuplicateDocumentException) { if (!await store.ContainsIdAsync(id, ct)) throw; }
        }
        if (!VectorSchema.CheckCatalog(await store.ListIndexesAsync(ct), index)) await store.CreateIndexAsync(index, ct);
        if (!VectorSchema.CheckCatalog(await store.ListIndexesAsync(ct), index)) throw new VerificationException();

        IReadOnlyList<BsonDocument> results = [];
        var verified = false;
        for (var attempt = 0; attempt < readinessAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            results = await store.SearchAsync(VectorSchema.Pipeline(fixture.Query, topK), ct);
            verified = FixtureOracle.Verify(results, fixture, item.Metric, topK);
            if (verified) break;
            if (attempt + 1 < readinessAttempts) await Task.Delay(readinessDelay ?? TimeSpan.FromSeconds(2), ct);
        }
        if (!verified) throw new VerificationException();
        return new SearchReport(inserted, fixture.Documents.Count - inserted, results.Count, topK,
            results.Select(r => new RankedResult(r["_id"].AsString, r["score"].ToDouble())).ToArray(),
            fixture.QuerySha256, true, "pinned fixture metric oracle, tolerance 0.001; no model or semantic-quality claim");
    }
}

public sealed record SearchReport(int Inserted, int Preserved, int Results, int TopK,
    IReadOnlyList<RankedResult> Matches, string QuerySha256, bool IndexVerified, string Validation);

public static class VectorSchema
{
    public static BsonDocument Index(ComparisonCase item)
    {
        if (!Enum.IsDefined(item.Metric)) throw new ConfigurationException("Unknown metric.");
        var options = new BsonDocument { ["kind"] = "vector-" + item.Algorithm.Suffix(), ["similarity"] = item.Metric.ToString(), ["dimensions"] = Fixture.Dimensions };
        switch (item.Algorithm)
        {
            case VectorIndexType.IVF: options["numLists"] = 1; break;
            case VectorIndexType.HNSW: options["m"] = 16; options["efConstruction"] = 64; break;
            case VectorIndexType.DiskANN: break;
            default: throw new ConfigurationException("Unknown algorithm.");
        }
        return new BsonDocument { ["name"] = "dotnet_vector_" + item.Algorithm.Suffix() + "_" + item.Metric.ToString().ToLowerInvariant(),
            ["key"] = new BsonDocument(Fixture.VectorField, "cosmosSearch"), ["cosmosSearchOptions"] = options };
    }

    public static bool CheckCatalog(IReadOnlyList<BsonDocument> catalog, BsonDocument expected)
    {
        var found = false;
        foreach (var actual in catalog)
        {
            // Reject malformed entries rather than silently treating unreadable catalogs as empty.
            if (!actual.TryGetValue("name", out var name) || !name.IsString || !actual.TryGetValue("key", out var key) || !key.IsBsonDocument)
                throw new IndexConflictException();
            var sameName = name == expected["name"];
            var vector = key.AsBsonDocument.Elements.Any(e => e.Value == "cosmosSearch");
            // Hazardous semantics affect inserts/retention even on unrelated fields.
            var genuineIdIndex = name == "_id_" && key == new BsonDocument("_id", 1);
            if ((!IsFalseOrAbsent(actual, "unique") && !(genuineIdIndex && actual["unique"].IsBoolean && actual["unique"].AsBoolean)) ||
                !IsFalseOrAbsent(actual, "sparse") || !IsFalseOrAbsent(actual, "hidden") ||
                actual.Contains("partialFilterExpression") || actual.Contains("expireAfterSeconds") || actual.Contains("collation"))
                throw new IndexConflictException();
            if (!sameName && !vector) continue; // only harmless unrelated indexes survive the complete safety check
            if (found || !sameName || !IsFalseOrAbsent(actual, "unique") || !IsFalseOrAbsent(actual, "sparse") ||
                !IsFalseOrAbsent(actual, "hidden") || actual.Contains("partialFilterExpression") || actual.Contains("expireAfterSeconds") ||
                actual.Contains("collation") || key != expected["key"] ||
                !actual.TryGetValue("cosmosSearchOptions", out var options) || !options.IsBsonDocument ||
                !expected["cosmosSearchOptions"].AsBsonDocument.All(e => options.AsBsonDocument.TryGetValue(e.Name, out var value) && value == e.Value))
                throw new IndexConflictException();
            // Server-supplied defaults are allowed only when known and harmless. Never ignore arbitrary extra semantics.
            var allowedFields = new HashSet<string> { "name", "key", "cosmosSearchOptions", "v", "ns", "unique", "sparse", "hidden", "background" };
            if (actual.Names.Any(n => !allowedFields.Contains(n))) throw new IndexConflictException();
            foreach (var option in options.AsBsonDocument)
                if (!expected["cosmosSearchOptions"].AsBsonDocument.Contains(option.Name) &&
                    !(itemIsDiskAnn(expected) && option.Name is "maxDegree" or "lBuild" && option.Value.IsNumeric && option.Value.ToDouble() > 0))
                    throw new IndexConflictException();
            found = true;
        }
        return found;
    }
    private static bool itemIsDiskAnn(BsonDocument expected) => expected["cosmosSearchOptions"]["kind"] == "vector-diskann";
    private static bool IsFalseOrAbsent(BsonDocument doc, string key) => !doc.TryGetValue(key, out var value) || value.IsBoolean && !value.AsBoolean;

    public static BsonDocument[] Pipeline(double[] vector, int topK)
    {
        Fixture.ValidateVector(vector);
        if (topK is < 1 or > 50) throw new ConfigurationException("Invalid TopK.");
        return [new BsonDocument("$search", new BsonDocument("cosmosSearch", new BsonDocument
                { ["vector"] = new BsonArray(vector), ["path"] = Fixture.VectorField, ["k"] = topK })),
            new BsonDocument("$project", new BsonDocument { ["_id"] = 1, ["score"] = new BsonDocument("$meta", "searchScore") })];
    }
}
