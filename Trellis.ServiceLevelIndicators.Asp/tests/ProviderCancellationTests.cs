namespace Trellis.ServiceLevelIndicators.Asp.Tests;

using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

public sealed class ProviderCancellationTests
{
    private const string SqlConnectionVariable = "TRELLIS_SLI_SQL_CONNECTION_STRING";

    public static bool SqlServerConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlConnectionVariable));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpClient_cancellation_preserves_abort_first_semantics(bool abortRequest)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var context = HttpOutcomePreviewTests.CreateContext(
            abortRequest ? cancellation.Token : TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/slow");
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        Exception? providerException = null;
        var middleware = HttpOutcomePreviewTests.CreateMiddleware(async _ =>
        {
            var pending = client.GetAsync(address, cancellation.Token);
            using var serverConnection = await accepted.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            using var stream = serverConnection.GetStream();
            var request = new byte[1024];
            var received = await stream.ReadAsync(request, TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            received.Should().BePositive("the outgoing request must reach the server before cancellation");
            await cancellation.CancelAsync();
            try
            {
                using var response = await pending;
            }
            catch (Exception exception)
            {
                providerException = exception;
                throw;
            }
        }, sli);

        var exception = Assert.IsAssignableFrom<OperationCanceledException>(
            await Xunit.Record.ExceptionAsync(() => middleware.InvokeAsync(context)));

        exception.Should().BeSameAs(providerException);
        AssertMeasurements(capture, abortRequest, exception);
    }

    [Theory(Skip = "Set TRELLIS_SLI_SQL_CONNECTION_STRING or run build\\test-sql-cancellation.ps1.",
        SkipUnless = nameof(SqlServerConfigured))]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlClient_cancellation_preserves_abort_first_semantics(bool abortRequest)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(20));
        var context = HttpOutcomePreviewTests.CreateContext(
            abortRequest ? cancellation.Token : TestContext.Current.CancellationToken);
        var connectionString = Environment.GetEnvironmentVariable(SqlConnectionVariable);
        Exception? providerException = null;
        var middleware = HttpOutcomePreviewTests.CreateMiddleware(async _ =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "WAITFOR DELAY '00:00:30';";
            var pending = command.ExecuteNonQueryAsync(cancellation.Token);
            await WaitForSqlDelayAsync(connectionString, connection.ServerProcessId, TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            try
            {
                await pending;
            }
            catch (Exception exception)
            {
                providerException = exception;
                throw;
            }
        }, sli);

        var exception = Assert.IsAssignableFrom<Exception>(
            await Xunit.Record.ExceptionAsync(() => middleware.InvokeAsync(context)));

        exception.Should().BeSameAs(providerException);
        (exception is SqlException or OperationCanceledException).Should().BeTrue(
            "a running SqlClient command should fail through its provider cancellation path");
        AssertMeasurements(capture, abortRequest, exception);
    }

    private static async Task WaitForSqlDelayAsync(string? connectionString, int sessionId, CancellationToken cancellationToken)
    {
        await using var monitor = new SqlConnection(connectionString);
        await monitor.OpenAsync(cancellationToken);
        await using var probe = monitor.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @sessionId AND wait_type = N'WAITFOR';";
        probe.Parameters.Add("@sessionId", SqlDbType.Int).Value = sessionId;
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await probe.ExecuteScalarAsync(cancellationToken) is int active && active > 0)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("The SqlClient command did not reach the server's WAITFOR state.");
    }

    private static void AssertMeasurements(MetricCapture capture, bool aborted, Exception exception)
    {
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        var count = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be(aborted ? "Ignored" : "Failure");
        count.Tags["Outcome"].Should().Be(aborted ? "Ignored" : "Failure");
        if (aborted)
        {
            duration.Tags["sli.request.aborted"].Should().Be(true);
            duration.Tags["sli.request.abort_exception"].Should().Be(
                exception is OperationCanceledException ? "cancellation" : "other");
            duration.Tags.Should().NotContainKey("http.response.status.code");
            count.Tags["sli.request.aborted"].Should().Be(true);
        }
        else
        {
            duration.Tags["http.response.status.code"].Should().Be(500);
            duration.Tags.Should().NotContainKeys("sli.request.aborted", "sli.request.abort_exception");
            count.Tags.Should().NotContainKey("sli.request.aborted");
        }

        count.Tags.Should().NotContainKeys("http.request.method", "http.response.status.code", "sli.request.abort_exception");
    }
}
