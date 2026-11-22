using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Cloud.Functions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Zharp.Internal;

namespace Zharp.GoogleCloudFunctions
{
    /// <summary>
    /// Zharp for a Cloud Run function (Cloud Functions), with one line in the function's project:
    /// <code>[assembly: FunctionsStartup(typeof(Zharp.GoogleCloudFunctions.ZharpStartup))]</code>
    /// Each invocation is a span named by the function (<c>POST shop.Checkout</c>), what it throws
    /// is reported once as unhandled, and what's waiting is sent before the response, since the
    /// instance's CPU is throttled once it has answered.
    /// </summary>
    public class ZharpStartup : FunctionsStartup
    {
        public override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
        {
            services.AddZharp();
        }

        public override void Configure(WebHostBuilderContext context, IApplicationBuilder app)
        {
            string function = Environment.GetEnvironmentVariable("FUNCTION_TARGET") ?? Environment.GetEnvironmentVariable("K_SERVICE") ?? "function";
            app.Use(async (HttpContext http, Func<Task> next) =>
            {
                Activity? span = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpActivityFeature>()?.Activity ?? Activity.Current;
                if (span != null)
                {
                    span.DisplayName = http.Request.Method + " " + function;
                    span.SetTag("faas.name", function);
                    span.SetTag("faas.trigger", "http");
                    span.SetTag("http.route", function);
                }
                try
                {
                    await next().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ZharpCore.Current?.Report(e, handled: false, mechanism: "gcp-functions", span: span);
                    throw;
                }
                finally
                {
                    ZharpCore.Current?.Flush(3000);
                }
            });
        }
    }
}
