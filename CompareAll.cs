using System.Text.Json;

namespace SelectAlgorithm;

/// <summary>Independent case deadlines; a failed first/middle case never hides the remaining rows.</summary>
public static class CompareAll
{
    public static async Task<int> RunCasesAsync(Func<ComparisonCase, CancellationToken, Task<object>> execute,
        TimeSpan caseTimeout, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var failures = 0;
        foreach (var item in ComparisonCase.All)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(caseTimeout);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                var result = await execute(item, deadline.Token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { Algorithm = item.Algorithm.Suffix(),
                    Metric = item.Metric.ToString(), Status = "PASS", Result = result }));
            }
            catch (Exception ex) when (Program.IsExpectedFailure(ex))
            {
                failures++;
                var message = Program.SafeError(ex);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { Algorithm = item.Algorithm.Suffix(),
                    Metric = item.Metric.ToString(), Status = "FAIL", Error = message }));
                await error.WriteLineAsync($"ERROR: {item.Algorithm.Suffix()}/{item.Metric}: {message}");
            }
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new { Attempted = 9, Passed = 9 - failures, Failed = failures }));
        return failures == 0 ? 0 : 1;
    }
}
