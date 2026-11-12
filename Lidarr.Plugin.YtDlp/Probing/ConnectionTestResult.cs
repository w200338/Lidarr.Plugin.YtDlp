namespace Lidarr.Plugin.YtDlp.Probing;

/// <summary>
/// One connection-test failure, tagged with the settings property it refers to
/// (used as the ValidationFailure.PropertyName so the host UI groups the error
/// under the right field).
/// </summary>
public sealed record ConnectionTestError(string Property, string Message);

/// <summary>
/// Outcome of <see cref="YtDlpConnectionTester.TestAsync"/> — plain data, no host
/// types, so both the indexer and the download client can map it to their own
/// host validation shapes.
/// </summary>
public sealed record ConnectionTestResult(IReadOnlyList<ConnectionTestError> Errors, IReadOnlyList<string> Warnings)
{
    public bool Success => Errors.Count == 0;
}
