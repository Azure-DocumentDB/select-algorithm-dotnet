using System.Text.Json;
using SelectAlgorithm.Models;
using SelectAlgorithm.Services;
using MongoDB.Bson;

namespace SelectAlgorithm.Tests;

public class ComparisonTests
{
    [Theory][InlineData(0)][InlineData(4)][InlineData(8)]
    public async Task FailureAtAnyPositionStillAttemptsNineAndExitsNonzero(int bad)
    {
        var calls = new List<ComparisonCase>(); var output = new StringWriter(); var errors = new StringWriter();
        var code = await Program.RunAsync(["compare-all"], output, errors, _ => null, (item, _) =>
        {
            calls.Add(item);
            if (calls.Count - 1 == bad) throw new InvalidOperationException("mongodb://user:SENTINEL@private-host token=SENTINEL");
            return Task.FromResult<object>(new { Results = 2 });
        });
        Assert.Equal(1, code); Assert.Equal(ComparisonCase.All, calls); Assert.DoesNotContain("SENTINEL", output + errors.ToString());
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Assert.Equal(10, lines.Length); Assert.Equal(1, lines.Take(9).Count(r => r.GetProperty("Status").GetString() == "FAIL"));
        Assert.Equal(9, lines[^1].GetProperty("Attempted").GetInt32()); Assert.Equal(1, lines[^1].GetProperty("Failed").GetInt32());
    }
    [Fact] public async Task AllSuccessIsZero() => Assert.Equal(0, await Program.RunAsync([], new StringWriter(), new StringWriter(), _ => null,
        (_, _) => Task.FromResult<object>(new { Verified = true })));
    [Fact] public async Task UnknownCommandAndMissingConfigurationFailWithoutStdin()
    {
        Assert.Equal(1, await Program.RunAsync(["wrong"], new StringWriter(), new StringWriter(), _ => null));
        Assert.Equal(1, await Program.RunAsync([], new StringWriter(), new StringWriter(), _ => null));
    }
    [Theory][InlineData("VectorSearch__Query")][InlineData("VectorSearch__Similarity")][InlineData("QUERY_TEXT")][InlineData("TOP_K")]
    [InlineData("DocumentDB__DatabaseName")][InlineData("AzureOpenAI__Endpoint")]
    public async Task RemovedKeysFailRatherThanBeingIgnored(string key)
    {
        var executed = false;
        Assert.Equal(1, await Program.RunAsync(["compare-all"], new StringWriter(), new StringWriter(), k => k == key ? "" : null,
            (_, _) => { executed = true; return Task.FromResult<object>(0); }));
        Assert.False(executed);
    }
    [Theory][InlineData("0")][InlineData("51")][InlineData("-1")][InlineData("garbage")]
    public void InvalidTopKRejected(string value) => Assert.Throws<ConfigurationException>(() => AppConfiguration.ReadTopK(k => k == "VectorSearch__TopK" ? value : null));

    [Theory][InlineData(1)][InlineData(2)][InlineData(7)][InlineData(50)]
    public async Task ActualQueryAndConfiguredTopKReachPipelineAndChangeResults(int k)
    {
        var topK = AppConfiguration.ReadTopK(key => key == "VectorSearch__TopK" ? k.ToString() : null);
        var fixture = WorkflowTests.SyntheticFixture();
        var changed = fixture with { Query = fixture.Documents[^1][Fixture.VectorField].AsBsonArray.Select(v => v.ToDouble()).ToArray() };
        var store = new WorkflowTests.FakeStore(); var service = new VectorSearchService(store);
        var first = await service.RunAsync(WorkflowTests.Case, fixture, topK, true, default);
        var second = await service.RunAsync(WorkflowTests.Case, changed, topK, true, default);
        Assert.Equal(k, second.Results); Assert.Equal(k, second.TopK); Assert.NotEqual(first.Matches[0].Id, second.Matches[0].Id);
        Assert.NotEqual(first.QuerySha256, second.QuerySha256);
        Assert.Equal(changed.Query, store.LastPipeline![0]["$search"]["cosmosSearch"]["vector"].AsBsonArray.Select(v => v.ToDouble()));
    }
    [Fact] public async Task EachCaseHasIndependentCancellationBudget()
    {
        var calls = 0; var output = new StringWriter();
        var code = await CompareAll.RunCasesAsync(async (_, ct) =>
        {
            calls++;
            if (calls == 1) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Assert.False(ct.IsCancellationRequested);
            return new { Passed = true };
        }, TimeSpan.FromMilliseconds(30), output, new StringWriter());
        Assert.Equal(1, code); Assert.Equal(9, calls);
        Assert.Contains("\"Passed\":8", output.ToString());
    }

    [Theory][InlineData("empty")][InlineData("duplicate")][InlineData("missing")][InlineData("string")]
    [InlineData("nan")][InlineData("score")][InlineData("order")][InlineData("id")]
    public async Task MalformedOrWrongResultsNeverPass(string failure)
    {
        var store = new WorkflowTests.FakeStore { MutateResults = rows =>
        {
            switch (failure)
            {
                case "empty": return [];
                case "duplicate": rows[1] = rows[0]; break;
                case "missing": rows[0].Remove("score"); break;
                case "string": rows[0]["score"] = "1"; break;
                case "nan": rows[0]["score"] = double.NaN; break;
                case "score": rows[0]["score"] = 99.0; break;
                case "order": rows.Reverse(); break;
                case "id": rows[0]["_id"] = "unknown"; break;
            }
            return rows;
        } };
        await Assert.ThrowsAsync<VerificationException>(() => new VectorSearchService(store).RunAsync(WorkflowTests.Case,
            WorkflowTests.SyntheticFixture(), 5, true, default, readinessAttempts: 1));
    }
    [Fact] public void OracleIsMetricCorrectForUnnormalizedVectors()
    {
        double[] q = [1, 0], near = [1, 0.1], large = [5, 2];
        Assert.Equal(1, FixtureOracle.Score(q, q, SimilarityMetric.COS), 8);
        Assert.Equal(0.1, FixtureOracle.Score(near, q, SimilarityMetric.L2), 8);
        Assert.Equal(Math.Sqrt(20), FixtureOracle.Score(large, q, SimilarityMetric.L2), 8);
        Assert.Equal(5, FixtureOracle.Score(large, q, SimilarityMetric.IP), 8);
        Assert.True(FixtureOracle.Score(near, q, SimilarityMetric.COS) > FixtureOracle.Score(large, q, SimilarityMetric.COS));
    }
    [Fact] public void EveryCombinationHasIsolatedAuthAndMetricNamespace()
    {
        var names = new List<string>();
        foreach (var auth in new[] { "scram", "oidc" })
            names.AddRange(ComparisonCase.All.Select(c => (SafetyTests.Good with { AuthMode = auth }).Collection(c)));
        Assert.Equal(18, names.Distinct().Count());
    }
}
