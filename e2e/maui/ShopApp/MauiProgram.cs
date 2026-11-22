namespace Shop.Mobile;

public static class MauiProgram
{
    /// <summary>The stand-in Zharp on the runner: the Android emulator reaches the host at 10.0.2.2.</summary>
#if ANDROID
    const string Endpoint = "http://10.0.2.2:4318/otlp";
#else
    const string Endpoint = "http://127.0.0.1:4318/otlp";
#endif

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseZharp(o =>
            {
                o.Key = "zpk_maui_e2e";
                o.Endpoint = Endpoint;
                o.Environment = "e2e";
                o.Debug = true;
            }, app => app.HangThreshold = TimeSpan.FromSeconds(2));
        return builder.Build();
    }
}
