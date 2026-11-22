// An HTTP Cloud Run function (Functions Framework for .NET), with Zharp's one attribute.
using Google.Cloud.Functions.Framework;
using Google.Cloud.Functions.Hosting;
using Microsoft.AspNetCore.Http;

[assembly: FunctionsStartup(typeof(Zharp.GoogleCloudFunctions.ZharpStartup))]

namespace GcfApp;

public class Checkout : IHttpFunction
{
    public async Task HandleAsync(HttpContext context)
    {
        string? quantity = context.Request.Query["quantity"];
        if (quantity == "0") throw new InvalidOperationException("Cart is empty");
        await context.Response.WriteAsync("ok");
    }
}
