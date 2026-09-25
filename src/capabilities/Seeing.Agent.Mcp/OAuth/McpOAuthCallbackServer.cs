using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// OAuth 回调服务器 - 使用 HttpListener 监听 localhost。
    /// <para>
    /// 支持同进程并发授权：单个监听端口只绑定一次，按 <c>state</c> 索引各自的等待源，
    /// HTTP 回调按 query 中的 state 分发；无匹配 state 的回调一律拒绝，避免串扰。
    /// </para>
    /// </summary>
    public class McpOAuthCallbackServer : IMcpOAuthCallbackServer, IDisposable
    {
        private readonly ILogger<McpOAuthCallbackServer> _logger;
        private readonly object _startLock = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingCallbacks =
            new(StringComparer.Ordinal);
        private HttpListener? _listener;
        private Task? _listenerTask;
        private CancellationTokenSource? _listenerCts;
        private int _port;
        private bool _disposed;

        public McpOAuthCallbackServer(ILogger<McpOAuthCallbackServer> logger)
        {
            _logger = logger;
        }

        /// <summary>确保服务器运行，返回端口号；已启动时复用同一监听端口（不重置任何在途等待）。</summary>
        public Task<int> EnsureRunningAsync()
        {
            lock (_startLock)
            {
                if (_listener == null)
                {
                    _listenerCts = new CancellationTokenSource();

                    // Find an available port
                    _port = GetAvailablePort();

                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{_port}/");

                    try
                    {
                        _listener.Start();
                    }
                    catch (HttpListenerException ex)
                    {
                        _logger.LogError(ex, "Failed to start HTTP listener on port {Port}", _port);
                        throw;
                    }

                    // Start listening for requests in the background
                    _listenerTask = ListenAsync(_listenerCts.Token);

                    _logger.LogInformation("OAuth callback server started on port {Port}", _port);
                }

                return Task.FromResult(_port);
            }
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                    _ = HandleRequestAsync(context);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    // Listener stopped
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while listening for HTTP requests");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // Only handle /callback path
                if (!request.Url?.AbsolutePath.Equals("/callback", StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteResponseAsync(response, "<html><body><h1>Not Found</h1></body></html>");
                    return;
                }

                // Extract code and state from query string
                var query = request.Url?.Query;
                var code = GetQueryParam(query, "code");
                var state = GetQueryParam(query, "state");

                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponseAsync(response,
                        "<html><body><h1>Authorization Failed</h1><p>Missing code or state parameter.</p></body></html>");
                    return;
                }

                // 按 state 分发；无匹配 state（或已被消费）一律拒绝，避免串扰其它并发授权
                if (!_pendingCallbacks.TryGetValue(state, out var pending) || !pending.TrySetResult(code))
                {
                    _logger.LogWarning("收到无匹配 state 的 OAuth 回调，已拒绝: {State}", state);
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponseAsync(response,
                        "<html><body><h1>Authorization Failed</h1><p>Unknown or expired state.</p></body></html>");
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteResponseAsync(response,
                    "<html><body><h1>Authorization Complete</h1><p>You can close this window.</p></body></html>");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling HTTP request");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                response.Close();
            }
        }

        private static string? GetQueryParam(string? query, string name)
        {
            if (string.IsNullOrEmpty(query)) return null;

            var startIndex = query.IndexOf($"{name}=", StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0) return null;

            startIndex += name.Length + 1;
            var endIndex = query.IndexOf('&', startIndex);
            if (endIndex < 0) endIndex = query.Length;

            return Uri.UnescapeDataString(query.Substring(startIndex, endIndex - startIndex));
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, string content)
        {
            response.ContentType = "text/html";
            var buffer = System.Text.Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        /// <summary>等待指定 state 的回调；超时或服务器释放时抛 <see cref="TimeoutException"/>。</summary>
        public async Task<(string Code, string State)> WaitForCallbackAsync(string state, TimeSpan timeout)
        {
            if (_listener == null)
                throw new InvalidOperationException("Server not started. Call EnsureRunningAsync first.");

            if (string.IsNullOrEmpty(state))
                throw new ArgumentException("state 不能为空", nameof(state));

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingCallbacks.TryAdd(state, tcs))
                throw new InvalidOperationException($"已有相同 state 的授权等待中: {state}");

            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

            try
            {
                var code = await tcs.Task.ConfigureAwait(false);
                return (code, state);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"OAuth callback timed out after {timeout.TotalSeconds} seconds");
            }
            finally
            {
                _pendingCallbacks.TryRemove(state, out _);
            }
        }

        /// <summary>获取回调 URL</summary>
        public string GetCallbackUrl()
        {
            if (_port == 0)
                throw new InvalidOperationException("Server not started. Call EnsureRunningAsync first.");
            return $"http://localhost:{_port}/callback";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _listenerCts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping HTTP listener");
            }

            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Ignore task wait exceptions
            }

            // 唤醒所有在途等待，避免调用方永久挂起
            foreach (var kvp in _pendingCallbacks)
                kvp.Value.TrySetCanceled();
            _pendingCallbacks.Clear();

            _listenerCts?.Dispose();
            _listener = null;
        }
    }
}
