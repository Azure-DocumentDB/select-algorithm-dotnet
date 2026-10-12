using System.Text.Json;
using Azure.Identity;
using MongoDB.Driver;
using SelectAlgorithm.Models;
using SelectAlgorithm.Services;
using SelectAlgorithm.Utilities;

namespace SelectAlgorithm;

public enum VectorIndexType { IVF, HNSW, DiskANN }
public enum SimilarityMetric { COS, L2, IP }
public sealed record ComparisonCase(VectorIndexType Algorithm, SimilarityMetric Metric)
{
    public static IReadOnlyList<ComparisonCase> All { get; } = Enum.GetValues<VectorIndexType>()
        .SelectMany(a => Enum.GetValues<SimilarityMetric>().Select(m => new ComparisonCase(a, m))).ToArray();
}
public static class AlgorithmNames
{
    public static string Suffix(this VectorIndexType type) => type switch
    {
        VectorIndexType.IVF => "ivf", VectorIndexType.HNSW => "hnsw", VectorIndexType.DiskANN => "diskann",
        _ => throw new ConfigurationException("Unknown algorithm.")
    };
}

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?>? environment = null,
        Func<ComparisonCase, CancellationToken, Task<object>>? execute = null)
    {
        try
        {
            if (args is ["--help"] or ["help"])
            {
                await output.WriteLineAsync("Usage: dotnet run -- [compare-all|validate-fixture]\nNo args runs all nine cases. No stdin or model calls. See README for required guards and migration.");
                return 0;
            }
            if (!(args.Length == 0 || args is ["compare-all"] or ["validate-fixture"]))
                throw new ConfigurationException("Unknown command. Use --help.");
            var get = environment ?? Environment.GetEnvironmentVariable;
            AppConfiguration.RejectRemovedKeys(get);
            var k = AppConfiguration.ReadTopK(get);
            if (execute != null) return await CompareAll.RunCasesAsync(execute, TimeSpan.FromSeconds(30), output, error);
            using var fixtureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (args is ["validate-fixture"])
            {
                var data = await Fixture.LoadAsync(AppConfiguration.DataPath(get), AppConfiguration.QueryPath(get), fixtureTimeout.Token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { Records = data.Documents.Count, Dimensions = data.Query.Length,
                    TopK = k, QuerySha256 = data.QuerySha256, Oracle = Enum.GetValues<SimilarityMetric>().Select(m => new
                    { Metric = m.ToString(), FirstId = FixtureOracle.Rank(data, m)[0].Id }), Validation = "local fixture/query validation only; no network or model call" }));
                return 0;
            }
            var config = AppConfiguration.Read(get);
            config.RequireWrites();
            ClientFactory.ValidateMongoDestination(config.MongoUri, config.AllowedHost);
            var fixture = await Fixture.LoadAsync(config.DataFile, config.QueryFile, fixtureTimeout.Token);
            using var client = new MongoClient(ClientFactory.Settings(config));
            return await CompareAll.RunCasesAsync(async (item, ct) =>
            {
                var store = new MongoDbService(client.GetDatabase(config.Database), config.Collection(item));
                return await new VectorSearchService(store).RunAsync(item, fixture, config.TopK, config.AllowWrites, ct);
            }, TimeSpan.FromSeconds(config.TimeoutSeconds), output, error);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            await error.WriteLineAsync("ERROR: " + SafeError(ex));
            return 1;
        }
    }

    // Only operational/input failures are caught, never blanket Exception. Messages never include inputs or SDK text.
    public static bool IsExpectedFailure(Exception ex) => ex is ConfigurationException or IndexConflictException or
        DuplicateDocumentException or VerificationException or MongoException or AuthenticationFailedException or
        Azure.RequestFailedException or OperationCanceledException or TimeoutException or IOException or
        UnauthorizedAccessException or JsonException or FormatException or ArgumentException or InvalidOperationException;

    public static string SafeError(Exception ex) => ex switch
    {
        ConfigurationException => "configuration rejected; check README guards, removed keys and input files",
        IndexConflictException => "index conflict; existing indexes/documents preserved; use a new disposable prefix",
        VerificationException => "catalog, result shape, score or fixture oracle verification failed",
        OperationCanceledException or TimeoutException => "operation timed out or was canceled",
        _ => "operation failed; check credentials, permissions, input files, network and service support"
    };
}
