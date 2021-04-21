// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    public class BrowserRefreshServer : IAsyncDisposable
    {
        private readonly byte[] ReloadMessage = Encoding.UTF8.GetBytes("Reload");
        private readonly byte[] WaitMessage = Encoding.UTF8.GetBytes("Wait");
        private readonly IReporter _reporter;
        private readonly TaskCompletionSource _taskCompletionSource;
        private IHost _refreshServer;
        private WebSocket _webSocket;

        public BrowserRefreshServer(IReporter reporter)
        {
            _reporter = reporter;
            _taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal static string GetAutoReloadUrl(string configuredEnv)
        {
            var url = "http://127.0.0.1:0";
            var uri = new Uri(url);
            
            if (configuredEnv is not null && Uri.TryCreate(configuredEnv.Replace("ws://","http://") , UriKind.Absolute, out uri))
            {
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            }
            
            return url;
        }

        public async ValueTask<string> StartAsync(CancellationToken cancellationToken)
        {
            var configuredEnv = Environment.GetEnvironmentVariable("DOTNET_WATCH_AUTO_RELOAD_WS_HOSTNAME");

            var url = GetAutoReloadUrl(configuredEnv);

            _refreshServer = new HostBuilder()
                .ConfigureWebHost(builder =>
                {
                    builder.UseKestrel();
                    builder.UseUrls(url);

                    builder.Configure(app =>
                    {
                        app.UseWebSockets();
                        app.Run(WebSocketRequest);
                    });
                })
                .Build();

            await _refreshServer.StartAsync(cancellationToken);

            var serverUrl = _refreshServer.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
                .First();

            return serverUrl.Replace("http://", "ws://");
        }

        private async Task WebSocketRequest(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            _webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await _taskCompletionSource.Task;
        }

        public async ValueTask SendMessage(ReadOnlyMemory<byte> messageBytes, CancellationToken cancellationToken = default)
        {
            if (_webSocket == null || _webSocket.CloseStatus.HasValue)
            {
                return;
            }

            try
            {
                await _webSocket.SendAsync(messageBytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _reporter.Verbose($"Refresh server error: {ex}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_webSocket != null)
            {
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
                _webSocket.Dispose();
            }

            if (_refreshServer != null)
            {
                _refreshServer.Dispose();
            }

            _taskCompletionSource.TrySetResult();
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken) => SendMessage(ReloadMessage, cancellationToken);

        public ValueTask SendWaitMessageAsync(CancellationToken cancellationToken) => SendMessage(WaitMessage, cancellationToken);
    }
}