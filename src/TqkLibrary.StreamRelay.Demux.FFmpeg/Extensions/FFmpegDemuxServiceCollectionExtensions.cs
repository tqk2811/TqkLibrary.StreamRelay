using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TqkLibrary.StreamRelay.Interfaces;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Extensions
{
    /// <summary>DI registration for the FFmpeg demuxer. Call after <c>AddStreamRelay(...)</c>.</summary>
    public static class FFmpegDemuxServiceCollectionExtensions
    {
        /// <summary>
        /// Replace the relay's demuxer factory with the FFmpeg one (in-process P/Invoke or out-of-process
        /// worker, per <c>StreamRelayOptions.DemuxMode</c>).
        /// </summary>
        public static IServiceCollection AddFFmpegDemuxer(this IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            services.RemoveAll<IStreamDemuxerFactory>();
            services.AddSingleton<IStreamDemuxerFactory, FFmpegStreamDemuxerFactory>();
            return services;
        }
    }
}
