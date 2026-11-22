using System;
using System.Threading.Tasks;

namespace Shop.Checkout
{
    public static class Cart
    {
        public static int Total(int[] prices)
        {
            int total = 0;
            foreach (int price in prices) total += price;
            if (total == 0) throw new InvalidOperationException("Cart is empty");
            return total;
        }

        public static void Pay(int[] prices)
        {
            int total = Total(prices);
            Console.WriteLine(total);
        }

        public static async Task PayLaterAsync(int[] prices)
        {
            await Task.Yield();
            Pay(prices);
        }
    }
}
