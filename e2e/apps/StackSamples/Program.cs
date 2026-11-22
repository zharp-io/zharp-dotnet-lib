using System.Globalization;
using System.Text.Json;

CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
var samples = new Dictionary<string, string>();
void Capture(string name, Action action)
{
    try { action(); } catch (Exception e) { samples[name] = e.ToString(); }
}
async Task CaptureAsync(string name, Func<Task> action)
{
    try { await action(); } catch (Exception e) { samples[name] = e.ToString(); }
}

Capture("lambda", () => Shop.Cart.Run(0));
Capture("local-function", () => Shop.Cart.WithLocal(0));
Capture("generic", () => new Shop.Repo<int>().Get(7));
await CaptureAsync("async", () => Shop.Orders.PlaceAsync(3));
await CaptureAsync("async-lambda", async () =>
{
    Func<int, Task> load = async id => { await Task.Yield(); throw new KeyNotFoundException($"Order {id} not found"); };
    await load(5);
});
Capture("wrapped", () =>
{
    try { Shop.Cart.Run(0); }
    catch (Exception e) { throw new ApplicationException("Checkout failed", e); }
});
Console.WriteLine(JsonSerializer.Serialize(samples));

namespace Shop
{
    public static class Cart
    {
        public static int Run(int quantity)
        {
            Func<int, int> price = n => n > 0 ? n * 100 : throw new InvalidOperationException("Cart is empty");
            return price(quantity);
        }

        public static int WithLocal(int quantity)
        {
            return Total(quantity);
            int Total(int q) => q > 0 ? q : throw new ArgumentOutOfRangeException(nameof(q), "Quantity must be positive");
        }
    }

    public class Repo<T>
    {
        public T Get(T id) => throw new KeyNotFoundException($"No row {id}");
    }

    public static class Orders
    {
        public static async Task PlaceAsync(int id)
        {
            await Task.Delay(1);
            await SaveAsync(id);
        }

        static async Task SaveAsync(int id)
        {
            await Task.Yield();
            throw new InvalidOperationException($"Order {id} could not be saved");
        }
    }
}
