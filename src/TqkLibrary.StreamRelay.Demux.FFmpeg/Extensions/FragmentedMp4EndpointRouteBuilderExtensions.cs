using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TqkLibrary.StreamRelay;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Extensions
{
    /// <summary>Maps the browser fMP4/MSE view endpoint, backed by the FFmpeg remuxer.</summary>
    public static class FragmentedMp4EndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Map the fragmented-MP4 viewer (default <c>/relay/view/{streamId:guid}.mp4</c>). A browser
        /// <c>MediaSource</c> (or any HTTP client) GETs this and receives an init segment followed by media
        /// fragments remuxed from the live stream. Returns 404 if no device is streaming this id.
        /// </summary>
        public static IEndpointConventionBuilder MapRelayViewMp4(this IEndpointRouteBuilder endpoints, string pattern = "/relay/view/{streamId:guid}.mp4")
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));

            return endpoints.MapGet(pattern, async (HttpContext context, Guid streamId) =>
            {
                StreamRegistry registry = context.RequestServices.GetRequiredService<StreamRegistry>();
                if (!registry.TryGet(streamId, out StreamSession session))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "video/mp4";
                context.Response.Headers["Cache-Control"] = "no-store";

                Guid viewerId = Guid.NewGuid();
                StreamSession.Subscriber subscriber = session.AddSubscriber(viewerId);
                var sink = new FragmentedMp4PacketSink(context.Response.Body);
                try
                {
                    await session.WriteToSinkAsync(subscriber, sink, context.RequestAborted).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    session.RemoveSubscriber(viewerId);
                    sink.Dispose();
                }
            });
        }
    }
}
