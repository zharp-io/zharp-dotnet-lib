namespace Shop.Mobile;

/// <summary>
/// What the e2e run asks for, from the launch (Android: the "mode" extra; iOS, Mac Catalyst and
/// Windows: the first argument, or ZHARP_E2E_MODE): error, crash, hang or quit.
/// </summary>
public static class Mode
{
    public static string Current = FromArgs();

    static string FromArgs()
    {
        // iOS simulator: simctl passes SIMCTL_CHILD_ZHARP_E2E_MODE to the app as ZHARP_E2E_MODE.
        string? fromEnv = Environment.GetEnvironmentVariable("ZHARP_E2E_MODE");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        string[] args = Environment.GetCommandLineArgs();
        foreach (string mode in new[] { "error", "crash", "hang", "quit" })
        {
            if (args.Contains(mode)) return mode;
        }
        return "quit";
    }
}

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new Window(new NavigationPage(new MainPage())) { Title = "Shop" };
}

public class MainPage : ContentPage
{
    bool ran;

    public MainPage()
    {
        Title = "Shop";
        Content = new Label { Text = "Shop", Margin = 20 };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (ran) return;
        ran = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), () => Run(Mode.Current));
    }

    void Run(string mode)
    {
        Console.WriteLine("[e2e] mode " + mode);
        switch (mode)
        {
            case "error":
                try { Checkout.Pay(Array.Empty<int>()); }
                catch (Exception error) { Zharp.ZharpSdk.CaptureException(error); }
                break;
            case "crash":
                // On the UI thread, from the platform's own loop: nothing handles it.
                Dispatcher.Dispatch(() => Checkout.Pay(Array.Empty<int>()));
                break;
            case "hang":
                Thread.Sleep(5000);
                break;
        }
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(5), () => Zharp.ZharpSdk.Flush(TimeSpan.FromSeconds(5)));
    }
}
