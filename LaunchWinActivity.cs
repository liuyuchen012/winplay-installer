using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Collections.Generic;
using Com.Xiaomi.Winplay.Bean;

namespace WinPlayInstaller
{
    /// <summary>
    /// 桌面快捷方式的路由入口：ShortcutInfo 的 PersistableBundle 不能保存自定义 Parcelable，
    /// 所以快捷方式只携带 exe/name 字符串，由本 Activity 立即转发给 PC 引擎的 WineActivity。
    /// 注意：NoDisplay 类 Activity 必须在 OnResume 完成前 Finish。
    /// </summary>
    [Activity(Name = "com.winplay.installer.LaunchWin", Exported = true,
        Theme = "@android:style/Theme.NoDisplay",
        ExcludeFromRecents = true)]
    public class LaunchWinActivity : Activity
    {
        protected override void OnResume()
        {
            base.OnResume();
            var exe = Intent?.GetStringExtra("exe");
            var name = Intent?.GetStringExtra("name");
            var cwd = Intent?.GetStringExtra("cwd");
            var lang = Intent?.GetStringExtra("lang");
            if (exe == "steam:")
            {
                // Steam 走引擎官方 deep link（最稳定的启动入口，自动引导组件/会话）
                var dl = new Intent(Intent.ActionView, Android.Net.Uri.Parse("winplay://opensteam?appid=0&devmode=1"));
                dl.AddFlags(ActivityFlags.NewTask);
                StartActivity(dl);
            }
            else if (!string.IsNullOrWhiteSpace(exe))
            {
                var intent = new Intent(Intent.ActionView);
                intent.SetComponent(new ComponentName("com.xiaomi.winplay", "com.xiaomi.winplay.WineActivity"));

                // 把工作目录与语言经 Env 字典传给引擎：WinAppInfo 没有独立的 cwd/lang 字段，
                // Env 是跨进程唯一可扩展的通道。老快捷方式没有这两个 extra，GetStringExtra
                // 会返回 null，此时 env 为空，引擎按默认行为启动，不会出错。
                var env = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    env["WINPLAY_WORKDIR"] = cwd;
                    env["PWD"] = cwd;
                }
                if (!string.IsNullOrWhiteSpace(lang)) env["LANG"] = lang;

                intent.PutExtra("app_info", new WinAppInfo(exe, env, false));
                intent.PutExtra("app_name", string.IsNullOrWhiteSpace(name) ? "WinPlay" : name);
                intent.AddFlags(ActivityFlags.NewTask);
                StartActivity(intent);
            }
            Finish();
        }
    }
}
