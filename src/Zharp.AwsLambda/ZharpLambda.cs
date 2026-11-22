using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using OpenTelemetry.Instrumentation.AWSLambda;
using OpenTelemetry.Trace;
using Zharp.Internal;

namespace Zharp.AwsLambda
{
    /// <summary>
    /// Zharp for a Lambda handler:
    /// <code>
    /// static readonly Func&lt;Order, ILambdaContext, Task&lt;Receipt&gt;&gt; Handle = ZharpLambda.Wrap&lt;Order, Receipt&gt;(PlaceAsync);
    /// public Task&lt;Receipt&gt; Handler(Order order, ILambdaContext context) =&gt; Handle(order, context);
    /// </code>
    /// Each invocation is a span (the first marked as the cold start); what it throws is reported
    /// once, as unhandled; and everything is sent before the handler returns, since Lambda freezes
    /// the process as soon as it has.
    /// </summary>
    public static class ZharpLambda
    {
        static int invocations;

        public static Func<TInput, ILambdaContext, Task<TOutput>> Wrap<TInput, TOutput>(Func<TInput, ILambdaContext, Task<TOutput>> handler, Action<ZharpOptions>? configure = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return async (input, context) =>
            {
                ZharpCore? core = Start(configure);
                if (core?.Tracer == null) return await handler(input, context).ConfigureAwait(false);
                try
                {
                    return await AWSLambdaWrapper.TraceAsync(core.Tracer, (i, c) => Invoke(core, handler, i, c), input, context).ConfigureAwait(false);
                }
                finally
                {
                    Flush(core, context);
                }
            };
        }

        public static Func<TInput, ILambdaContext, Task> Wrap<TInput>(Func<TInput, ILambdaContext, Task> handler, Action<ZharpOptions>? configure = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Func<TInput, ILambdaContext, Task<bool>> wrapped = Wrap<TInput, bool>(async (i, c) =>
            {
                await handler(i, c).ConfigureAwait(false);
                return true;
            }, configure);
            return (input, context) => wrapped(input, context);
        }

        public static Func<TInput, ILambdaContext, TOutput> Wrap<TInput, TOutput>(Func<TInput, ILambdaContext, TOutput> handler, Action<ZharpOptions>? configure = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Func<TInput, ILambdaContext, Task<TOutput>> wrapped = Wrap<TInput, TOutput>((i, c) => Task.FromResult(handler(i, c)), configure);
            return (input, context) => wrapped(input, context).GetAwaiter().GetResult();
        }

        static async Task<TOutput> Invoke<TInput, TOutput>(ZharpCore core, Func<TInput, ILambdaContext, Task<TOutput>> handler, TInput input, ILambdaContext context)
        {
            Activity? span = Activity.Current;
            span?.SetTag("faas.coldstart", Interlocked.Increment(ref invocations) == 1);
            try
            {
                return await handler(input, context).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                core.Report(e, handled: false, mechanism: "aws-lambda", span: span);
                throw;
            }
        }

        static ZharpCore? Start(Action<ZharpOptions>? configure)
        {
            ZharpCore? running = ZharpCore.Current;
            if (running != null) return running;
            var options = new ZharpOptions();
            configure?.Invoke(options);
            return ZharpSdk.Start(options, null, new LambdaIntegration());
        }

        /// <summary>What's waiting goes out now, within the time the invocation has left.</summary>
        static void Flush(ZharpCore core, ILambdaContext context)
        {
            int left = 5000;
            try
            {
                if (context?.RemainingTime is TimeSpan remaining) left = (int)Math.Max(200, Math.Min(5000, remaining.TotalMilliseconds - 200));
            }
            catch
            {
                // No context (a test): the default.
            }
            core.Flush(left);
        }
    }

    /// <summary>OpenTelemetry's Lambda setup on Zharp's tracer: the invocation spans and the function's resource.</summary>
    internal sealed class LambdaIntegration : IZharpIntegration
    {
        public void ConfigureTracing(TracerProviderBuilder builder, ZharpCore core) => builder.AddAWSLambdaConfigurations(o => o.DisableAwsXRayContextExtraction = true);
        public void Started(ZharpCore core) { }
        public void AddServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services, ZharpCore core) { }
    }
}
