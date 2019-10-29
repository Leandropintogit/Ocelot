// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.
// Modified https://github.com/aspnet/Proxy websockets class to use in Ocelot.

using Microsoft.AspNetCore.Http;
using Ocelot.Logging;
using Ocelot.Middleware;
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ocelot.WebSockets.Middleware
{
    public class WebSocketsProxyMiddleware : OcelotMiddleware
    {
        private static readonly string[] NotForwardedWebSocketHeaders = { "Connection", "Host", "Upgrade", "Sec-WebSocket-Accept", "Sec-WebSocket-Protocol", "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions" };
        private const int DefaultWebSocketBufferSize = 4096;
        private readonly OcelotRequestDelegate _next;

        public WebSocketsProxyMiddleware(OcelotRequestDelegate next, IOcelotLoggerFactory loggerFactory)
                : base(loggerFactory.CreateLogger<WebSocketsProxyMiddleware>())
        {
            _next = next;
        }

        private static async Task PumpWebSocket(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[DefaultWebSocketBufferSize];
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await CloseWebSocket(destination, cancellationToken);
                    return;
                }
                catch (WebSocketException e)
                {
                    if (e.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely)
                    {
                        throw;
                    }

                    await CloseWebSocket(destination, cancellationToken);
                        return;
                    }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseWebSocket(destination, cancellationToken, source.CloseStatus.GetValueOrDefault(), source.CloseStatusDescription);
                    return;
                }

                await destination.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
            }
        }

        public async Task Invoke(DownstreamContext context)
        {
            await Proxy(context.HttpContext, context.DownstreamRequest.ToUri());
        }

        private static Task CloseWebSocket(WebSocket webSocket, CancellationToken cancellationToken,
            WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string message = "")
        {
            return webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived
                ? webSocket.CloseAsync(closeStatus, message, cancellationToken) : Task.CompletedTask;
        }

        private async Task Proxy(HttpContext context, string serverEndpoint)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (serverEndpoint == null)
            {
                throw new ArgumentNullException(nameof(serverEndpoint));
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                throw new InvalidOperationException();
            }

            var client = new ClientWebSocket();
            foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
            {
                client.Options.AddSubProtocol(protocol);
            }

            foreach (var (key, value) in context.Request.Headers)
            {
                if (NotForwardedWebSocketHeaders.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                    try
                    {
                    client.Options.SetRequestHeader(key, value);
                    }
                    catch (ArgumentException)
                    {
                        // Expected in .NET Framework for headers that are mistakenly considered restricted.
                        // See: https://github.com/dotnet/corefx/issues/26627
                        // .NET Core does not exhibit this issue, ironically due to a separate bug (https://github.com/dotnet/corefx/issues/18784)
                    }
                }

            var destinationUri = new Uri(serverEndpoint);
            await client.ConnectAsync(destinationUri, context.RequestAborted);

            using var server = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol);
            await Task.WhenAll(PumpWebSocket(client, server, context.RequestAborted), PumpWebSocket(server, client, context.RequestAborted));
        }
    }
}
