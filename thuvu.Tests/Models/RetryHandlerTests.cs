using System.Net;
using thuvu.Models;

namespace thuvu.Tests.Models;

public class RetryHandlerTests
{
    [Fact]
    public void ShouldRetry_HttpRequestException_ReturnsTrue()
    {
        Assert.True(RetryHandler.ShouldRetry(new HttpRequestException("Connection refused")));
    }

    [Fact]
    public void ShouldRetry_TimeoutException_ReturnsTrue()
    {
        Assert.True(RetryHandler.ShouldRetry(new TimeoutException("The operation timed out")));
    }

    [Fact]
    public void ShouldRetry_TaskCanceledExceptionWithTimeout_ReturnsTrue()
    {
        var ex = new TaskCanceledException("timed out", new TimeoutException());
        Assert.True(RetryHandler.ShouldRetry(ex));
    }

    [Fact]
    public void ShouldRetry_SocketException_ReturnsTrue()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            var ex = new System.Net.Sockets.SocketException();
            Assert.True(RetryHandler.ShouldRetry(ex));
        }
    }

    [Fact]
    public void ShouldRetry_InvalidOperationException_ReturnsFalse()
    {
        Assert.False(RetryHandler.ShouldRetry(new InvalidOperationException("Invalid state")));
    }

    [Fact]
    public void ShouldRetry_ArgumentException_ReturnsFalse()
    {
        Assert.False(RetryHandler.ShouldRetry(new ArgumentException("Invalid argument")));
    }

    [Fact]
    public void ShouldRetry_NullReferenceException_ReturnsFalse()
    {
        Assert.False(RetryHandler.ShouldRetry(new NullReferenceException("Object reference")));
    }

    [Fact]
    public void IsRetryableStatusCode_429TooManyRequests_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsRetryableStatusCode(HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public void IsRetryableStatusCode_500InternalServerError_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsRetryableStatusCode(HttpStatusCode.InternalServerError));
    }

    [Fact]
    public void IsRetryableStatusCode_502BadGateway_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsRetryableStatusCode(HttpStatusCode.BadGateway));
    }

    [Fact]
    public void IsRetryableStatusCode_503ServiceUnavailable_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsRetryableStatusCode(HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public void IsRetryableStatusCode_504GatewayTimeout_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsRetryableStatusCode(HttpStatusCode.GatewayTimeout));
    }

    [Fact]
    public void IsRetryableStatusCode_408RequestTimeout_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsRetryableStatusCode(HttpStatusCode.RequestTimeout));
    }

    [Fact]
    public void IsRetryableStatusCode_200Ok_ReturnsFalse()
    {
        Assert.False(RetryHandler.IsRetryableStatusCode(HttpStatusCode.OK));
    }

    [Fact]
    public void IsRetryableStatusCode_404NotFound_ReturnsFalse()
    {
        Assert.False(RetryHandler.IsRetryableStatusCode(HttpStatusCode.NotFound));
    }

    [Fact]
    public void IsNonRetryableStatusCode_400BadRequest_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsNonRetryableStatusCode(HttpStatusCode.BadRequest));
    }

    [Fact]
    public void IsNonRetryableStatusCode_401Unauthorized_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsNonRetryableStatusCode(HttpStatusCode.Unauthorized));
    }

    [Fact]
    public void IsNonRetryableStatusCode_403Forbidden_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsNonRetryableStatusCode(HttpStatusCode.Forbidden));
    }

    [Fact]
    public void IsNonRetryableStatusCode_404NotFound_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsNonRetryableStatusCode(HttpStatusCode.NotFound));
    }

    [Fact]
    public void IsNonRetryableStatusCode_405MethodNotAllowed_ReturnsTrue()
    {
        Assert.True(RetryHandler.IsNonRetryableStatusCode(HttpStatusCode.MethodNotAllowed));
    }

    [Fact]
    public void IsNonRetryableStatusCode_500ServerError_ReturnsFalse()
    {
        Assert.False(RetryHandler.IsNonRetryableStatusCode(HttpStatusCode.InternalServerError));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SuccessOnFirstAttempt_ReturnsSuccess()
    {
        var result = await RetryHandler.ExecuteWithRetryAsync(
            _ => Task.FromResult(42),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(42, result.Result);
        Assert.Equal(1, result.AttemptsUsed);
        Assert.Null(result.LastException);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SuccessAfterRetries_ReturnsSuccess()
    {
        int callCount = 0;
        var config = new RetryConfig { MaxRetries = 3, RetryBaseDelayMs = 10, UseJitter = false };

        var result = await RetryHandler.ExecuteWithRetryAsync(
            _ =>
            {
                callCount++;
                if (callCount < 3)
                    throw new HttpRequestException("Transient error");
                return Task.FromResult("success");
            },
            CancellationToken.None,
            config);

        Assert.True(result.Success);
        Assert.Equal("success", result.Result);
        Assert.Equal(3, result.AttemptsUsed);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ExceedsMaxRetries_ReturnsFailure()
    {
        var config = new RetryConfig { MaxRetries = 2, RetryBaseDelayMs = 10, UseJitter = false };

        var result = await RetryHandler.ExecuteWithRetryAsync<string>(
            _ => throw new HttpRequestException("Persistent error"),
            CancellationToken.None,
            config);

        Assert.False(result.Success);
        Assert.Equal(3, result.AttemptsUsed);
        Assert.NotNull(result.LastException);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_NonRetryableException_DoesNotRetry()
    {
        var config = new RetryConfig { MaxRetries = 5, RetryBaseDelayMs = 10, UseJitter = false };

        var result = await RetryHandler.ExecuteWithRetryAsync<string>(
            _ => throw new InvalidOperationException("Non-retryable"),
            CancellationToken.None,
            config);

        Assert.False(result.Success);
        Assert.Equal(1, result.AttemptsUsed);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_UserCancellation_ThrowsImmediately()
    {
        using var cts = new CancellationTokenSource();
        var config = new RetryConfig { MaxRetries = 5, RetryBaseDelayMs = 1000, UseJitter = false };
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryHandler.ExecuteWithRetryAsync<string>(
                _ => throw new HttpRequestException("Should not be reached"),
                cts.Token,
                config));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ReportsAttemptsViaOnRetry()
    {
        int retryCount = 0;
        var config = new RetryConfig { MaxRetries = 3, RetryBaseDelayMs = 10, UseJitter = false };

        var result = await RetryHandler.ExecuteWithRetryAsync<string>(
            _ => throw new HttpRequestException("Boom"),
            CancellationToken.None,
            config,
            (attempt, _, _) => retryCount++);

        Assert.Equal(3, retryCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_TracksTotalDuration()
    {
        var config = new RetryConfig { MaxRetries = 1, RetryBaseDelayMs = 50, UseJitter = false };

        var result = await RetryHandler.ExecuteWithRetryAsync<string>(
            _ => throw new HttpRequestException("Boom"),
            CancellationToken.None,
            config);

        Assert.True(result.TotalDuration >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_DefaultConfig_IsUsed()
    {
        RetryHandler.DefaultConfig = new RetryConfig { MaxRetries = 1, RetryBaseDelayMs = 10, UseJitter = false };

        try
        {
            var result = await RetryHandler.ExecuteWithRetryAsync<string>(
                _ => throw new HttpRequestException("Boom"),
                CancellationToken.None);

            Assert.Equal(2, result.AttemptsUsed);
        }
        finally
        {
            RetryHandler.DefaultConfig = new RetryConfig();
        }
    }

    [Fact]
    public void PrintRetryStatus_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            RetryHandler.PrintRetryStatus(2, 5, TimeSpan.FromSeconds(4), "Test reason"));
        Assert.Null(exception);
    }
}
