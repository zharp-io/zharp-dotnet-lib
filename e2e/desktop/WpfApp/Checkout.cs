using System;

namespace Shop.Desktop
{
    public static class Checkout
    {
        public static int Total(int[] prices)
        {
            int total = 0;
            foreach (int price in prices) total += price;
            if (total == 0) throw new InvalidOperationException("Desktop cart is empty");
            return total;
        }

        public static void Pay(int[] prices)
        {
            int total = Total(prices);
            Console.WriteLine(total);
        }
    }
}
