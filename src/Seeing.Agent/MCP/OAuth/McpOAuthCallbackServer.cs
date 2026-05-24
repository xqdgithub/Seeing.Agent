using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// OAuth 回调服务器 - 使用 HttpListener 监听 localhost
    /// </summary>
    public class McpOAuthCallbackServer : IDisposable
    {
        private readonly ILogger<McpOAuthCallbackServer> _logger;
        private HttpListener? _listener;
        private Task? _listenerTask;
        private CancellationTokenSource? _listenerCts;
        private TaskCompletionSource<(string Code, string State)>? _tcs;
        private int _port;
        private bool _disposed;

        public McpOAuthCallbackServer(ILogger<McpOAuthCallbackServer> logger)
        {
            _logger = logger;
        }

        /// <summary>确保服务器运行，返回端口号</summary>
        public async Task<int> EnsureRunningAsync()
        {
            if (_listener != null) return _port;

            _tcs = new TaskCompletionSource<(string Code, string State)>();
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
            return _port;
        }

        private int GetAvailablePort()
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
                    _ = HandleRequestAsync(context, cancellationToken);
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

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
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

                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(state))
                {
                    _tcs?.TrySetResult((code, state));
                    await WriteResponseAsync(response,
                        "<html><body><h1>Authorization Complete</h1><p>You can close this window.</p></body></html>");
                }
                else
                {
                    await WriteResponseAsync(response,
                        "<html><body><h1>Authorization Failed</h1><p>Missing code or state parameter.</p></body></html>");
                }
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
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/html";
            var buffer = System.Text.Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        /// <summary>等待回调</summary>
        public async Task<(string Code, string State)> WaitForCallbackAsync(TimeSpan timeout)
        {
            if (_tcs == null)
                throw new InvalidOperationException("Server not started. Call EnsureRunningAsync first.");

            using var cts = new CancellationTokenSource(timeout);
            cts.Token.Register(() => _tcs.TrySetCanceled(cts.Token));

            try
            {
                return await _tcs.Task;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"OAuth callback timed out after {timeout.TotalSeconds} seconds");
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

            _listenerCts?.Dispose();
            _listener = null;
            _tcs = null;
        }
    }
}
