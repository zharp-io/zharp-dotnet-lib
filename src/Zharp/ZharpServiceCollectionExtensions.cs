using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zharp;
using Zharp.Internal;
using Zharp.Logging;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary><c>builder.Services.AddZharp()</c>.</summary>
    public static class ZharpServiceCollectionExtensions
    {
        /// <summary>
        /// Start Zharp for this host: errors, traces and failed queries, the agent stamp on console
        /// lines, and the app's logs with <c>ZHARP_LOGS=true</c>. Settings come from the
        /// environment (<c>ZHARP_KEY</c>, …) and the <c>"Zharp"</c> section of the configuration;
        /// <paramref name="configure"/> overrides both. With the Zharp.AspNetCore package
        /// referenced, requests are traced and their errors reported too.
        /// </summary>
        public static IServiceCollection AddZharp(this IServiceCollection services, Action<ZharpOptions>? configure = null)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (services.Any(d => d.ServiceType == typeof(ZharpMarker))) return services;
            services.AddSingleton<ZharpMarker>();

            var options = new ZharpOptions();
            configure?.Invoke(options);
            // WebApplicationBuilder and HostApplicationBuilder register their configuration as an
            // instance, so it can be read now, before the host is built.
            var configuration = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
            ZharpCore? core = ZharpSdk.Start(options, configuration);
            if (core == null) return services;

            services.AddLogging(b => ZharpLoggingSetup.Add(b, core.Settings.LogCorrelation));
            services.AddSingleton<IHostedService, ZharpHostedService>();
            foreach (IZharpIntegration integration in core.Integrations)
            {
                try { integration.AddServices(services, core); }
                catch (Exception e) { core.DebugLog("integration services: " + e.Message); }
            }
            return services;
        }

        sealed class ZharpMarker { }
    }

    /// <summary>Sends what's waiting when the host stops.</summary>
    internal sealed class ZharpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            ZharpCore.Current?.Flush(5000);
            return Task.CompletedTask;
        }
    }
}

namespace Microsoft.Extensions.Logging
{
    /// <summary><c>logging.AddZharp()</c>, for a <c>LoggerFactory</c> built by hand next to <c>ZharpSdk.Init()</c>.</summary>
    public static class ZharpLoggingBuilderExtensions
    {
        /// <summary>
        /// This factory's lines to Zharp (with <c>ZHARP_LOGS=true</c>; exceptions at Error always),
        /// and the agent stamp on its console lines. Call <c>ZharpSdk.Init()</c> first.
        /// </summary>
        public static ILoggingBuilder AddZharp(this ILoggingBuilder builder)
        {
            bool stamp = ZharpCore.Current?.Settings.LogCorrelation ?? true;
            ZharpLoggingSetup.Add(builder, stamp);
            return builder;
        }
    }
}
