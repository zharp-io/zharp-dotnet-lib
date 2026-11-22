// A Lambda handler as written for the managed dotnet10 runtime, wrapped by Zharp.
using Amazon.Lambda.Core;
using Zharp.AwsLambda;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LambdaApp;

public class Order
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}

public class Function
{
    static readonly Func<Order, ILambdaContext, Task<string>> Handle = ZharpLambda.Wrap<Order, string>(PlaceAsync);

    public Task<string> Handler(Order order, ILambdaContext context) => Handle(order, context);

    static async Task<string> PlaceAsync(Order order, ILambdaContext context)
    {
        context.Logger.LogInformation($"Placing {order.Quantity} x {order.Sku}");
        await Task.Delay(5);
        if (order.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(order), "Quantity must be positive");
        return $"placed {order.Sku}";
    }
}
