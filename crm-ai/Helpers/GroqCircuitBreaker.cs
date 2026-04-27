using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace crm_ai.Helpers
{
    public static class GroqCircuitBreaker
    {
        /// <summary>
        /// Returns a combined policy: Timeout → Retry → CircuitBreaker.
        /// Order matters: outermost = last to catch, innermost = first to catch.
        /// CircuitBreaker wraps Retry wraps Timeout.
        /// </summary>
        public static IAsyncPolicy<HttpResponseMessage> GetCombinedPolicy(
            GroqResilienceOptions options,
            ILogger logger)
        {
            // 1. Timeout — Polly-level, not HttpClient.Timeout
            //    Throws TimeoutRejectedException which the circuit breaker counts
            var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(
                options.TimeoutSeconds,
                TimeoutStrategy.Optimistic);

            // 2. Retry — handles transient errors + 429 + 5xx
            //    Waits exponentially between retries
            var retryPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .Or<TimeoutRejectedException>()
                .OrResult(r =>
                    (int)r.StatusCode >= 500 ||
                    (int)r.StatusCode == 429)
                .WaitAndRetryAsync(
                    retryCount: options.RetryCount,
                    sleepDurationProvider: attempt =>
                        TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (outcome, delay, attempt, _) =>
                    {
                        var reason = outcome.Exception?.Message
                            ?? outcome.Result?.StatusCode.ToString();
                        logger.LogWarning(
                            "Groq retry {Attempt}/{Max} after {Delay}s — {Reason}",
                            attempt, options.RetryCount, delay.TotalSeconds, reason);
                    });

            // 3. Circuit breaker — counts failures from retry exhaustion
            var circuitBreakerPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .Or<TimeoutRejectedException>()
                .OrResult(r =>
                    (int)r.StatusCode >= 500 ||
                    (int)r.StatusCode == 429)
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking:
                        options.FailuresBeforeBreaking,
                    durationOfBreak:
                        TimeSpan.FromSeconds(options.BreakDurationSeconds),
                    onBreak: (outcome, duration) =>
                    {
                        var reason = outcome.Exception?.Message
                            ?? outcome.Result?.StatusCode.ToString();
                        logger.LogWarning(
                            "Groq circuit OPEN for {Duration}s — {Reason}",
                            duration.TotalSeconds, reason);
                    },
                    onReset: () =>
                        logger.LogInformation(
                            "Groq circuit CLOSED — resuming"),
                    onHalfOpen: () =>
                        logger.LogInformation(
                            "Groq circuit HALF-OPEN — testing"));

            // Wrap: circuit breaker → retry → timeout
            // Execution order: timeout fires first, retry catches it,
            // circuit breaker counts retry exhaustion
            return Policy.WrapAsync(
                circuitBreakerPolicy,
                retryPolicy,
                timeoutPolicy);
        }
    }
}