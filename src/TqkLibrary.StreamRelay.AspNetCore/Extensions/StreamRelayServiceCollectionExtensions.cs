using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TqkLibrary.StreamRelay.Interfaces;

namespace TqkLibrary.StreamRelay.AspNetCore.Extensions
{
    /// <summary>DI registration for the stream relay.</summary>
    public static class StreamRelayServiceCollectionExtensions
    {
        /// <summary>
        /// Register the relay services: options, the singleton <see cref="StreamRegistry"/>, the connection
        /// handler, and a fail-fast demuxer factory placeholder. A real demuxer package (e.g.
        /// <c>TqkLibrary.StreamRelay.Demux.FFmpeg</c>) replaces the factory via <c>TryAddSingleton</c> order.
        /// </summary>
        public static IServiceCollection AddStreamRelay(this IServiceCollection services, Action<StreamRelayOptions>? configure = null)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            if (configure != null)
                services.Configure(configure);

            services.AddSingleton(sp =>
            {
                StreamRelayOptions options = sp.GetRequiredService<IOptions<StreamRelayOptions>>().Value;
                return new StreamRegistry(options.Session);
            });

            services.AddSingleton<RelayConnectionHandler>();

            // Only used if no concrete demuxer is registered; the FFmpeg package registers its own first.
            services.TryAddSingleton<IStreamDemuxerFactory, NotConfiguredStreamDemuxerFactory>();

            return services;
        }
    }
}
