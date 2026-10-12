using System.Security.Cryptography;
using System.Text.Json;
using SelectAlgorithm.Models;
using MongoDB.Bson;

namespace SelectAlgorithm.Services;

public sealed record Fixture(IReadOnlyList<BsonDocument> Documents, double[] Query)
{
    public const int Dimensions = 1536;
    public const string VectorField = "DescriptionVector";
    public const string SharedHash = "60c08c2cdd439444ba8601d94a1f3487f4a9ee56cd48ba201d983c96722de6e4";
    public const string OriginalDotnetHash = "68ca2a0f2a57e095f5c41853301b834779fce30ee7a9eb0aa9b156092945443f";
    public string QuerySha256 => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Query)));

    public static async Task<Fixture> LoadAsync(string dataPath, string queryPath, CancellationToken ct)
    {
        if (new FileInfo(dataPath).Length > 10_000_000)
            throw new ConfigurationException("Fixture/query file exceeds sample size limit.");
        var bytes = await File.ReadAllBytesAsync(dataPath, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != SharedHash && hash != OriginalDotnetHash)
            throw new ConfigurationException("Fixture checksum mismatch. Use one of the pinned upstream datasets.");
        using var data = JsonDocument.Parse(bytes);
        var docs = ParseDocuments(data.RootElement);
        if (new FileInfo(queryPath).Length > 100_000) throw new ConfigurationException("Query file exceeds sample size limit.");
        var query = JsonSerializer.Deserialize<double[]>(await File.ReadAllTextAsync(queryPath, ct))
            ?? throw new ConfigurationException("Query vector is missing.");
        ValidateVector(query);
        // Any valid user-supplied vector is honored, not replaced by a hard-coded fixture vector.
        return new Fixture(docs, query);
    }

    public static IReadOnlyList<BsonDocument> ParseDocuments(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 50)
            throw new ConfigurationException("Expected exactly 50 pinned fixture documents.");
        var docs = new List<BsonDocument>();
        var ids = new HashSet<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Select(p => p.Name).Distinct().Count() != item.EnumerateObject().Count())
                throw new ConfigurationException("Fixture must contain objects without duplicate keys.");
            var doc = BsonDocument.Parse(item.GetRawText());
            if (!doc.TryGetValue("HotelId", out var hotelId) || !hotelId.IsString ||
                string.IsNullOrWhiteSpace(hotelId.AsString) || !ids.Add(hotelId.AsString))
                throw new ConfigurationException("Fixture HotelId values must be unique strings.");
            if (!doc.TryGetValue(VectorField, out var rawVector) || !rawVector.IsBsonArray || rawVector.AsBsonArray.Any(v => !v.IsNumeric))
                throw new ConfigurationException("Fixture vector is missing or nonnumeric.");
            var vector = rawVector.AsBsonArray.Select(v => v.ToDouble()).ToArray();
            ValidateVector(vector);
            doc["_id"] = "dotnet-hotel-" + hotelId.AsString;
            doc[VectorField] = new BsonArray(vector.Select(v => new BsonDouble(v)));
            docs.Add(doc);
        }
        return docs;
    }

    public static void ValidateVector(IReadOnlyList<double> vector)
    {
        var normSquared = vector.Sum(v => v * v);
        if (vector.Count != Dimensions || vector.Any(v => !double.IsFinite(v)) || !double.IsFinite(normSquared) || normSquared <= 0)
            throw new ConfigurationException("Expected a finite nonzero 1536-dimensional vector with finite norm.");
    }
}

public sealed record RankedResult(string Id, double Score);
public static class FixtureOracle
{
    // DocumentDB searchScore: COS similarity, L2 Euclidean distance, IP dot product.
    public static double Score(IReadOnlyList<double> vector, IReadOnlyList<double> query, SimilarityMetric metric)
    {
        var dot = vector.Zip(query).Sum(p => p.First * p.Second);
        return metric switch
        {
            SimilarityMetric.COS => dot / Math.Sqrt(vector.Sum(v => v * v) * query.Sum(v => v * v)),
            SimilarityMetric.L2 => Math.Sqrt(vector.Zip(query).Sum(p => (p.First - p.Second) * (p.First - p.Second))),
            SimilarityMetric.IP => dot,
            _ => throw new ConfigurationException("Unknown metric.")
        };
    }
    public static IReadOnlyList<RankedResult> Rank(Fixture fixture, SimilarityMetric metric)
    {
        var ranked = fixture.Documents.Select(d => new RankedResult(d["_id"].AsString,
            Score(d[Fixture.VectorField].AsBsonArray.Select(v => v.ToDouble()).ToArray(), fixture.Query, metric)));
        return (metric == SimilarityMetric.L2 ? ranked.OrderBy(r => r.Score) : ranked.OrderByDescending(r => r.Score)).ToArray();
    }

    public static bool Verify(IReadOnlyList<BsonDocument> results, Fixture fixture, SimilarityMetric metric, int k)
    {
        if (results.Count != k || results.Any(r => !r.TryGetValue("_id", out var id) || !id.IsString ||
            !r.TryGetValue("score", out var score) || !score.IsNumeric || !double.IsFinite(score.ToDouble())) ||
            results.Select(r => r["_id"].AsString).Distinct().Count() != k) return false;
        var oracle = Rank(fixture, metric);
        var scores = oracle.ToDictionary(r => r.Id, r => r.Score);
        const double tolerance = 0.001;
        for (var i = 0; i < k; i++)
        {
            var actual = results[i];
            if (!scores.TryGetValue(actual["_id"].AsString, out var expected) ||
                Math.Abs(actual["score"].ToDouble() - expected) > tolerance ||
                Math.Abs(expected - oracle[i].Score) > tolerance) return false;
            if (i > 0)
            {
                var delta = actual["score"].ToDouble() - results[i - 1]["score"].ToDouble();
                if (metric == SimilarityMetric.L2 ? delta < -tolerance : delta > tolerance) return false;
            }
        }
        return true;
    }
}
