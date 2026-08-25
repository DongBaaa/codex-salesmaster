using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace GeoraePlan.Mobile.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize
                           | ConfigChanges.Orientation
                           | ConfigChanges.UiMode
                           | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize
                           | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Window is not null)
            WindowCompat.SetDecorFitsSystemWindows(Window, false);

        var contentRoot = FindViewById<Android.Views.View>(Android.Resource.Id.Content);
        if (contentRoot is not null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(contentRoot, new SafeWindowInsetsListener());
            ViewCompat.RequestApplyInsets(contentRoot);
        }
    }

    private sealed class SafeWindowInsetsListener : Java.Lang.Object,
        IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(
            Android.Views.View view,
            WindowInsetsCompat windowInsets)
        {
            var systemBars = windowInsets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            var ime = windowInsets.GetInsets(WindowInsetsCompat.Type.Ime());
            view.SetPadding(
                Math.Max(systemBars.Left, ime.Left),
                Math.Max(systemBars.Top, ime.Top),
                Math.Max(systemBars.Right, ime.Right),
                Math.Max(systemBars.Bottom, ime.Bottom));
            return windowInsets;
        }
    }

#if GEORAEPLAN_MOBILE_UI_MATRIX
    protected override void OnPostCreate(Bundle? savedInstanceState)
    {
        base.OnPostCreate(savedInstanceState);
        DispatchUiMatrixRequest(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        DispatchUiMatrixRequest(intent);
    }

    private static void DispatchUiMatrixRequest(Intent? intent)
    {
        var encoded = intent?.GetStringExtra(
            UiMatrix.MobileUiMatrixHostPage.RequestExtraName);
        if (string.IsNullOrWhiteSpace(encoded))
            return;

        UiMatrix.MobileUiMatrixHostPage.DispatchEncodedRequest(encoded);
    }
#endif
}
