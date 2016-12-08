// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Sockets.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Sockets
{
    public class HttpConnectionDispatcher
    {
        private readonly ConnectionManager _manager;
        private readonly PipelineFactory _pipelineFactory;
        private readonly ILoggerFactory _loggerFactory;

        public HttpConnectionDispatcher(ConnectionManager manager, PipelineFactory factory, ILoggerFactory loggerFactory)
        {
            _manager = manager;
            _pipelineFactory = factory;
            _loggerFactory = loggerFactory;
        }

        public async Task ExecuteAsync<TEndPoint>(string path, HttpContext context) where TEndPoint : EndPoint
        {
            // Get the end point mapped to this http connection
            var endpoint = (EndPoint)context.RequestServices.GetRequiredService<TEndPoint>();

            if (context.Request.Path.StartsWithSegments(path + "/getid"))
            {
                await ProcessGetId(context, endpoint.Mode);
            }
            else if (context.Request.Path.StartsWithSegments(path + "/send"))
            {
                await ProcessSend(context);
            }
            else
            {
                await ExecuteStreamingEndpointAsync(path, context, endpoint);
            }
        }

        private async Task ExecuteEndpointAsync(string path, HttpContext context, EndPoint endpoint)
        {
            var format =
                string.Equals(context.Request.Query["format"], "binary", StringComparison.OrdinalIgnoreCase)
                    ? Format.Binary
                    : Format.Text;

            // Server sent events transport
            if (context.Request.Path.StartsWithSegments(path + "/sse"))
            {
                var state = InitializePersistentConnection(context, endpoint, format);

                var sse = new ServerSentEvents(state.Connection);

                await DoPersistentConnection(endpoint, sse, context, connection);

                _manager.RemoveConnection(state.Connection.ConnectionId);
            }
            else if (context.Request.Path.StartsWithSegments(path + "/ws"))
            {
                var state = InitializePersistentConnection(context, endpoint, format);

                var ws = new WebSockets(connection, format, _loggerFactory);

                await DoPersistentConnection(endpoint, ws, context, connection);

                _manager.RemoveConnection(state.Connection.ConnectionId);
            }
            else if (context.Request.Path.StartsWithSegments(path + "/poll"))
            {
                bool isNewConnection;
                var state = GetOrCreateConnection(context, endpoint.Mode, out isNewConnection);
                var connection = (StreamingConnection)state.Connection;

                // TODO: this is wrong. + how does the user add their own metadata based on HttpContext
                var formatType = (string)context.Request.Query["formatType"];
                state.Connection.Metadata["formatType"] = string.IsNullOrEmpty(formatType) ? "json" : formatType;

                // Mark the connection as active
                state.Active = true;

                RegisterLongPollingDisconnect(context, connection);

                var longPolling = new LongPolling(connection);

                // Start the transport
                var transportTask = longPolling.ProcessRequestAsync(context);

                Task endpointTask = null;

                // Raise OnConnected for new connections only since polls happen all the time
                if (isNewConnection)
                {
                    state.Connection.Metadata["transport"] = "poll";
                    state.Connection.Metadata.Format = format;
                    state.Connection.User = context.User;

                    // REVIEW: This is super gross, this all needs to be cleaned up...
                    state.Close = async () =>
                    {
                        connection.Transport.Dispose();

                        await endpointTask;
                    };

                    endpointTask = endpoint.OnConnectedAsync(connection);
                    state.Connection.Metadata["endpoint"] = endpointTask;
                }
                else
                {
                    // Get the endpoint task from connection state
                    endpointTask = state.Connection.Metadata.Get<Task>("endpoint");
                }

                var resultTask = await Task.WhenAny(endpointTask, transportTask);

                if (resultTask == endpointTask)
                {
                    // Notify the long polling transport to end
                    connection.Transport.Dispose();

                    await transportTask;
                }

                // Mark the connection as inactive
                state.LastSeen = DateTimeOffset.UtcNow;
                state.Active = false;
            }
        }

        private ConnectionState InitializePersistentConnection(HttpContext context, EndPoint endpoint, Format format)
        {
            // Get the connection state for the current http context
            var state = GetOrCreateConnection(context, endpoint.Mode);
            state.Connection.User = context.User;
            state.Connection.Metadata["transport"] = "sse";
            state.Connection.Metadata.Format = format;

            // TODO: this is wrong. + how does the user add their own metadata based on HttpContext
            var formatType = (string)context.Request.Query["formatType"];
            state.Connection.Metadata["formatType"] = string.IsNullOrEmpty(formatType) ? "json" : formatType;
            return state;
        }

        private static async Task DoPersistentConnection(StreamingEndPoint endpoint,
                                                         IHttpTransport transport,
                                                         HttpContext context,
                                                         StreamingConnection connection)
        {
            // Register this transport for disconnect
            RegisterDisconnect(context, connection);

            // Start the transport
            var transportTask = transport.ProcessRequestAsync(context);

            // Call into the end point passing the connection
            var endpointTask = endpoint.OnConnectedAsync(connection);

            // Wait for any of them to end
            await Task.WhenAny(endpointTask, transportTask);

            // Kill the channel
            connection.Transport.Dispose();

            // Wait for both
            await Task.WhenAll(endpointTask, transportTask);
        }

        private static void RegisterLongPollingDisconnect(HttpContext context, Connection connection)
        {
            // For long polling, we need to end the transport but not the overall connection so we write 0 bytes
            context.RequestAborted.Register(state => ((HttpConnection)state).Output.WriteAsync(Span<byte>.Empty), connection.Transport);
        }

        private static void RegisterDisconnect(HttpContext context, Connection connection)
        {
            // We just kill the output writing as a signal to the transport that it is done
            context.RequestAborted.Register(state => ((HttpConnection)state).Output.CompleteWriter(), connection.Transport);
        }

        private Task ProcessGetId(HttpContext context, ConnectionMode mode)
        {
            // Establish the connection
            var state = _manager.CreateConnection(mode);

            // Get the bytes for the connection id
            var connectionIdBuffer = Encoding.UTF8.GetBytes(state.Connection.ConnectionId);

            // Write it out to the response with the right content length
            context.Response.ContentLength = connectionIdBuffer.Length;
            return context.Response.Body.WriteAsync(connectionIdBuffer, 0, connectionIdBuffer.Length);
        }

        private Task ProcessSend(HttpContext context)
        {
            var connectionId = context.Request.Query["id"];
            if (StringValues.IsNullOrEmpty(connectionId))
            {
                throw new InvalidOperationException("Missing connection id");
            }

            ConnectionState state;
            if (_manager.TryGetConnection(connectionId, out state))
            {
                if (state.Connection.Mode == ConnectionMode.Streaming)
                {
                    var streamingState = (StreamingConnectionState)state;

                    return context.Request.Body.CopyToAsync(streamingState.Application.Output);
                }
                else
                {
                    throw new NotImplementedException();
                }
            }

            throw new InvalidOperationException("Unknown connection id");
        }

        private ConnectionState GetOrCreateConnection(HttpContext context, ConnectionMode mode)
        {
            bool isNewConnection;
            return GetOrCreateConnection(context, mode, out isNewConnection);
        }

        private ConnectionState GetOrCreateConnection(HttpContext context, ConnectionMode mode, out bool isNewConnection)
        {
            var connectionId = context.Request.Query["id"];
            ConnectionState connectionState;
            isNewConnection = false;

            // There's no connection id so this is a brand new connection
            if (StringValues.IsNullOrEmpty(connectionId))
            {
                isNewConnection = true;
                connectionState = _manager.CreateConnection(mode);
            }
            else
            {
                // REVIEW: Fail if not reserved? Reused an existing connection id?

                // There's a connection id
                if (!_manager.TryGetConnection(connectionId, out connectionState))
                {
                    throw new InvalidOperationException("Unknown connection id");
                }

                // Reserved connection, we need to provide a channel
                var connection = (StreamingConnection)connectionState.Connection;
                if (connection.Transport == null)
                {
                    isNewConnection = true;
                    connection.Transport = new HttpConnection(_pipelineFactory);
                    connectionState.Active = true;
                    connectionState.LastSeen = DateTimeOffset.UtcNow;
                }
            }

            return connectionState;
        }
    }
}
