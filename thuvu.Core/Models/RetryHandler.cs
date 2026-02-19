using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Configuration for retry behavior
    /// </summary>
    public class RetryConfig
    {
        /// <summary>
        /// Maximum number of retry attempts (default: 5)
        /// </summary>
        public int MaxRetries { get; set; } = 5;

        /// <summary>
        /// Base delay in milliseconds for exponential backoff (default: 2000ms)
        /// </summary>
        public int RetryBaseDelayMs { get; set; } = 2000;

        /// <summary>
        /// Maximum delay in milliseconds between retries (default: 30000ms)
        /// </summary>
        public int RetryMaxDelayMs { get; set; } = 30000;

        /// <summary>
        /// Whether to add jitter to retry delays to avoid thundering herd (default: true)
        /// </summary>
        public bool UseJitter { get; set; } = true;
    }

    /// <summary>
    /// Result of a retryable operation
    /// </summary>
    public class RetryResult<T>
    {
        public bool Success { get; set; }
        public T? Result { get; set; }
        public Exception? LastException { get; set; }
        public int AttemptsUsed { get; set; }
        public TimeSpan TotalDuration { get; set; }
    }

    /// <summary>
    /// Provides retry logic with exponential backoff for transient failures
    /// </summary>
    public static class RetryHandler
    {
        private static readonly Random _jitterRandom = new();
        
        /// <summary>
        /// Default retry configuration
        /// </summary>
        public static RetryConfig DefaultConfig { get; set; } = new();

        /// <summary>
        /// Execute an async operation with retry logic
        /// </summary>
        public static async Task<RetryResult<T>> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken ct,
            RetryConfig? config = null,
            Action<int, Exception, TimeSpan>? onRetry = null)
        {
            config ??= DefaultConfig;
            var result = new RetryResult<T>();
            var startTime = DateTime.Now;
            
            for (int attempt = 1; attempt <= config.MaxRetries + 1; attempt++)
            {
                result.AttemptsUsed = attempt;
                
                try
                {
                    ct.ThrowIfCancellationRequested();
                    result.Result = await operation(ct);
                    result.Success = true;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // User cancelled - don't retry
                    throw;
                }
                catch (Exception ex)
                {
                    result.LastException = ex;
                    
                    // Check if we should retry
                    if (attempt > config.MaxRetries || !ShouldRetry(ex))
                    {
                        result.Success = false;
                        break;
                    }

                    // Calculate delay with exponential backoff
                    var delay = CalculateDelay(attempt, config);
                    
                    // Notify caller of retry
                    onRetry?.Invoke(attempt, ex, delay);
                    
                    // Log retry attempt
                    AgentLogger.LogWarning(
                        "Retry attempt {Attempt}/{MaxRetries} after {DelayMs}ms due to: {Error}",
                        attempt, config.MaxRetries, (int)delay.TotalMilliseconds, GetRetryReason(ex));

                    // Wait before retrying
                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            result.TotalDuration = DateTime.Now - startTime;
            return result;
        }

        /// <summary>
        /// Execute an HTTP request with retry logic
        /// </summary>
        public static async Task<RetryResult<HttpResponseMessage>> ExecuteHttpWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> httpOperation,
            CancellationToken ct,
            RetryConfig? config = null,
            Action<int, Exception, TimeSpan>? onRetry = null)
        {
            return await ExecuteWithRetryAsync(
                async (token) =>
                {
                    var response = await httpOperation(token);
                    
                    // Throw for retryable HTTP status codes
                    if (IsRetryableStatusCode(response.StatusCode))
                    {
                        var content = await response.Content.ReadAsStringAsync(token);
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {content.Substring(0, Math.Min(200, content.Length))}");
                    }
                    
                    return response;
                },
                ct,
                config,
                onRetry);
        }

        /// <summary>
        /// Determine if an exception is retryable
        /// </summary>
        public static bool ShouldRetry(Exception ex)
        {
            return ex switch
            {
                // Network errors - retry
                HttpRequestException httpEx => IsRetryableHttpException(httpEx),
                
                // Timeout - retry
                TaskCanceledException tcEx when tcEx.InnerException is TimeoutException => true,
                TimeoutException => true,
                
                // Socket errors - retry
                System.Net.Sockets.SocketException => true,
                
                // IO errors (transient) - retry
                IOException ioEx when ioEx.Message.Contains("network") => true,
                
                // Don't retry other exceptions
                _ => false
            };
        }

        /// <summary>
        /// Check if HTTP exception is retryable
        /// </summary>
        private static bool IsRetryableHttpException(HttpRequestException ex)
        {
            // Connection refused, network unreachable, etc.
            if (ex.InnerException is System.Net.Sockets.SocketException)
                return true;

            // Check status code if available
            if (ex.StatusCode.HasValue)
                return IsRetryableStatusCode(ex.StatusCode.Value);

            // Default to retry for generic HTTP errors
            return true;
        }

        /// <summary>
        /// Check if HTTP status code is retryable
        /// </summary>
        public static bool IsRetryableStatusCode(HttpStatusCode statusCode)
        {
            return statusCode switch
            {
                HttpStatusCode.TooManyRequests => true,           // 429 - Rate limited
                HttpStatusCode.InternalServerError => true,       // 500
                HttpStatusCode.BadGateway => true,                // 502
                HttpStatusCode.ServiceUnavailable => true,        // 503
                HttpStatusCode.GatewayTimeout => true,            // 504
                HttpStatusCode.RequestTimeout => true,            // 408
                _ => false
            };
        }

        /// <summary>
        /// Check if HTTP status code should NOT be retried
        /// </summary>
        public static bool IsNonRetryableStatusCode(HttpStatusCode statusCode)
        {
            return statusCode switch
            {
                HttpStatusCode.BadRequest => true,                // 400 - Fix the request
                HttpStatusCode.Unauthorized => true,              // 401 - Auth error
                HttpStatusCode.Forbidden => true,                 // 403 - Auth error
                HttpStatusCode.NotFound => true,                  // 404 - Resource doesn't exist
                HttpStatusCode.MethodNotAllowed => true,          // 405 - Wrong method
                HttpStatusCode.UnprocessableEntity => true,       // 422 - Validation error
                _ => false
            };
        }

        /// <summary>
        /// Calculate delay with exponential backoff and optional jitter
        /// </summary>
        private static TimeSpan CalculateDelay(int attempt, RetryConfig config)
        {
            // Exponential backoff: baseDelay * 2^(attempt-1)
            var exponentialDelay = config.RetryBaseDelayMs * Math.Pow(2, attempt - 1);
            
            // Cap at max delay
            var cappedDelay = Math.Min(exponentialDelay, config.RetryMaxDelayMs);
            
            // Add jitter (±25%) to avoid thundering herd
            if (config.UseJitter)
            {
                var jitterRange = cappedDelay * 0.25;
                var jitter = (_jitterRandom.NextDouble() * 2 - 1) * jitterRange;
                cappedDelay += jitter;
            }

            return TimeSpan.FromMilliseconds(Math.Max(0, cappedDelay));
        }

        /// <summary>
        /// Get a human-readable reason for the retry
        /// </summary>
        private static string GetRetryReason(Exception ex)
        {
            return ex switch
            {
                HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.TooManyRequests 
                    => "Rate limited (429)",
                HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.ServiceUnavailable 
                    => "Service unavailable (503)",
                HttpRequestException httpEx when httpEx.StatusCode.HasValue 
                    => $"HTTP {(int)httpEx.StatusCode}",
                HttpRequestException httpEx when httpEx.InnerException is System.Net.Sockets.SocketException 
                    => "Connection refused",
                TaskCanceledException when ex.InnerException is TimeoutException 
                    => "Request timeout",
                TimeoutException 
                    => "Timeout",
                System.Net.Sockets.SocketException sockEx 
                    => $"Socket error: {sockEx.SocketErrorCode}",
                _ => ex.Message.Length > 50 ? ex.Message.Substring(0, 47) + "..." : ex.Message
            };
        }

        /// <summary>
        /// Print retry status to console
        /// </summary>
        public static void PrintRetryStatus(int attempt, int maxRetries, TimeSpan delay, string reason)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  ⟳ Retry {attempt}/{maxRetries}");
            Console.ResetColor();
            Console.Write($" in {delay.TotalSeconds:F1}s");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($" ({reason})");
            Console.ResetColor();
        }
    }
}
