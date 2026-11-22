using UIKit;

namespace Shop.Mobile;

public class Program
{
    static void Main(string[] args)
    {
        foreach (string mode in new[] { "error", "crash", "hang", "quit" })
        {
            if (args.Contains(mode)) Mode.Current = mode;
        }
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
