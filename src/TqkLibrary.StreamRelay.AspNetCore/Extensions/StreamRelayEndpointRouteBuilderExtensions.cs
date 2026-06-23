using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TqkLibrary.StreamRelay.AspNetCore.Extensions
{
    /// <summary>Maps the relay WebSocket endpoints. Call <c>app.UseWebSockets()</c> before these.</summary>
    public static class StreamRelayEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Map the ingest endpoint (default <c>/relay/ingest/{streamId:guid}</c>). A device opens a
        /// WebSocket here and streams container bytes; an optional <c>?format=</c> hint narrows the demuxer.
        /// </summary>
        public static IEndpointConventionBuilder MapRelayIngest(this IEndpointRouteBuilder endpoints, string pattern = "/relay/ingest/{streamId:guid}")
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));

            return endpoints.Map(pattern, async (HttpContext context, Guid streamId) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                RelayConnectionHandler handler = context.RequestServices.GetRequiredService<RelayConnectionHandler>();
                string? format = context.Request.Query.TryGetValue("format", out var f) ? f.ToString() : null;
                if (!handler.IsFormatAllowed(format))
                {
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    return;
                }

                // Create the demuxer before upgrading so a worker-cap rejection returns 503 (not a dead socket).
                if (!handler.TryCreateDemuxer(format, out var demuxer) || demuxer == null)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await handler.HandleIngestAsync(streamId, demuxer, socket, context.RequestAborted).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Map the native viewer endpoint (default <c>/relay/view/{streamId:guid}</c>). A client opens a
        /// WebSocket and receives framed init + packets. Returns 404 if no device is streaming this id.
        /// </summary>
        public static IEndpointConventionBuilder MapRelayView(this IEndpointRouteBuilder endpoints, string pattern = "/relay/view/{streamId:guid}")
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));

            return endpoints.Map(pattern, async (HttpContext context, Guid streamId) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                RelayConnectionHandler handler = context.RequestServices.GetRequiredService<RelayConnectionHandler>();

                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                bool handled = await handler.HandleViewAsync(streamId, socket, context.RequestAborted).ConfigureAwait(false);
                if (!handled && socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "no such stream", context.RequestAborted).ConfigureAwait(false);
                }
            });
        }
    }
}
