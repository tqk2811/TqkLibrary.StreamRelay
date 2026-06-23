using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.AspNetCore.Extensions;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Extensions;

namespace TqkLibrary.StreamRelay.Demo
{
    /// <summary>Builds the demo relay web app: AddStreamRelay + the FFmpeg demuxer + the WS endpoints + a static viewer.</summary>
    public static class RelayHostFactory
    {
        public static WebApplication Build(string[] args, string? url, DemuxMode demuxMode)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            if (!string.IsNullOrEmpty(url))
                builder.WebHost.UseUrls(url!);

            builder.Services.AddStreamRelay(o =>
            {
                o.DemuxMode = demuxMode;
            });
            builder.Services.AddFFmpegDemuxer();

            WebApplication app = builder.Build();

            app.UseWebSockets();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapRelayIngest();
            app.MapRelayView();

            return app;
        }
    }
}
