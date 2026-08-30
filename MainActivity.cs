using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Widget;
using Com.Xiaomi.Winplay.Bean;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IoPath = System.IO.Path;
using APath = Android.Graphics.Path;

namespace WinPlayInstaller
{
    [Activity(Label = "WinPlay安装器", MainLauncher = true, Exported = true,
        LaunchMode = LaunchMode.SingleTop,
        Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class MainActivity : Activity
    {
        const int PickFileRequest = 42;

        const string EngineFiles = "/data/user/0/com.xiaomi.winplay/files";
        const string PrefixDrive = EngineFiles + "/prefix/drive_c";
        const string EngineUid = "7100";

        TextView _log;
        EditText _name;
        EditText _mainExe;
        EditText _quietArgs;
        Button _pickBtn;
        Button _autoBtn;
        Button _wizardBtn;
        Button _zipBtn;
        Button _shortcutBtn;
        Button _launchBtn;
        Button _steamBtn;

        string _pickedSharePath;
        string _pickedName;
        bool _haveRoot;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetPadding(Dp(20), Dp(24), Dp(20), Dp(20));

            var title = new TextView(this) { Text = "WinPlay 安装器", TextSize = 24f };
            title.SetTextColor(Color.Rgb(30, 30, 30));
            root.AddView(title);

            var sub = new TextView(this) { Text = "选择安装包 → 自动安装（静默/图形向导/便携解压）→ 一键桌面快捷方式", TextSize = 13f };
            sub.SetTextColor(Color.Gray);
            root.AddView(sub);

            _pickBtn = new Button(this) { Text = "① 选择 安装包（exe / msi / zip）" };
            _pickBtn.Click += (s, e) => PickFile();
            root.AddView(_pickBtn);

            _name = new EditText(this) { Hint = "名称（快捷方式显示名）" };
            root.AddView(_name);

            _quietArgs = new EditText(this)
            {
                Hint = "静默参数（NSIS 默认 /S，不用改）",
                Text = "/S", TextSize = 13f
            };
            root.AddView(_quietArgs);

            _autoBtn = new Button(this) { Text = "② 自动安装 + 创建快捷方式（root，首选）", Enabled = false };
            _autoBtn.Click += (s, e) => RunAutoInstall();
            root.AddView(_autoBtn);

            _wizardBtn = new Button(this) { Text = "③ 引擎图形安装向导（静默失败时用）", Enabled = false };
            _wizardBtn.Click += (s, e) => LaunchWizard();
            root.AddView(_wizardBtn);

            _zipBtn = new Button(this) { Text = "④ zip 便携版解压到容器（免安装绿色软件）", Enabled = false };
            _zipBtn.Click += (s, e) => RunZipExtract();
            root.AddView(_zipBtn);

            _mainExe = new EditText(this)
            {
                Hint = "⑤ 主程序路径（自动识别；可改，如 C:\\VSCode\\Code.exe）",
                TextSize = 13f
            };
            root.AddView(_mainExe);

            _shortcutBtn = new Button(this) { Text = "⑥ 创建桌面快捷方式", Enabled = false };
            _shortcutBtn.Click += (s, e) => CreateShortcutAsync(_name.Text?.Trim(), GetMainPath());
            root.AddView(_shortcutBtn);

            _launchBtn = new Button(this) { Text = "⑦ 直接启动", Enabled = false };
            _launchBtn.Click += (s, e) => Launch(GetMainPath(), _name.Text?.Trim());
            root.AddView(_launchBtn);

            _steamBtn = new Button(this) { Text = "⑧ 一键添加 Steam 桌面快捷方式" };
            _steamBtn.Click += (s, e) => AddSteamShortcut();
            root.AddView(_steamBtn);

            _mainExe.TextChanged += (s, e) =>
            {
                bool ok = !string.IsNullOrWhiteSpace(_mainExe.Text);
                _shortcutBtn.Enabled = ok;
                _launchBtn.Enabled = ok;
            };

            _log = new TextView(this) { TextSize = 12f, Text = "就绪。\n" };
            _log.SetTextColor(Color.Rgb(20, 80, 20));
            root.AddView(_log);

            var scroll = new ScrollView(this) { LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f) };
            scroll.AddView(root);
            SetContentView(scroll);

            CheckRoot();
        }

        async void CheckRoot()
        {
            try
            {
                var r = await RootShell.ExecAsync("id -u");
                _haveRoot = r.Output.Trim() == "0";
                Log(_haveRoot
                    ? "✓ root 就绪（SukiSU 已授权）。"
                    : "✕ 未获得 root：打开 SukiSU Ultra → 超级用户 → 添加「WinPlay安装器」为允许后返回本页。自动静默安装需要 root；①③④②（图形向导/便携解压/快捷方式）无需 root。");
            }
            catch (Exception ex) { Log("root 检测失败：" + ex.Message); }
        }

        void PickFile()
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/x-msdownload", "application/vnd.microsoft.portable-executable", "application/octet-stream", "application/zip", "application/x-zip-compressed" });
            StartActivityForResult(intent, PickFileRequest);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != PickFileRequest || resultCode != Result.Ok || data?.Data == null) return;

            var uri = data.Data;
            var dn = QueryDisplayName(uri) ?? "setup.bin";
            try
            {
                var values = new ContentValues();
                values.Put(MediaStore.Downloads.InterfaceConsts.DisplayName, dn);
                values.Put(MediaStore.Downloads.InterfaceConsts.MimeType, dn.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip" : "application/octet-stream");
                values.Put(MediaStore.Downloads.InterfaceConsts.RelativePath, "Download/");
                var fileUri = ContentResolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
                if (fileUri == null) throw new Exception("MediaStore 写入被拒绝");
                using (var src = ContentResolver.OpenInputStream(uri))
                using (var dst = ContentResolver.OpenOutputStream(fileUri))
                    src.CopyTo(dst);

                _pickedSharePath = "/sdcard/Download/" + dn;
                _pickedName = Regex.Replace(dn, @"\.(exe|msi|zip)$", "", RegexOptions.IgnoreCase);
                Log($"已选择：{dn} → {_pickedSharePath}");
                if (string.IsNullOrWhiteSpace(_name.Text))
                    _name.Text = _pickedName;
                _autoBtn.Enabled = true;
                _wizardBtn.Enabled = true;
                _zipBtn.Enabled = dn.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log("保存文件失败：" + ex.Message); }
        }

        string QueryDisplayName(Android.Net.Uri uri)
        {
            try
            {
                var cursor = ContentResolver.Query(uri, null, null, null, null);
                if (cursor != null)
                {
                    using (cursor)
                    {
                        var idx = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                        if (idx >= 0 && cursor.MoveToFirst()) return cursor.GetString(idx);
                    }
                }
            }
            catch { }
            return null;
        }

        async Task RunAutoInstall()
        {
            if (_pickedSharePath == null) return;
            var name = string.IsNullOrWhiteSpace(_name.Text) ? _pickedName : _name.Text.Trim();
            var quiet = string.IsNullOrWhiteSpace(_quietArgs.Text) ? "/S" : _quietArgs.Text.Trim();

            Log("→ 开始自动安装：" + name);
            if (!_haveRoot) { Log("✕ 自动安装需要 root（见上方说明），可改用③图形向导。"); return; }
            var ck = await RootShell.ExecAsync($"test -d {PrefixDrive} && echo OK || echo NO");
            if (!ck.Output.Contains("OK"))
            {
                var dbg = await RootShell.ExecAsync("echo PWD=$PWD; id; ls -ld /data/user/0/com.xiaomi.winplay/files/prefix; ls -ld /data/user/0/com.xiaomi.winplay; ls /data/data 2>&1 | head -3; mountpoint -q /data && echo DATA_IS_MN || echo DATA_NOT_MN");
                Log("✕ 引擎容器检查失败 out=[" + ck.Output.Trim() + "] 诊断=[" + (dbg.Output + dbg.Error).Trim() + "]");
                return;
            }

            var safe = Regex.Replace(name, @"[^a-zA-Z0-9_-]", "") + ".bin";
            var r = await RootShell.ExecScriptAsync(new[]
            {
                $"mkdir -p {PrefixDrive}/wpinst",
                $"cp '{_pickedSharePath}' {PrefixDrive}/wpinst/{safe}",
                $"chown -R {EngineUid}:{EngineUid} {PrefixDrive}/wpinst",
                "echo COPY_OK"
            });
            if (!r.Output.Contains("COPY_OK")) { Log("✕ 拷贝失败：" + r.Error); return; }

            var before = await ListExesAsync();
            var type = await DetectInstallerAsync(safe);
            Log("   安装器类型：" + type);

            bool ok = false; string detail = "";
            string dosSafe = "C:\\wpinst\\" + safe;
            if (type == "MSI")
            {
                Log("   msiexec /qn 静默安装…");
                var rr = await RunWineAsync($"msiexec /i {dosSafe} /qn /norestart");
                ok = rr.Output.Contains("__WINE_EXIT=0"); detail = rr.Output + rr.Error;
            }
            else if (type == "NSIS")
            {
                Log($"   NSIS 静默安装（{quiet}）…");
                var rr = await RunWineAsync($"{dosSafe} {quiet}");
                ok = rr.Output.Contains("__WINE_EXIT=0"); detail = rr.Output + rr.Error;
            }
            else if (type == "INNO")
            {
                Log("   Inno Setup：无头静默不可用（引擎无图形驱动限制），请用③图形向导安装。");
                LaunchWizard();
                return;
            }
            else
            {
                var rr = await RunWineAsync($"{dosSafe} {quiet}");
                ok = rr.Output.Contains("__WINE_EXIT=0"); detail = rr.Output + rr.Error;
            }

            var after = await ListExesAsync();
            var newExes = after.Except(before).Where(p => !Regex.IsMatch(p, @"unins|uninst|setup\.tmp|crash|report|updater", RegexOptions.IgnoreCase)).Distinct().ToList();

            if (newExes.Count == 0 && ok)
            {
                Log("   未在标准目录发现新 exe，检查安装器所在目录…");
                var rr0 = await RunWineAsync($"cmd /c dir /s /b C:\\\\wpinst\\\\*.exe");
                var inwp = ParseExeLines(rr0.Output).Where(p => !Regex.IsMatch(p, @"unins|uninst", RegexOptions.IgnoreCase)).ToList();
                if (inwp.Count > 0) newExes.AddRange(inwp.Select(ToDosPath));
            }
            if (!ok && newExes.Count == 0)
            {
                Log("✕ 静默安装未成功：" + TakeSafe(detail, 400) + "。请改用③图形向导安装。");
                return;
            }
            if (newExes.Count == 0)
            {
                Log("✕ 已装但未发现新 exe，请在⑤手动填写主程序路径后点⑥。");
                return;
            }

            var best = await PickMainBySizeAsync(newExes);
            var mainDos = best ?? newExes[0];
            // 引擎 runNewApp 不处理带空格的路径，自动转成无空格符号链接
            var linked = await EnsureNoSpacePathAsync(mainDos, name);
            _mainExe.Text = linked;
            Log("✔ 识别主程序：" + mainDos + (linked != mainDos ? "  →（无空格链接）" + linked : ""));

            await CreateShortcutAsync(name, _mainExe.Text);
            await RootShell.ExecAsync($"rm -f {PrefixDrive}/wpinst/{safe}");
        }

        async Task<string> DetectInstallerAsync(string safe)
        {
            try
            {
                var r = await RootShell.ExecAsync($"head -c 65536 '{PrefixDrive}/wpinst/{safe}' | tr -d '\\0'");
                var s = r.Output;
                if (s.Contains("NullsoftInst")) return "NSIS";
                if (s.Contains("InnoSetup")) return "INNO";
                return "OTHER";
            }
            catch { return "OTHER"; }
        }

        async Task<List<string>> ListExesAsync()
        {
            var r = await RootShell.ExecAsync(
                $"cd '{PrefixDrive}' && find . -path ./unix -prune -o -type f -iname '*.exe' -print 2>/dev/null | sort");
            return ParseExeLines(r.Output).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }

        static IEnumerable<string> ParseExeLines(string s) =>
            s.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim());

        async Task<string> PickMainBySizeAsync(List<string> dosList)
        {
            string best = null; long bestSize = -1;
            foreach (var d in dosList)
            {
                var unix = DosToUnix(d);
                var r = await RootShell.ExecAsync($"stat -c %s '{unix}'");
                if (long.TryParse(r.Output.Trim(), out var sz) && sz > bestSize) { bestSize = sz; best = d; }
            }
            return best;
        }

        async Task<string> EnsureNoSpacePathAsync(string dosPath, string name)
        {
            if (string.IsNullOrEmpty(dosPath) || !dosPath.Contains(" "))
            {
                // 无空格但已是标准路径，直接用
                return dosPath;
            }
            var safe = Regex.Replace(string.IsNullOrWhiteSpace(name) ? "app" : name, @"[^a-zA-Z0-9_-]", "");
            try
            {
                var dir = dosPath.Substring(0, Math.Max(dosPath.LastIndexOf('\\'), 0));
                var file = dosPath.Substring(dosPath.LastIndexOf('\\') + 1);
                var linkDir = "C:\\apps\\" + safe;
                var r = await RootShell.ExecAsync(
                    $"mkdir -p {PrefixDrive}/apps && rm -rf '{PrefixDrive}/apps/{safe}' && ln -sfn '{DosToUnix(dir)}' '{PrefixDrive}/apps/{safe}' && ls -la '{PrefixDrive}/apps/{safe}/{file}' && echo LN_OK");
                if (r.Output.Contains("LN_OK")) return linkDir + "\\" + file;
            }
            catch { }
            return dosPath;
        }

        async Task RunZipExtract()
        {
            if (_pickedSharePath == null || !_pickedSharePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { Log("请先选择一个 .zip 文件。"); return; }
            var name = string.IsNullOrWhiteSpace(_name.Text) ? _pickedName : _name.Text.Trim();
            Log("→ 解压便携版 zip 到容器…");
            if (!_haveRoot) { Log("✕ 需要 root。"); return; }

            var safe = Regex.Replace(name, @"[^a-zA-Z0-9_-]", "");
            await RootShell.ExecScriptAsync(new[]
            {
                $"mkdir -p {PrefixDrive}/wpinst",
                $"cp '{_pickedSharePath}' {PrefixDrive}/wpinst/app.zip",
                $"chown -R {EngineUid}:{EngineUid} {PrefixDrive}/wpinst"
            });
            var rr = await RunWineAsync($"C:\\Program Files\\7-Zip\\7z.exe x C:\\wpinst\\app.zip -oC:\\apps\\{safe} -y");
            if (!rr.Output.Contains("__WINE_EXIT=0"))
            {
                Log("✕ 7z 解压失败：" + TakeSafe(rr.Output, 300));
                Log("提示：容器内无 7-Zip 时，先用本工具「③图形向导」安装 7-Zip（官网 x64 安装包），即可解锁便携解压。");
                return;
            }
            var list = await RootShell.ExecAsync($"find '{PrefixDrive}/apps/{safe}' -maxdepth 3 -iname '*.exe' ! -iname 'unins*' 2>/dev/null | sort");
            var cand = ParseExeLines(list.Output).ToList();
            if (cand.Count == 0) { Log("解压完成但未发现 exe，程序位于 C:\\apps\\" + safe); return; }
            var dos = ToDosPath(cand[0]);
            if (cand.Count > 1)
            {
                var best = await PickMainBySizeAsync(cand.Select(ToDosPath).ToList());
                if (best != null) dos = best;
            }
            _mainExe.Text = dos;
            Log("✔ 解压完成，主程序：" + dos);
            await RootShell.ExecAsync($"rm -f {PrefixDrive}/wpinst/app.zip");
        }

        Task<RootShell.Result> RunWineAsync(string wineArgs)
        {
            var script = $@"#!/system/bin/sh
B={EngineFiles}/arm64-v8a
cd {EngineFiles} || exit 9
export WINEPREFIX={EngineFiles}/prefix
export PATH=$B/bin:$PATH
export LD_LIBRARY_PATH=$B/lib/wine/aarch64-unix:$B/lib:/product/app/WinPlay/lib/arm64
export WINEDLLPATH=$B/lib/wine/aarch64-windows:$B/lib/wine/i386-windows:$B/lib/wine
export TMPDIR=/data/user/0/com.xiaomi.winplay/cache
export WINPLAY_CONFIG_PATH={EngineFiles}/cloud/winplay.conf
export LANG=zh_CN.UTF-8
export WINEDEBUG=-all
$B/lib/wine/aarch64-unix/wine {wineArgs}
echo __WINE_EXIT=$?
";
            return RootShell.ExecScriptAsync(new[]
            {
                "cat > /data/local/tmp/wp_run.sh << 'WPEOF'\n" + script + "WPEOF",
                "chmod 644 /data/local/tmp/wp_run.sh",
                $"su {EngineUid} -c sh /data/local/tmp/wp_run.sh"
            });
        }

        void LaunchWizard()
        {
            if (_pickedSharePath == null) return;
            var dos = ToDosPath(_pickedSharePath);
            Log("→ 在 PC 引擎中启动安装向导：" + dos);
            Launch(dos, "安装向导 - " + (_name.Text?.Trim() ?? "WinPlay"));
        }

        string GetMainPath()
        {
            var p = _mainExe.Text?.Trim();
            if (string.IsNullOrWhiteSpace(p)) return null;
            if (p.StartsWith("C:/") || p.StartsWith("D:/")) return p.Replace('/', '\\');
            if (!p.StartsWith("C:\\") && p.Contains("/")) return ToDosPath(p);
            return p;
        }

        string DosToUnix(string dosPath)
        {
            if (dosPath.StartsWith("C:\\")) return PrefixDrive + "/" + dosPath.Substring(3).Replace('\\', '/');
            if (dosPath.StartsWith("D:\\")) return "/sdcard/" + dosPath.Substring(3).Replace('\\', '/');
            return dosPath;
        }

        static string ToDosPath(string unixPath)
        {
            if (unixPath.StartsWith("/sdcard/")) return "D:\\" + unixPath.Substring(8).Replace('/', '\\');
            return "C:\\" + unixPath.TrimStart('/').Replace('/', '\\');
        }

        async Task CreateShortcutAsync(string name, string dosPath)
        {
            if (string.IsNullOrWhiteSpace(dosPath)) { Log("请先填写主程序路径。"); return; }
            name = string.IsNullOrWhiteSpace(name) ? "WinPlay应用" : name;
            try
            {
                var sm = (ShortcutManager)GetSystemService(ShortcutService);
                if (!sm.IsRequestPinShortcutSupported) { Log("当前桌面不支持固定快捷方式。"); return; }
                var intent = new Intent(Intent.ActionView);
                intent.SetClassName(PackageName, "com.winplay.installer.LaunchWin");
                intent.PutExtra("exe", dosPath);
                intent.PutExtra("name", name);
                var id = "wpinst_" + DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var info = new ShortcutInfo.Builder(this, id)
                    .SetShortLabel(name)
                    .SetLongLabel(name + "（PC引擎）")
                    .SetIcon(Icon.CreateWithBitmap(MakeIcon(name)))
                    .SetIntent(intent)
                    .Build();
                sm.RequestPinShortcut(info, null);
                Log("→ 已请求固定快捷方式，请在系统弹窗中确认添加。");
                _mainExe.Text = dosPath;
            }
            catch (Exception ex) { Log("创建快捷方式失败：" + ex.Message); }
            await Task.CompletedTask;
        }

        async Task AddSteamShortcut()
        {
            // 走引擎官方 deep link 路由（零 root，最稳定的 Steam 启动入口）
            Log("→ 添加 Steam 桌面快捷方式（引擎官方入口启动）…");
            await CreateShortcutAsync("Steam", "steam:");
        }

        void Launch(string dosPath, string name)
        {
            if (string.IsNullOrEmpty(dosPath)) { Log("请先填写主程序路径。"); return; }
            try
            {
                var intent = new Intent(Intent.ActionView);
                intent.SetComponent(new ComponentName("com.xiaomi.winplay", "com.xiaomi.winplay.WineActivity"));
                intent.PutExtra("app_info", new WinAppInfo(dosPath, new Dictionary<string, string>(), false));
                intent.PutExtra("app_name", string.IsNullOrWhiteSpace(name) ? "WinPlay" : name);
                intent.AddFlags(ActivityFlags.NewTask);
                StartActivity(intent);
                Log("已请求 PC 引擎启动：" + dosPath);
            }
            catch (Exception ex) { Log("启动失败：" + ex.Message); }
        }

        Bitmap MakeIcon(string name)
        {
            int s = 192;
            var bmp = Bitmap.CreateBitmap(s, s, Bitmap.Config.Argb8888);
            using var c = new Canvas(bmp);
            var p = new Paint(PaintFlags.AntiAlias);
            var g = new LinearGradient(0, 0, s, s, Color.Rgb(69, 58, 224), Color.Rgb(14, 165, 233), Shader.TileMode.Clamp!);
            p.SetShader(g);
            c.DrawRoundRect(0, 0, s, s, s * 0.22f, s * 0.22f, p);
            p.SetShader(null!);
            p.Color = Color.White;
            c.DrawRoundRect(s * 0.18f, s * 0.24f, s * 0.82f, s * 0.72f, s * 0.06f, s * 0.06f, p);
            p.Color = Color.Rgb(69, 58, 224);
            for (int i = 0; i < 3; i++)
                c.DrawCircle(s * (0.26f + 0.067f * i), s * 0.305f, s * 0.020f, p);
            p.Color = Color.Rgb(228, 231, 240);
            c.DrawRoundRect(s * 0.25f, s * 0.39f, s * 0.75f, s * 0.435f, s * 0.02f, s * 0.02f, p);
            c.DrawRoundRect(s * 0.25f, s * 0.485f, s * 0.65f, s * 0.53f, s * 0.02f, s * 0.02f, p);
            c.DrawRoundRect(s * 0.25f, s * 0.58f, s * 0.70f, s * 0.625f, s * 0.02f, s * 0.02f, p);
            p.Color = Color.Rgb(255, 176, 32);
            c.DrawCircle(s * 0.735f, s * 0.705f, s * 0.153f, p);
            p.Color = Color.White;
            using var tri = new APath();
            tri.MoveTo(s * 0.692f, s * 0.625f);
            tri.LineTo(s * 0.692f, s * 0.785f);
            tri.LineTo(s * 0.818f, s * 0.705f);
            tri.Close();
            c.DrawPath(tri, p);
            return bmp;
        }

        void Log(string msg) => RunOnUiThread(() => _log.Append($"[{DateTime.Now:HH:mm:ss}] {msg}\n"));

        static string TakeSafe(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);
    }
}
