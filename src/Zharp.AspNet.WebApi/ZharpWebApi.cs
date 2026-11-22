using System.Diagnostics;
using System.Web.Http.ExceptionHandling;
using Zharp.Internal;

namespace System.Web.Http
{
    /// <summary><c>config.AddZharp()</c> in <c>WebApiConfig.Register</c>.</summary>
    public static class ZharpWebApiExtensions
    {
        /// <summary>
        /// Report what Web API controllers throw. Web API turns it into a 500 itself, so it never
        /// reaches Application_Error, where Zharp.AspNet sees the rest. Reported once, when the
        /// request ends: unhandled when the answer was 5xx.
        /// </summary>
        public static HttpConfiguration AddZharp(this HttpConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            Zharp.AspNet.ZharpAspNet.Start();
            foreach (object logger in config.Services.GetServices(typeof(IExceptionLogger)))
            {
                if (logger is Zharp.AspNet.WebApi.ZharpExceptionLogger) return config;
            }
            config.Services.Add(typeof(IExceptionLogger), new Zharp.AspNet.WebApi.ZharpExceptionLogger());
            return config;
        }
    }
}

namespace Zharp.AspNet.WebApi
{
    /// <summary>Web API's unhandled exceptions, to Zharp.</summary>
    public sealed class ZharpExceptionLogger : ExceptionLogger
    {
        public override void Log(ExceptionLoggerContext context)
        {
            System.Exception? error = context?.Exception;
            if (error == null) return;
            ZharpCore.Current?.Errors.Hold(error, "aspnet-webapi", uncaught: true, Activity.Current);
        }
    }
}
