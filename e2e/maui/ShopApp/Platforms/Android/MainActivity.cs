using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Shop.Mobile;

[Activity(Name = "io.zharp.e2e.shop.MainActivity", Theme = "@style/Maui.MainTheme.NoActionBar", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        string? mode = Intent?.GetStringExtra("mode");
        if (!string.IsNullOrEmpty(mode)) Mode.Current = mode;
        base.OnCreate(savedInstanceState);
    }
}
