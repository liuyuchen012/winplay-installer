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
    // 全局结构化日志：记录到 App 内所有操作（UI 操作日志、root 通信、wine 调用、引擎交互）与异常
    public static class LogStore
    {
        static readonly System.Text.StringBuilder B = new System.Text.StringBuilder();
        static readonly object L = new object();
        public static void Add(string line)
        {
            lock (L)
            {
                B.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").Append(line).AppendLine();
                if (B.Length > 200000) { var s = B.ToString().Substring(50000); B.Clear(); B.Append(s); }
            }
        }
        public static string Full() { lock (L) { return B.ToString(); } }
    }

    [Activity(Label = "WinPlay安装器", MainLauncher = true, Exported = true,
        LaunchMode = LaunchMode.SingleTop,
        Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize,
        WindowSoftInputMode = Android.Views.SoftInput.AdjustResize)]
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
        Button _autoBtn;
        Button _wizardBtn;
        Button _zipBtn;
        Button _shortcutBtn;
        Button _launchBtn;
        Button _steamBtn;
        EditText _pathInput;
        Button _fixBtn;
        EditText _fbDesc;

        string _pickedSharePath;
        string _pickedName;
        bool _haveRoot;

        // ---- 程序清单 / 目录映射（数据层见 AppCatalog、RecentStore）----
        CheckBox _forceScriptChk;                        // 「强制脚本方式运行」勾选框，步骤4 在工具页创建
        List<AppEntry> _catalog = new List<AppEntry>();   // 最近一次加载的程序清单，供 ⑦ 按钮回退使用

        CheckBox _debugChk;                                  // 「详细日志」勾选框
        List<RecentEntry> _recents = new List<RecentEntry>(); // 最近使用列表（界面副本）

        LinearLayout _listCatalog;      // 清单列表容器
        LinearLayout _listRecent;       // 最近使用列表容器
        LinearLayout _mountList;        // 目录映射行容器
        TextView _mountHint;            // 映射状态提示
        TextView _catalogPathHint;      // 清单文件路径提示
        TextView _recentPathHint;       // 最近使用文件路径提示
        ScrollView _logScroll;          // 日志滚动容器（Log 自动滚底依赖）
        int _pendingMountLetter = -1;   // 正在添加映射的盘符字符（选目录回调时用）
        const int TreeRequest = 43;     // 目录树选择请求码

        string _driveC; // 引擎容器根目录（按机型探测，见 ResolveDriveAsync）
        string _winePath; // 引擎 wine 可执行路径（OS3/OS4 探测，见 GetWinePathAsync）
        string _prefix; // 引擎 WINEPREFIX（drive_c 上级）

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // 左右布局根容器：左＝操作区（页面+底部导航），右＝日志面板
            var root = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            root.SetPadding(Dp(16), Dp(12), Dp(16), Dp(8));

            // ============ 左列：标题 + 页面区 + 底部导航 ============
            var left = new LinearLayout(this) { Orientation = Orientation.Vertical, LayoutParameters = new LinearLayout.LayoutParams(0, -1, 2.4f) };
            var title = new TextView(this) { Text = "WinPlay 安装器", TextSize = 22f };
            title.SetTextColor(Color.Rgb(30, 30, 30));
            left.AddView(title);

            // ---- 页 1：安装 ----
            var pInstall = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var pickBtn = new Button(this) { Text = "① 选择 exe（直接从原位置运行）" };
            pickBtn.Click += (s, e) => PickFile();
            pInstall.AddView(pickBtn);

            _launchBtn = new Button(this) { Text = "② 运行 exe（工作目录 = exe 所在文件夹）" };
            _launchBtn.Click += async (s, e) =>
            {
                var p = GetMainPath();
                if (!string.IsNullOrWhiteSpace(p))
                {
                    // 有路径就直接按「便携版」方式运行：工作目录自动取 exe 所在文件夹，无需先解压/安装
                    await LaunchEntryAsync(new AppEntry { Name = _name.Text?.Trim(), Exe = p });
                    return;
                }

                // 输入框为空：回退到清单第一项 / 最近使用第一项，避免用户被逼回安装页粘路径
                var fallback = _catalog.Count > 0 ? _catalog[0] : null;
                if (fallback == null && RecentStore.Cached.Count > 0) fallback = RecentStore.Cached[0].ToAppEntry();
                if (fallback != null)
                {
                    Log("输入框为空，改用清单项：" + fallback.DisplayName);
                    await LaunchEntryAsync(fallback);
                }
                else Log("请填写主程序路径，或在「程序清单」页选择一项。");
            };
            pInstall.AddView(_launchBtn);

            _name = new EditText(this) { Hint = "名称（快捷方式显示名）" };
            pInstall.AddView(_name);

            _quietArgs = new EditText(this) { Hint = "静默参数（留空 = 按安装器类型自动决定）", Text = "", TextSize = 13f };
            pInstall.AddView(_quietArgs);
            // 不再需要静默参数：安装流程已隐藏，只保留直接启动
            _quietArgs.Visibility = Android.Views.ViewStates.Gone;

            _autoBtn = new Button(this) { Text = "② 自动安装 + 创建快捷方式（root，首选）", Enabled = false };
            _autoBtn.Click += (s, e) => _ = RunAutoInstall();
            pInstall.AddView(_autoBtn);
            _autoBtn.Visibility = Android.Views.ViewStates.Gone;   // 安装流程已隐藏，保留代码便于日后恢复

            _wizardBtn = new Button(this) { Text = "③ 引擎图形安装向导（静默失败时用）", Enabled = false };
            _wizardBtn.Click += (s, e) => LaunchWizard();
            pInstall.AddView(_wizardBtn);
            _wizardBtn.Visibility = Android.Views.ViewStates.Gone;

            _zipBtn = new Button(this) { Text = "④ zip 便携版解压到容器（免安装绿色软件）", Enabled = false };
            _zipBtn.Click += (s, e) => _ = RunZipExtract();
            pInstall.AddView(_zipBtn);
            _zipBtn.Visibility = Android.Views.ViewStates.Gone;

            _mainExe = new EditText(this) { Hint = "③ 主程序路径（选择后自动填入，也可手改）", TextSize = 13f };
            pInstall.AddView(_mainExe);

            _shortcutBtn = new Button(this) { Text = "④ 创建桌面快捷方式", Enabled = false };
            _shortcutBtn.Click += (s, e) => _ = CreateShortcutAsync(_name.Text?.Trim(), GetMainPath());
            pInstall.AddView(_shortcutBtn);


            _pathInput = new EditText(this) { Hint = "①-备 直接输入安装包路径（选择器不可用时，如 /sdcard/Download/app.exe）", TextSize = 13f };
            pInstall.AddView(_pathInput);
            _pathInput.Visibility = Android.Views.ViewStates.Gone;   // 手输安装包路径已不再需要

            var pathBtn = new Button(this) { Text = "用上面路径安装" };
            pathBtn.Click += (s, e) => _ = InstallByPath();
            pInstall.AddView(pathBtn);
            pathBtn.Visibility = Android.Views.ViewStates.Gone;

            // ---- 页 2：工具 ----
            var pTools = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var tipT = new TextView(this) { Text = "快捷方式一条龙与引擎问题修复", TextSize = 13f };
            tipT.SetTextColor(Color.Gray);
            pTools.AddView(tipT);

            _steamBtn = new Button(this) { Text = "⑧ 一键添加 Steam 桌面快捷方式" };
            _steamBtn.Click += (s, e) => _ = AddSteamShortcut();
            pTools.AddView(_steamBtn);

            _fixBtn = new Button(this) { Text = "⑨ 引擎启动修复（初始化失败/卡99%时点这里）" };
            _fixBtn.Click += (s, e) => _ = FixEngineStart();
            pTools.AddView(_fixBtn);

            var tipFix = new TextView(this) { Text = "⑨ 会先清理残留 wine 会话（防引擎冷启动 NPE），再触发引擎官方初始化入口。", TextSize = 12f };
            tipFix.SetTextColor(Color.Rgb(140, 90, 0));
            pTools.AddView(tipFix);

            // ---- 工具页底部：目录映射 + 诊断开关 ----
            // 这两个方法内部会创建 _forceScriptChk 与 _debugChk，是启动流程的前置依赖
            BuildMountsSection(pTools);
            BuildDebugSection(pTools);

            // ---- 页 3：日志与反馈 ----
            var pLog = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var exportBtn = new Button(this) { Text = "⑩ 导出详细日志（含 root 通信/wine 调用全过程）" };
            exportBtn.Click += (s, e) => _ = ExportLog();
            pLog.AddView(exportBtn);

            var fbTitle = new TextView(this) { Text = "── 问题反馈（自动创建 GitHub Issue）──", TextSize = 14f };
            fbTitle.SetTextColor(Color.Rgb(30, 80, 160));
            pLog.AddView(fbTitle);

            _fbDesc = new EditText(this) { Hint = "问题描述：机型/系统版本/操作步骤/现象…", TextSize = 13f };
            pLog.AddView(_fbDesc);

            var fbSend = new Button(this) { Text = "⑪ 生成反馈并打开 GitHub 提交页（你登录后点击提交即可）" };
            fbSend.Click += (s, e) => _ = OpenFeedbackSubmit();
            pLog.AddView(fbSend);

            var fbCopy = new Button(this) { Text = "复制反馈内容到剪贴板（无 Token 时用）" };
            fbCopy.Click += (s, e) => CopyFeedback();
            pLog.AddView(fbCopy);

            // ---- 页面容器（滚动，键盘弹出可滚）----
            var pages = new LinearLayout(this) { Orientation = Orientation.Vertical, LayoutParameters = new LinearLayout.LayoutParams(-1, -2) };
            // ---- 页 4：程序清单 / 页 5：最近使用 ----
            var pApps = new LinearLayout(this) { Orientation = Orientation.Vertical };
            BuildAppsPage(pApps);
            var pRecent = new LinearLayout(this) { Orientation = Orientation.Vertical };
            BuildRecentPage(pRecent);

            pages.AddView(pInstall); pages.AddView(pTools); pages.AddView(pApps); pages.AddView(pRecent); pages.AddView(pLog);
            pTools.Visibility = Android.Views.ViewStates.Gone;
            pApps.Visibility = Android.Views.ViewStates.Gone;
            pRecent.Visibility = Android.Views.ViewStates.Gone;
            pLog.Visibility = Android.Views.ViewStates.Gone;
            var contentScroll = new ScrollView(this) { LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f), FillViewport = true };
            // 关掉边缘拉伸光效与滚动条，让手指拖动更跟手；过度滚动会把一次惯性滑动提前截断
            contentScroll.OverScrollMode = Android.Views.OverScrollMode.Never;
            contentScroll.VerticalScrollBarEnabled = false;
            contentScroll.SmoothScrollingEnabled = true;
            contentScroll.AddView(pages);
            left.AddView(contentScroll);

            // ---- 底部导航栏（Bottom Navigation）----
            var navRow = new LinearLayout(this) { Orientation = Orientation.Horizontal, LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(88)) };
            // 5 个 tab 文案必须短：navRow 高度固定 Dp(88)，长文案会被截断
            var tabDef = new[] { new[] { "📦", "安装" }, new[] { "🧰", "工具" }, new[] { "🗂", "清单" }, new[] { "🕘", "最近" }, new[] { "📋", "日志" } };
            var tabViews = new (LinearLayout root, TextView label)[5];
            Color active = Color.Rgb(37, 99, 235), inactive = Color.Gray;
            for (int i = 0; i < 5; i++)
            {
                var item = new LinearLayout(this) { Orientation = Orientation.Vertical, LayoutParameters = new LinearLayout.LayoutParams(0, -1, 1f) };
                try { item.SetGravity(Android.Views.GravityFlags.Center); } catch { }
                var icon = new TextView(this) { Text = tabDef[i][0], TextSize = 22f, Gravity = Android.Views.GravityFlags.Center };
                var lbl = new TextView(this) { Text = tabDef[i][1], TextSize = 12f, Gravity = Android.Views.GravityFlags.Center };
                item.AddView(icon);
                item.AddView(lbl);
                navRow.AddView(item);
                tabViews[i] = (item, lbl);
            }
            void SelectTab(int idx)
            {
                pInstall.Visibility = idx == 0 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
                pTools.Visibility   = idx == 1 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
                pApps.Visibility    = idx == 2 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
                pRecent.Visibility  = idx == 3 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
                pLog.Visibility     = idx == 4 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
                for (int i = 0; i < 5; i++)
                    tabViews[i].label.SetTextColor(i == idx ? active : inactive);

                // 五个页面共用一个 ScrollView，不重置会停在上一页的滚动位置、看起来像内容丢失
                contentScroll.ScrollTo(0, 0);

                // 切到清单/最近页时重新读盘，避免展示过期数据
                if (idx == 2) _ = RefreshCatalogAsync();
                if (idx == 3) _ = RefreshRecentAsync();
            }
            for (int i = 0; i < 5; i++)
            {
                var idx = i;
                tabViews[i].root.Clickable = true;
                tabViews[i].root.Click += (s, e) => SelectTab(idx);
            }
            SelectTab(0);
            left.AddView(navRow);

            // ============ 右列：日志面板（常驻）============
            var right = new LinearLayout(this) { Orientation = Orientation.Vertical, LayoutParameters = new LinearLayout.LayoutParams(0, -1, 1f) };
            right.SetPadding(Dp(12), Dp(4), Dp(4), Dp(4));
            var logTitle = new TextView(this) { Text = "── 操作日志 ──", TextSize = 13f };
            logTitle.SetTextColor(Color.Rgb(30, 80, 160));
            right.AddView(logTitle);
            _log = new TextView(this) { TextSize = 11f, Text = "就绪。\n" };
            _log.SetTextColor(Color.Rgb(20, 80, 20));
            var logScroll = new ScrollView(this) { LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f) };
            // 日志面板同样关掉过度滚动效果：自动滚底与手动滑行不会互相打断
            logScroll.OverScrollMode = Android.Views.OverScrollMode.Never;
            logScroll.VerticalScrollBarEnabled = false;
            logScroll.SmoothScrollingEnabled = true;
            logScroll.AddView(_log);
            right.AddView(logScroll);
            _logScroll = logScroll;   // 提升为字段，供 Log 自动滚底使用

            root.AddView(left);
            root.AddView(right);
            SetContentView(root);

            _mainExe.TextChanged += (s, e) =>
            {
                bool ok = !string.IsNullOrWhiteSpace(_mainExe.Text);
                _shortcutBtn.Enabled = ok;   // ④ 建快捷方式必须有路径
                // ② 不再跟着禁用：它有空路径回退（清单第一项/最近使用第一项），
                // 禁用会让那条分支永远不可达
            };

            RootShell.OnLog = m => LogStore.Add(m);

            // 数据层日志统一进 LogStore；debug 开启时同时显示在面板，避免正常使用时刷屏
            AppCatalog.Notify = m => { LogStore.Add(m); if (AppCatalog.Debug) Log(m); };
            RecentStore.Notify = m => LogStore.Add(m);
            FileAccess.Notify = m => LogStore.Add(m);
            RecentStore.Changed += () => RunOnUiThread(() => _ = RefreshRecentAsync());

            CheckRoot();

            // 首屏就把清单与最近使用加载好，切 tab 时无需再等
            _ = RefreshCatalogAsync();
            _ = RefreshRecentAsync();
        }

        protected override void OnResume()
        {
            base.OnResume();
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
                    : "✕ 未获得 root（su 返回 out=[" + r.Output.Trim() + "] err=[" + r.Error.Trim() + "]，exit=" + r.ExitCode + "）。自动静默安装需要 root；①③④② 无需 root。");
            }
            catch (Exception ex) { Log("root 检测失败：" + ex.Message); }
        }

        void PickFile()
        {
            // 注意：不加 ExtraMimeTypes。部分机型（如平板 6sPro）的文件管理器会按该列表过滤，
            // 导致 exe 文件不可见（Issue #1）。*/* 全量显示最稳。
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            StartActivityForResult(intent, PickFileRequest);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            // 目录树选择（添加目录映射用）与选文件是两条独立流程，先分流避免互相误判
            if (requestCode == TreeRequest)
            {
                if (resultCode == Result.Ok && data?.Data != null) OnTreePicked(data.Data);
                return;
            }

            if (requestCode != PickFileRequest || resultCode != Result.Ok || data?.Data == null) return;

            var uri = data.Data;
            var dn = QueryDisplayName(uri) ?? "setup.bin";

            // 优先还原真实路径：能还原就直接用原位置运行，不复制、不占额外空间。
            // 此处不做文件系统校验——FileAccess.Exists 会同步走 su 冻结界面，
            // System.IO.File.Exists 又受 MANAGE_EXTERNAL_STORAGE 授权影响会误判。
            // 路径真实性交给 LaunchEntryAsync 启动时校验。
            var realPath = DocumentUriToPath(uri);
            if (dn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(realPath))
            {
                _pickedSharePath = realPath;
                _pickedName = Regex.Replace(dn, @"\.(exe|msi|zip)$", "", RegexOptions.IgnoreCase);
                if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = _pickedName;
                _mainExe.Text = ToDosPath(realPath);
                Log($"已选择：{dn} → 将直接从 {realPath} 运行");
                return;
            }

            // 还原失败（网盘/云文档等 provider）才回退到复制到 Download
            Log("⚠ 该来源无法直接读取原路径，已复制到 Download（云盘/网盘常见）");
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

                // 便携版 exe 不需要安装：直接填入⑤，让 ⑥⑦ 立即可用，避免被 ② 的 /S 当成安装器
                if (dn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    _mainExe.Text = ToDosPath(_pickedSharePath);
                    Log("检测到 exe：已填入主程序路径，可直接点 ⑦ 启动（无需安装）");
                }
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
                        // OpenableColumns 在较新绑定里被标记过时，改用等价平台字符串常量（值与原属性完全一致）
                        var idx = cursor.GetColumnIndex("_display_name");
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
            // 不再回落 /S：参数该由安装器类型决定，用户没填就不预设
            var quiet = (_quietArgs.Text ?? "").Trim();

            Log("→ 开始自动安装：" + name);
            if (!_haveRoot) { Log("✕ 自动安装需要 root（见上方说明），可改用③图形向导。"); return; }
            _driveC = await ResolveDriveAsync();
            if (_driveC == null)
            {
                // 容器不存在 = 引擎从未初始化（刚重置/新机型常见）：自动唤起引擎官方初始化并轮询等待
                Log("⚠ PC 引擎未初始化：正在自动唤起初始化（首次需下载组件，约 3-5 分钟）…");
                try
                {
                    var warm = new Intent(Intent.ActionView, Android.Net.Uri.Parse("winplay://opensteam?appid=0&devmode=1"));
                    warm.AddFlags(ActivityFlags.NewTask);
                    StartActivity(warm);
                }
                catch { }
                Log("请在引擎界面完成「同意/下载组件」后回到本页（仅首次需要）。");
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(10000);
                    var rd = await ResolveDriveAsync();
                    if (rd != null) { Log("✔ 引擎容器已就绪，继续安装…"); break; }
                }
                if (_driveC == null) { Log("✕ 初始化等待超时：请先到桌面打开任意 PC 应用（如 WPS Office PC）完成初始化，再重试②。"); return; }
            }
            var ck = await RootShell.ExecAsync($"test -d {_driveC} && echo OK || echo NO");
            if (!ck.Output.Contains("OK"))
            {
                var dbg = await RootShell.ExecAsync("echo PWD=$PWD; id; ls -ld /data/user/0/com.xiaomi.winplay/files/prefix; ls -ld /data/user/0/com.xiaomi.winplay; ls /data/data 2>&1 | head -3; mountpoint -q /data && echo DATA_IS_MN || echo DATA_NOT_MN");
                Log("✕ 引擎容器检查失败 out=[" + ck.Output.Trim() + "] 诊断=[" + (dbg.Output + dbg.Error).Trim() + "]");
                return;
            }

            var safe = Regex.Replace(name, @"[^a-zA-Z0-9_-]", "") + ".bin";
            var r = await RootShell.ExecScriptAsync(new[]
            {
                $"mkdir -p {(_driveC ?? PrefixDrive)}/wpinst",
                $"cp '{_pickedSharePath}' {(_driveC ?? PrefixDrive)}/wpinst/{safe}",
                $"chown -R {EngineUid}:{EngineUid} {(_driveC ?? PrefixDrive)}/wpinst",
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
                // NSIS 的标准静默参数就是 /S：用户没填时由类型补上，而不是界面预设
                var nsisArgs = string.IsNullOrEmpty(quiet) ? "/S" : quiet;
                Log($"   NSIS 静默安装（{nsisArgs}）…");
                var rr = await RunWineAsync($"{dosSafe} {nsisArgs}");
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
                // 类型未识别：不预设 /S（Inno/MSI 之外的安装器大多不认它，强加会导致装不上）
                var rawArgs = string.IsNullOrEmpty(quiet) ? dosSafe : dosSafe + " " + quiet;
                Log(string.IsNullOrEmpty(quiet) ? "   未识别安装器类型，直接运行（未附加参数）…" : $"   未识别类型，附加参数（{quiet}）…");
                var rr = await RunWineAsync(rawArgs);
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
            await RootShell.ExecAsync($"rm -f {(_driveC ?? PrefixDrive)}/wpinst/{safe}");
        }

        async Task<string> DetectInstallerAsync(string safe)
        {
            try
            {
                var r = await RootShell.ExecAsync($"head -c 65536 '{(_driveC ?? PrefixDrive)}/wpinst/{safe}' | tr -d '\\0'");
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
                $"cd '{(_driveC ?? PrefixDrive)}' && find . -path ./unix -prune -o -type f -iname '*.exe' -print 2>/dev/null | sort");
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
                    $"mkdir -p {(_driveC ?? PrefixDrive)}/apps && rm -rf '{(_driveC ?? PrefixDrive)}/apps/{safe}' && ln -sfn '{DosToUnix(dir)}' '{(_driveC ?? PrefixDrive)}/apps/{safe}' && ls -la '{(_driveC ?? PrefixDrive)}/apps/{safe}/{file}' && echo LN_OK");
                if (r.Output.Contains("LN_OK")) return linkDir + "\\" + file;
            }
            catch { }
            return dosPath;
        }

        // 探测引擎容器根目录：兼容不同机型/系统版本的引擎目录布局（Issue #2）
        async Task<string> ResolveDriveAsync()
        {
            if (_driveC != null) return _driveC;
            if (!_haveRoot) return PrefixDrive;
            var r = await RootShell.ExecAsync($"find /data/user/0/com.xiaomi.winplay -maxdepth 3 -type d -name drive_c 2>/dev/null | head -1");
            var found = r.Output.Trim();
            if (found.Length <= 5)
            {
                // find 在部分设备的 su 环境不可用/受限：直接回退标准布局（OS3/OS4 引擎同构）
                LogStore.Add("[engine] find 探测失败 out=[" + r.Output.Trim() + "] err=[" + r.Error.Trim() + "]，回退标准容器路径");
                _driveC = PrefixDrive;
                _prefix = EngineFiles + "/prefix";
                return _driveC;
            }
            if (found.Length > 5)
            {
                _driveC = found;
                _prefix = System.IO.Path.GetDirectoryName(found);
                // 顺便探测 wine 可执行（OS3/OS4/不同引擎版本的 wine 布局可能不同）
                var w = await RootShell.ExecAsync($"find {EngineFiles} -maxdepth 6 -type f -name wine -path '*aarch64-unix*' 2>/dev/null | head -1");
                if (!string.IsNullOrWhiteSpace(w.Output)) _winePath = w.Output.Trim();
                var eng = await RootShell.ExecAsync("dumpsys package com.xiaomi.winplay | grep -m1 versionName | tr -d ' '");
                var sys = await RootShell.ExecAsync("getprop ro.mi.os.version.name; getprop ro.build.version.release");
                LogStore.Add("[engine] 引擎版本: " + (eng.Output.Trim().Length > 0 ? eng.Output.Trim() : "unknown") + " | 系统: " + sys.Output.Replace("\n", " ").Trim());
                LogStore.Add("[engine] wine路径: " + (_winePath ?? "探测失败(无root?)"));
                return _driveC;
            }
            return null;
        }

        // 一键修复引擎启动：清残留 wine 会话（冷启动 NPE 元凶）并触发官方入口预热（Issue #2）
        async Task FixEngineStart()
        {
            Log("→ 修复引擎启动：清理残留 wine 进程…");
            if (_haveRoot)
            {
                await RootShell.ExecAsync("for p in $(ps -A -o PID,ARGS | grep -iE 'wineserver|wine C' | awk '{print $1}'); do kill -9 $p 2>/dev/null; done; echo done");
                Log("   残留 wine 已清理。");
            }
            else Log("   无 root：跳过清理（冷启动卡 99% 时可先手动关掉引擎再试）。");
            try
            {
                var warm = new Intent(Intent.ActionView, Android.Net.Uri.Parse("winplay://opensteam?appid=0&devmode=1"));
                warm.AddFlags(ActivityFlags.NewTask);
                StartActivity(warm);
                Log("→ 已触发引擎官方初始化入口（winplay://opensteam），等待窗口就绪后可再次点击启动。");
            }
            catch (Exception ex) { Log("触发初始化失败：" + ex.Message); }
        }

        // 手动路径直装（绕过文件选择器机型限制，Issue #1）
        async Task InstallByPath()
        {
            var p = _pathInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(p)) { Log("请输入安装包路径，如 /sdcard/Download/app.exe"); return; }
            Log("→ 使用手动路径：" + p);
            var r = await RootShell.ExecAsync($"test -f '{p}' && echo YES || echo NO");
            if (!r.Output.Contains("YES"))
            {
                // 允许 Windows 风格路径（D:\...）与相对 Download 名
                if (p.StartsWith("/")) { Log("✕ 文件不存在：" + p); return; }
                p = "/sdcard/Download/" + p.TrimStart('/');
                r = await RootShell.ExecAsync($"test -f '{p}' && echo YES || echo NO");
                if (!r.Output.Contains("YES")) { Log("✕ 文件不存在：" + p); return; }
            }
            _pickedSharePath = p;
            _pickedName = Regex.Replace(System.IO.Path.GetFileName(p), @"\.(exe|msi|zip)$", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = _pickedName;
            _autoBtn.Enabled = true;
            _wizardBtn.Enabled = true;
            _zipBtn.Enabled = p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            Log("✔ 已就绪：" + p);
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
                $"mkdir -p {(_driveC ?? PrefixDrive)}/wpinst",
                $"cp '{_pickedSharePath}' {(_driveC ?? PrefixDrive)}/wpinst/app.zip",
                $"chown -R {EngineUid}:{EngineUid} {(_driveC ?? PrefixDrive)}/wpinst"
            });
            var rr = await RunWineAsync($"C:\\Program Files\\7-Zip\\7z.exe x C:\\wpinst\\app.zip -oC:\\apps\\{safe} -y");
            if (!rr.Output.Contains("__WINE_EXIT=0"))
            {
                Log("✕ 7z 解压失败：" + TakeSafe(rr.Output, 300));
                Log("提示：容器内无 7-Zip 时，先用本工具「③图形向导」安装 7-Zip（官网 x64 安装包），即可解锁便携解压。");
                return;
            }
            var list = await RootShell.ExecAsync($"find '{(_driveC ?? PrefixDrive)}/apps/{safe}' -maxdepth 3 -iname '*.exe' ! -iname 'unins*' 2>/dev/null | sort");
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
            await RootShell.ExecAsync($"rm -f {(_driveC ?? PrefixDrive)}/wpinst/app.zip");
        }

        Task<RootShell.Result> RunWineAsync(string wineArgs)
        {
            var script = $@"#!/system/bin/sh
B={EngineFiles}/arm64-v8a
WINE={_winePath ?? EngineFiles + "/arm64-v8a/lib/wine/aarch64-unix/wine"}
cd {EngineFiles} || exit 9
export WINEPREFIX={_prefix ?? EngineFiles + "/prefix"}
export PATH=$B/bin:$PATH
export LD_LIBRARY_PATH=$B/lib/wine/aarch64-unix:$B/lib:/product/app/WinPlay/lib/arm64
export WINEDLLPATH=$B/lib/wine/aarch64-windows:$B/lib/wine/i386-windows:$B/lib/wine
export TMPDIR=/data/user/0/com.xiaomi.winplay/cache
export WINPLAY_CONFIG_PATH={EngineFiles}/cloud/winplay.conf
export LANG=zh_CN.UTF-8
export WINEDEBUG=-all
chown -R 7100:7100 {EngineFiles}/prefix 2>/dev/null
$WINE {wineArgs}
RC=$?
chown -R 7100:7100 {EngineFiles}/prefix 2>/dev/null
echo __WINE_EXIT=$RC
";
            return RootShell.ExecScriptAsync(new[]
            {
                "cat > /data/local/tmp/wp_run.sh << 'WPEOF'\n" + script + "WPEOF",
                "chmod 644 /data/local/tmp/wp_run.sh",
                "sh /data/local/tmp/wp_run.sh"
            });
        }

        /// <summary>仅在清单开启 debug 时输出诊断日志，避免正常使用时日志被大量细节淹没。</summary>
        /// <param name="msg">要记录的内容。</param>
        void DLog(string msg)
        {
            if (AppCatalog.Debug) Log(msg);
        }

        /// <summary>把用户输入的路径归一化：统一分隔符，并把 Android 路径转成 Windows 路径。</summary>
        /// <param name="path">用户输入或清单里写的路径。</param>
        /// <returns>归一化后的 Windows 风格路径；输入为空时返回 null。</returns>
        string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim();

            // 已经是 C:/ D:/ E:/ 这类盘符写法，只把斜杠换成反斜杠
            if (p.Length > 1 && p[1] == ':' && p.Contains('/')) return p.Replace('/', '\\');
            if (p.Length > 1 && p[1] == ':') return p;

            // /sdcard/... 这类 Android 路径转回 D:\...
            if (p.StartsWith("/")) return ToDosPath(p);
            return p;
        }

        /// <summary>取路径的父目录，兼容 Windows 反斜杠与 Android 斜杠。</summary>
        /// <param name="path">文件路径。</param>
        /// <returns>父目录；无法解析时返回空字符串。</returns>
        string GetExeDir(string path) => AppEntry.ExeDirOf(path);

        /// <summary>按清单配置的目录映射把 Windows 路径重写为 Android 路径。</summary>
        /// <param name="dosPath">Windows 风格路径，如 E:\Games\x\x.exe。</param>
        /// <returns>命中映射时返回 Android 路径；未命中返回 null，由调用方走原有硬编码规则。</returns>
        string ApplyMountsToUnix(string dosPath)
        {
            if (string.IsNullOrWhiteSpace(dosPath)) return null;

            foreach (var m in AppCatalog.Mounts)
            {
                var win = m.WinPrefix();
                if (string.IsNullOrEmpty(win)) continue;
                if (!dosPath.StartsWith(win, StringComparison.OrdinalIgnoreCase)) continue;

                var rest = dosPath.Substring(win.Length).Replace('\\', '/').TrimStart('/');
                var baseDir = (m.AndroidPath ?? "").TrimEnd('/');
                var result = rest.Length == 0 ? baseDir : baseDir + "/" + rest;

                DLog("[mounts] 路径重写 " + dosPath + " → " + result);
                return result;
            }
            return null;
        }

        /// <summary>
        /// 盘符软链在真机上不可靠：引擎每次重建容器都会冲掉，且依赖 _prefix 探测与 root 授权。
        /// 因此不再创建 dosdevices 链接，只保留 <see cref="ApplyMountsToUnix"/> 的路径重写。
        /// </summary>
        /// <returns>已完成的空任务，保持调用方 <c>await</c> 写法不变。</returns>
        Task ApplyMountsAsync()
        {
            if (AppCatalog.Mounts.Count > 0)
                DLog("[mounts] 已跳过盘符软链，仅使用路径重写（" + AppCatalog.Mounts.Count + " 条映射）");
            return Task.CompletedTask;
        }

        /// <summary>解析实际生效的语言：项 lang → defaultLang → zh_CN.UTF-8。</summary>
        /// <param name="entry">清单项，可为 null。</param>
        /// <returns>可写入 LANG 环境变量的语言字符串。</returns>
        string ResolveLang(AppEntry entry)
        {
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Lang)) return entry.Lang.Trim();
            if (!string.IsNullOrWhiteSpace(AppCatalog.DefaultLang)) return AppCatalog.DefaultLang.Trim();
            return "zh_CN.UTF-8";
        }

        /// <summary>
        /// 按四级回退解析清单项图标：icon 字段 → exe 同目录图片 → 同目录 exe 自动提取 → 内置渐变图+名字首字。
        /// 注意：涉及 root 读文件，必须在后台线程调用，否则会卡住界面。
        /// </summary>
        /// <param name="entry">清单项，可为 null。</param>
        /// <returns>图标位图，永不返回 null。</returns>
        Bitmap ResolveIcon(AppEntry entry)
        {
            if (entry != null)
            {
                // 1) 手工指定：可填 png/jpg，也可填某个带图标的 exe/dll
                if (!string.IsNullOrWhiteSpace(entry.Icon))
                {
                    var raw = entry.Icon.Trim();
                    var unix = raw.StartsWith("/") ? raw : DosToUnix(raw);
                    var ext = (IoPath.GetExtension(unix ?? "") ?? "").ToLowerInvariant();
                    var manual = (ext == ".exe" || ext == ".dll")
                        ? IconExtractor.ExtractPngIcon(unix)
                        : IconExtractor.LoadImageFile(unix);
                    if (manual != null)
                    {
                        DLog("[icon] 图标来源：icon 字段 " + raw);
                        return manual;
                    }
                }

                var exeUnix = entry.Exe.StartsWith("/") ? entry.Exe : DosToUnix(entry.Exe);

                // 2) 磁盘/内存缓存（同批 exe 只解析一次 PE）
                var cached = IconCache.TryGet(entry.Exe);
                if (cached != null)
                {
                    DLog("[icon] 图标来源：缓存");
                    return cached;
                }

                // 3) exe 同目录的常见图片名
                var dirImage = IconExtractor.FindDirImage(exeUnix);
                if (dirImage != null)
                {
                    DLog("[icon] 图标来源：目录图片");
                    return dirImage;
                }

                // 4) 同目录其它 exe 自动提取
                var found = IconExtractor.ScanSiblings(exeUnix, AppCatalog.IconScanLimit);
                if (found != null)
                {
                    IconCache.Put(entry.Exe, found);
                    DLog("[icon] 图标来源：同目录 exe 提取");
                    return found;
                }

                DLog("[icon] 全部回退失败，使用渐变图+首字");
                return MakeIcon(entry.DisplayName);
            }
            return MakeIcon("WinPlay");
        }

        /// <summary>通过引擎 WineActivity 的 intent 启动程序，工作目录经 WinAppInfo.Env 传给引擎。</summary>
        /// <param name="exePath">Windows 风格 exe 路径。</param>
        /// <param name="name">显示名。</param>
        /// <param name="args">启动参数，可为空。</param>
        /// <param name="cwdWin">工作目录（Windows 风格）。</param>
        /// <param name="lang">语言，如 ja_JP.UTF-8。</param>
        /// <returns>intent 发送成功返回 true；引擎不存在或抛异常时返回 false。</returns>
        bool LaunchViaIntent(string exePath, string name, string args, string cwdWin, string lang)
        {
            try
            {
                var env = new Dictionary<string, string>();

                // 以下键名是从引擎接口反推的推测值：实测若引擎不采纳，请勾选「强制脚本方式运行」，
                // 脚本方式直接 cd 到工作目录，工作目录必定生效。
                if (!string.IsNullOrWhiteSpace(cwdWin))
                {
                    env["WINPLAY_WORKDIR"] = cwdWin;
                    env["PWD"] = cwdWin;
                }
                if (!string.IsNullOrWhiteSpace(lang)) env["LANG"] = lang;
                if (!string.IsNullOrWhiteSpace(args)) env["WINPLAY_ARGS"] = args;

                // WinAppInfo 没有独立的 args 字段，只能把参数拼在 exe 之后
                var exeWithArgs = string.IsNullOrWhiteSpace(args) ? exePath : exePath + " " + args;

                var intent = new Intent(Intent.ActionView);
                intent.SetComponent(new ComponentName("com.xiaomi.winplay", "com.xiaomi.winplay.WineActivity"));
                intent.PutExtra("app_info", new WinAppInfo(exeWithArgs, env, false));
                intent.PutExtra("app_name", string.IsNullOrWhiteSpace(name) ? "WinPlay" : name);
                intent.AddFlags(ActivityFlags.NewTask);
                StartActivity(intent);

                if (AppCatalog.Debug)
                {
                    Log("[launch] intent → WineActivity，app_name=" + name + "，exe=" + exeWithArgs);
                    foreach (var kv in env) Log("[launch] env " + kv.Key + "=" + kv.Value);
                }
                else
                {
                    Log("已请求 PC 引擎启动：" + exePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("intent 启动失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 用 root 脚本启动：脚本内先 cd 到工作目录再执行 wine，工作目录 100% 生效。
        /// 脚本模板复制自 RunWineAsync，仅改动 cd 目标与 LANG 两行。
        /// </summary>
        /// <param name="wineArgs">传给 wine 的完整参数（含 exe 与 args）。</param>
        /// <param name="cwdUnix">工作目录的 Android 路径。</param>
        /// <param name="lang">语言，写入 LANG 环境变量。</param>
        /// <returns>脚本执行结果，Output 中含 __WINE_EXIT 退出码。</returns>
        Task<RootShell.Result> RunWineInDirAsync(string wineArgs, string cwdUnix, string lang)
        {
            var script = $@"#!/system/bin/sh
B={EngineFiles}/arm64-v8a
WINE={_winePath ?? EngineFiles + "/arm64-v8a/lib/wine/aarch64-unix/wine"}
cd '{cwdUnix}' 2>/dev/null || cd {EngineFiles} || exit 9
export WINEPREFIX={_prefix ?? EngineFiles + "/prefix"}
export PATH=$B/bin:$PATH
export LD_LIBRARY_PATH=$B/lib/wine/aarch64-unix:$B/lib:/product/app/WinPlay/lib/arm64
export WINEDLLPATH=$B/lib/wine/aarch64-windows:$B/lib/wine/i386-windows:$B/lib/wine
export TMPDIR=/data/user/0/com.xiaomi.winplay/cache
export WINPLAY_CONFIG_PATH={EngineFiles}/cloud/winplay.conf
export LANG={lang}
export WINEDEBUG=-all
chown -R 7100:7100 {EngineFiles}/prefix 2>/dev/null
$WINE {wineArgs}
RC=$?
chown -R 7100:7100 {EngineFiles}/prefix 2>/dev/null
echo __WINE_EXIT=$RC
";
            if (AppCatalog.Debug) Log("[launch] wp_run.sh:\n" + script);

            return RootShell.ExecScriptAsync(new[]
            {
                "cat > /data/local/tmp/wp_run.sh << 'WPEOF'\n" + script + "WPEOF",
                "chmod 644 /data/local/tmp/wp_run.sh",
                "sh /data/local/tmp/wp_run.sh"
            });
        }

        /// <summary>
        /// 按清单项启动程序：先应用目录映射，再算工作目录，默认走 intent，
        /// 引擎不可用或被强制时改走 root 脚本；成功后记入最近使用。
        /// </summary>
        /// <param name="entry">清单项。</param>
        /// <returns>启动流程成功返回 true；参数缺失或抛出异常返回 false。</returns>
        async Task<bool> LaunchEntryAsync(AppEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Exe))
            {
                Log("清单项缺少 exe 路径。");
                return false;
            }

            try
            {
                // 顺序不可换：先挂盘符，再重写路径，否则 cwd 指向的目录在容器里还不存在
                await ApplyMountsAsync();

                var exe = NormalizePath(entry.Exe);
                var cwdWin = entry.ResolvedWorkDir;
                var cwdUnix = cwdWin.StartsWith("/") ? cwdWin : DosToUnix(cwdWin);
                var lang = ResolveLang(entry);
                var args = (entry.Args ?? "").Trim();

                DLog("[launch] exe=" + exe + " | cwd=" + cwdWin + " | cwdUnix=" + cwdUnix
                     + " | args=[" + args + "] | lang=" + lang);

                await ResolveDriveAsync();

                var forceScript = _forceScriptChk != null && _forceScriptChk.Checked;
                if (!forceScript && LaunchViaIntent(exe, entry.DisplayName, args, cwdWin, lang))
                {
                    await RecentStore.TouchAsync(entry);
                    return true;
                }
                if (!forceScript) Log("→ 改用脚本方式启动（可保证工作目录生效）");

                var wineArgs = "\"" + exe + "\"" + (args.Length > 0 ? " " + args : "");
                var r = await RunWineInDirAsync(wineArgs, cwdUnix, lang);
                var output = (r == null ? "" : (r.Output ?? "")).Trim();
                var ok = output.Contains("__WINE_EXIT=0");

                if (ok)
                {
                    await RecentStore.TouchAsync(entry);
                    Log("✔ 已通过脚本方式启动：" + entry.DisplayName);
                }
                else
                {
                    Log("脚本方式启动结束，未取得成功退出码：" + output);
                }
                return ok;
            }
            catch (Exception ex)
            {
                Log("启动失败：" + ex.Message);
                return false;
            }
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
            // 先查用户配置的目录映射（E: 及以后盘符），命中则按映射翻译；未命中再走原有硬编码规则
            var mapped = ApplyMountsToUnix(dosPath);
            if (mapped != null) return mapped;

            if (dosPath.StartsWith("C:\\")) return PrefixDrive + "/" + dosPath.Substring(3).Replace('\\', '/');
            if (dosPath.StartsWith("D:\\")) return "/sdcard/" + dosPath.Substring(3).Replace('\\', '/');
            return dosPath;
        }

        static string ToDosPath(string unixPath)
        {
            // /storage/emulated/0 与 /sdcard 指向同一位置，HyperOS 的 GetDocumentId 返回前者
            if (unixPath.StartsWith("/storage/emulated/0/"))
                return "D:\\" + unixPath.Substring(20).Replace('/', '\\');
            if (unixPath.StartsWith("/sdcard/")) return "D:\\" + unixPath.Substring(8).Replace('/', '\\');
            return "C:\\" + unixPath.TrimStart('/').Replace('/', '\\');
        }

        async Task CreateShortcutAsync(string name, string dosPath, string cwd = null, string lang = null, Bitmap icon = null)
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
                // 快捷方式也带上工作目录与语言：与清单页启动行为一致；旧快捷方式没有这两个 extra，引擎按默认处理
                if (!string.IsNullOrWhiteSpace(cwd)) intent.PutExtra("cwd", cwd);
                if (!string.IsNullOrWhiteSpace(lang)) intent.PutExtra("lang", lang);
                var id = "wpinst_" + DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var info = new ShortcutInfo.Builder(this, id)
                    .SetShortLabel(name)
                    .SetLongLabel(name + "（PC引擎）")
                    .SetIcon(Icon.CreateWithBitmap(icon ?? MakeIcon(name)))
                    .SetIntent(intent)
                    .Build();
                sm.RequestPinShortcut(info, null);
                Log("→ 已请求固定快捷方式，请在系统弹窗中确认添加。");
                _mainExe.Text = dosPath;
            }
            catch (Exception ex) { Log("创建快捷方式失败：" + ex.Message); }
            await Task.CompletedTask;
        }

        void CopyFeedback()
        {
            var desc = _fbDesc.Text?.Trim() ?? "";
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var title = "[" + stamp + "] " + Build.Model + " " + (desc.Length > 40 ? desc.Substring(0, 40) : desc);
            var body = BuildFeedbackBody(desc, stamp, withLog: true);
            var clip = Android.Content.ClipData.NewPlainText("feedback", title + "\n\n" + body);

            // .NET 绑定未导出 SetPrimaryClip，用 Java 反射调用
            var cm = GetSystemService(ClipboardService);
            var method = cm.Class.GetMethod("setPrimaryClip", new Java.Lang.Class[] { Java.Lang.Class.FromType(typeof(Android.Content.ClipData)) });
            method.Invoke(cm, new Java.Lang.Object[] { clip });
            Log("✔ 反馈内容已复制到剪贴板（含设备信息与日志），粘贴到 github.com/" + FbRepo + "/issues/new");
        }

        string BuildFeedbackBody(string desc, string stamp, bool withLog)
        {
            var b = new System.Text.StringBuilder();
            b.AppendLine("## 反馈时间");
            b.AppendLine(stamp);
            b.AppendLine();
            b.AppendLine("## 设备");
            b.AppendLine(Build.Manufacturer + " " + Build.Model + " (" + Build.Device + ")");
            b.AppendLine("Android " + Build.VERSION.Release + " / SDK " + Build.VERSION.SdkInt);
            try { b.AppendLine("App 版本: " + PackageManager.GetPackageInfo(PackageName, 0).VersionName); } catch { }
            b.AppendLine();
            b.AppendLine("## 问题描述");
            b.AppendLine(desc);
            if (withLog)
            {
                var log = LogStore.Full();
                if (log.Length > 55000) log = log.Substring(log.Length - 55000);
                b.AppendLine();
                b.AppendLine("## 详细日志（root通信/wine调用）");
                b.AppendLine("```");
                b.AppendLine(log);
                b.AppendLine("```");
            }
            return b.ToString();
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

        /// <summary>向日志面板追加一行带时间戳的日志，并在开启自动滚底时把面板滚动到末尾。</summary>
        /// <param name="msg">日志正文，不含时间戳前缀。</param>
        void Log(string msg) => RunOnUiThread(() =>
        {
            _log.Append($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            // 面板只 Append 不会跟随，日志超过一屏后用户永远看到开头几行，像「日志卡住」
            if (AppCatalog.AutoScrollLog && _logScroll != null)
                _logScroll.Post(() => _logScroll.FullScroll(Android.Views.FocusSearchDirection.Down));
        });

        // ==================== 程序清单 / 最近使用 / 目录映射 界面 ====================

        /// <summary>构建「清单」页：文件路径提示、重新加载、强制脚本开关与列表容器。</summary>
        /// <param name="host">要挂载内容的父容器。</param>
        void BuildAppsPage(LinearLayout host)
        {
            var title = new TextView(this) { Text = "── 程序清单（apps.json）──", TextSize = 14f };
            title.SetTextColor(Color.Rgb(30, 80, 160));
            host.AddView(title);

            _catalogPathHint = new TextView(this) { Text = "清单文件：" + AppCatalog.CatalogPath, TextSize = 11f };
            _catalogPathHint.SetTextColor(Color.Gray);
            host.AddView(_catalogPathHint);

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var reload = new Button(this) { Text = "重新加载清单" };
            reload.Click += async (s, e) => await RefreshCatalogAsync();
            row.AddView(reload, new LinearLayout.LayoutParams(0, -2, 1f));

            var copy = new Button(this) { Text = "复制清单路径" };
            copy.Click += (s, e) => CopyToClipboard("清单路径", AppCatalog.CatalogPath);
            row.AddView(copy, new LinearLayout.LayoutParams(0, -2, 1f));
            host.AddView(row);

            _forceScriptChk = new CheckBox(this) { Text = "强制脚本方式运行（保证工作目录生效）", TextSize = 13f };
            host.AddView(_forceScriptChk);

            var tip = new TextView(this)
            {
                Text = "若引擎不认工作目录，勾选上一项；日文游戏在 apps.json 里写 \"lang\": \"ja_JP.UTF-8\"。",
                TextSize = 12f
            };
            tip.SetTextColor(Color.Rgb(140, 90, 0));
            host.AddView(tip);

            _listCatalog = new LinearLayout(this) { Orientation = Orientation.Vertical };
            host.AddView(_listCatalog);
        }

        /// <summary>构建「最近」页：记录文件路径提示与列表容器。</summary>
        /// <param name="host">要挂载内容的父容器。</param>
        void BuildRecentPage(LinearLayout host)
        {
            var title = new TextView(this) { Text = "── 最近使用 ──", TextSize = 14f };
            title.SetTextColor(Color.Rgb(30, 80, 160));
            host.AddView(title);

            _recentPathHint = new TextView(this) { Text = "记录文件：" + RecentStore.RecentPath, TextSize = 11f };
            _recentPathHint.SetTextColor(Color.Gray);
            host.AddView(_recentPathHint);

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var reload = new Button(this) { Text = "刷新" };
            reload.Click += async (s, e) => await RefreshRecentAsync();
            row.AddView(reload, new LinearLayout.LayoutParams(0, -2, 1f));
            host.AddView(row);

            _listRecent = new LinearLayout(this) { Orientation = Orientation.Vertical };
            host.AddView(_listRecent);
        }

        /// <summary>构建工具页的「目录映射」区块：映射列表 + 添加映射 + 重新应用盘符。</summary>
        /// <param name="host">工具页容器。</param>
        void BuildMountsSection(LinearLayout host)
        {
            var title = new TextView(this) { Text = "── 目录映射（给容器加盘符）──", TextSize = 14f };
            title.SetTextColor(Color.Rgb(30, 80, 160));
            host.AddView(title);

            var sub = new TextView(this) { Text = "容器盘符 → Android 实际目录（仅做路径重写，不需要 root）", TextSize = 11f };
            sub.SetTextColor(Color.Gray);
            host.AddView(sub);

            _mountList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            host.AddView(_mountList);

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var add = new Button(this) { Text = "＋ 添加映射" };
            add.Click += (s, e) => ShowAddMountDialog();
            row.AddView(add, new LinearLayout.LayoutParams(0, -2, 1f));

            var apply = new Button(this) { Text = "重新应用全部盘符" };
            apply.Click += async (s, e) => await ApplyMountsAsync();
            row.AddView(apply, new LinearLayout.LayoutParams(0, -2, 1f));
            host.AddView(row);

            _mountHint = new TextView(this) { TextSize = 12f };
            _mountHint.SetTextColor(Color.Rgb(140, 90, 0));
            host.AddView(_mountHint);
        }

        /// <summary>构建工具页的「诊断」区块：详细日志开关、存储访问状态与授权入口。</summary>
        /// <param name="host">工具页容器。</param>
        void BuildDebugSection(LinearLayout host)
        {
            var title = new TextView(this) { Text = "── 诊断 ──", TextSize = 14f };
            title.SetTextColor(Color.Rgb(30, 80, 160));
            host.AddView(title);

            _debugChk = new CheckBox(this) { Text = "详细日志（输出路径重写/盘符/Env/脚本全文）", TextSize = 13f };
            _debugChk.Checked = AppCatalog.Debug;
            _debugChk.CheckedChange += (s, e) => Log(_debugChk.Checked
                ? "详细日志已开启（本次运行有效；永久生效请改 apps.json 的 debug）"
                : "详细日志已关闭");
            host.AddView(_debugChk);

            var storage = new TextView(this)
            {
                Text = "存储访问：" + (FileAccess.UsingDirect ? "直读（已获所有文件访问）" : "回退 root"),
                TextSize = 12f
            };
            storage.SetTextColor(Color.Gray);
            host.AddView(storage);

            var grant = new Button(this) { Text = "申请「所有文件访问」权限（可选，便于直读 SD 卡）" };
            grant.Click += (s, e) => EnsureAllFilesAccess();
            host.AddView(grant);
        }

        /// <summary>重新读取清单并刷新界面；失败时保留旧列表不白屏。</summary>
        /// <returns>加载完成的任务。</returns>
        async Task RefreshCatalogAsync()
        {
            try
            {
                _catalog = await AppCatalog.LoadAsync() ?? new List<AppEntry>();
                if (_debugChk != null) _debugChk.Checked = AppCatalog.Debug;
                RebuildMountRows();
                RebuildCatalogRows();
                if (_catalogPathHint != null) _catalogPathHint.Text = "清单文件：" + AppCatalog.CatalogPath;
            }
            catch (Exception ex)
            {
                Log("加载清单失败：" + ex.Message);
            }
        }

        /// <summary>重新读取最近使用并刷新界面。</summary>
        /// <returns>加载完成的任务。</returns>
        async Task RefreshRecentAsync()
        {
            try
            {
                _recents = new List<RecentEntry>(await RecentStore.LoadAsync());
                RebuildRecentRows();
                if (_recentPathHint != null) _recentPathHint.Text = "记录文件：" + RecentStore.RecentPath;
            }
            catch (Exception ex)
            {
                Log("加载最近使用失败：" + ex.Message);
            }
        }

        /// <summary>重建清单列表；为空时显示提示文字。</summary>
        void RebuildCatalogRows()
        {
            if (_listCatalog == null) return;
            _listCatalog.RemoveAllViews();

            if (_catalog.Count == 0)
            {
                _listCatalog.AddView(MakeTip("清单为空或未找到 apps.json，点上方「重新加载清单」重试。"));
                return;
            }

            foreach (var entry in _catalog) _listCatalog.AddView(BuildCatalogRow(entry));
        }

        /// <summary>构建一条清单行：启动、创建快捷方式、查看/临时修改参数。</summary>
        /// <param name="entry">清单项。</param>
        /// <returns>可直接加入列表的行容器。</returns>
        LinearLayout BuildCatalogRow(AppEntry entry)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };

            var open = new Button(this) { Text = "▶ " + entry.DisplayName };
            open.Click += async (s, e) => await LaunchEntryAsync(entry);
            row.AddView(open, new LinearLayout.LayoutParams(0, -2, 1f));

            var sc = new Button(this) { Text = "快捷" };
            sc.Click += async (s, e) => await CreateShortcutForEntryAsync(entry);
            row.AddView(sc);

            var edit = new Button(this) { Text = "参数" };
            edit.Click += (s, e) => ShowEntryDialog(entry);
            row.AddView(edit);

            return row;
        }

        /// <summary>重建最近使用列表；为空时显示提示文字。</summary>
        void RebuildRecentRows()
        {
            if (_listRecent == null) return;
            _listRecent.RemoveAllViews();

            if (_recents.Count == 0)
            {
                _listRecent.AddView(MakeTip("暂无记录，运行任意程序后自动出现。"));
                return;
            }

            foreach (var item in _recents)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };

                var open = new Button(this) { Text = "🕘 " + item.Name + " · " + AppEntry.LastSegment(item.Exe) };
                open.Click += async (s, e) => await LaunchEntryAsync(item.ToAppEntry());
                row.AddView(open, new LinearLayout.LayoutParams(0, -2, 1f));

                var del = new Button(this) { Text = "✕" };
                var target = item;
                del.Click += async (s, e) =>
                {
                    await RecentStore.RemoveAsync(target.Exe);
                    await RefreshRecentAsync();
                };
                row.AddView(del);

                _listRecent.AddView(row);
            }
        }

        /// <summary>重建目录映射行；点击盘符立即重新应用，点击 ✕ 删除并保存。</summary>
        void RebuildMountRows()
        {
            if (_mountList == null) return;
            _mountList.RemoveAllViews();

            var mounts = AppCatalog.Mounts;
            if (mounts.Count == 0)
            {
                _mountList.AddView(MakeTip("暂无映射，点下方「＋ 添加映射」。"));
                if (_mountHint != null)
                    _mountHint.Text = "映射只影响路径翻译，游戏内不会出现对应盘符。";
                return;
            }

            foreach (var m in mounts.ToList())
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };

                var drive = new Button(this) { Text = m.Drive };
                drive.Click += async (s, e) => await ApplyMountsAsync();
                row.AddView(drive);

                var label = new TextView(this) { Text = "→ " + m.AndroidPath, TextSize = 11f };
                label.SetTextColor(Color.DarkGray);
                row.AddView(label, new LinearLayout.LayoutParams(0, -2, 1f));

                var del = new Button(this) { Text = "✕" };
                var target = m;
                del.Click += async (s, e) =>
                {
                    AppCatalog.Mounts.Remove(target);
                    await SaveAndApplyMountsAsync();
                    Log("已删除映射：" + target.Drive);
                };
                row.AddView(del);

                _mountList.AddView(row);
            }

            if (_mountHint != null)
                _mountHint.Text = "映射只影响路径翻译，点盘符按钮可重新应用；✕ 删除。";
        }

        /// <summary>保存清单并重新应用目录映射；顺带在后台校验映射目录可访问性。</summary>
        /// <returns>保存与应用完成的任务。</returns>
        async Task SaveAndApplyMountsAsync()
        {
            await AppCatalog.SaveAsync(_catalog);

            // 可访问性校验放后台线程：FileAccess.Exists 会同步走 su，直接在 UI 线程调用会冻结界面
            string bad = null;
            await Task.Run(() =>
            {
                foreach (var m in AppCatalog.Mounts)
                {
                    if (string.IsNullOrWhiteSpace(m.AndroidPath)) continue;
                    if (!FileAccess.Exists(m.AndroidPath)) { bad = m.AndroidPath; break; }
                }
            });
            if (bad != null) Log("⚠ 目录不可访问（未授权或被系统限制）：" + bad);

            RebuildMountRows();
            await ApplyMountsAsync();
        }

        /// <summary>展示并临时修改某一项的启动参数与工作目录（永久生效请编辑 apps.json）。</summary>
        /// <param name="entry">清单项。</param>
        void ShowEntryDialog(AppEntry entry)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));

            box.AddView(MakeTip("exe：" + entry.Exe));
            box.AddView(MakeTip("工作目录：" + entry.ResolvedWorkDir));
            box.AddView(MakeTip("语言：" + ResolveLang(entry)));

            var argsInput = new EditText(this) { Text = entry.Args ?? "", Hint = "启动参数，如 -windowed", TextSize = 13f };
            box.AddView(argsInput);

            var workInput = new EditText(this) { Text = entry.WorkDir ?? "", Hint = "工作目录（留空 = exe 所在文件夹）", TextSize = 13f };
            box.AddView(workInput);

            new AlertDialog.Builder(this)
                .SetTitle("程序参数")
                .SetView(box)
                .SetPositiveButton("应用（本次运行）", (s, e) =>
                {
                    entry.Args = argsInput.Text?.Trim();
                    entry.WorkDir = workInput.Text?.Trim();
                    Log("已临时应用参数；要永久生效请编辑 apps.json。");
                })
                .SetNegativeButton("取消", (s, e) => { })
                .Show();
        }

        /// <summary>弹出「添加目录映射」对话框：选择盘符并浏览目录，确认后写入配置并应用。</summary>
        void ShowAddMountDialog()
        {
            var letters = new List<string>();
            for (char ch = 'E'; ch <= 'Z'; ch++)
            {
                var used = false;
                foreach (var m in AppCatalog.Mounts)
                {
                    if (m.Letter() == ch.ToString().ToLowerInvariant()) { used = true; break; }
                }
                if (!used) letters.Add(ch + ":");
            }

            if (letters.Count == 0) { Log("盘符已用完（E: ~ Z:）。"); return; }

            var spinner = new Spinner(this);
            spinner.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, letters);

            var pathInput = new EditText(this) { Hint = "点下方「浏览…」选择目录", TextSize = 13f };
            pathInput.Focusable = false;

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
            box.AddView(MakeTip("选择容器盘符："));
            box.AddView(spinner);
            box.AddView(MakeTip("映射到 Android 目录："));
            box.AddView(pathInput);

            new AlertDialog.Builder(this)
                .SetTitle("添加目录映射")
                .SetView(box)
                .SetPositiveButton("确定", (s, e) =>
                {
                    var letter = spinner.SelectedItem?.ToString() ?? "";
                    var path = pathInput.Text?.Trim() ?? "";
                    if (letter.Length != 2 || path.Length == 0)
                    {
                        Log("盘符或目录为空，已取消。");
                        return;
                    }
                    AppCatalog.Mounts.Add(new MountEntry { Drive = letter, AndroidPath = path });
                    _ = SaveAndApplyMountsAsync();
                })
                .SetNeutralButton("浏览…", (s, e) =>
                {
                    // 记住当前选中盘符，等目录树回调时再落盘
                    var letter = spinner.SelectedItem?.ToString() ?? "";
                    _pendingMountLetter = letter.Length == 2 ? letter[0] : -1;
                    ChooseTree();
                })
                .SetNegativeButton("取消", (s, e) => { })
                .Show();
        }

        /// <summary>打开系统目录树选择器，可进入内部存储、SD 卡与 USB。</summary>
        void ChooseTree()
        {
            try
            {
                var intent = new Intent(Intent.ActionOpenDocumentTree);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivityForResult(intent, TreeRequest);
            }
            catch (Exception ex) { Log("打开目录选择器失败：" + ex.Message); }
        }

        /// <summary>处理目录树选择结果：转成真实路径后写入映射（可访问性校验在 SaveAndApplyMountsAsync 中异步执行）。</summary>
        /// <param name="uri">系统返回的 content:// 目录树 URI。</param>
        void OnTreePicked(Android.Net.Uri uri)
        {
            var path = TreeUriToPath(uri);
            if (string.IsNullOrWhiteSpace(path))
            {
                Log("无法把所选目录转成真实路径，请换一个目录。");
                return;
            }

            // 可读性校验延后到 SaveAndApplyMountsAsync（那里放后台线程），
            // 避免在此同步走 su 冻结界面

            var letter = _pendingMountLetter > 0 ? ((char)_pendingMountLetter).ToString() + ":" : "E:";
            AppCatalog.Mounts.Add(new MountEntry { Drive = letter, AndroidPath = path });
            Log("＋ 已添加映射：" + letter + " → " + path);
            _ = SaveAndApplyMountsAsync();
        }

        /// <summary>把 externalstorage 目录树 URI 转成文件系统路径，如 primary:Games → /storage/emulated/0/Games。</summary>
        /// <param name="uri">目录树 URI。</param>
        /// <returns>真实路径；无法解析时返回 null。</returns>
        string TreeUriToPath(Android.Net.Uri uri)
        {
            try
            {
                var docId = uri.LastPathSegment;      // 形如 primary:Games
                if (string.IsNullOrEmpty(docId)) return null;

                var idx = docId.IndexOf(':');
                if (idx < 0) return null;

                var volume = docId.Substring(0, idx);
                var relative = docId.Substring(idx + 1);
                var root = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
                    ? "/storage/emulated/0"
                    : "/storage/" + volume;

                return string.IsNullOrEmpty(relative) ? root : root + "/" + relative;
            }
            catch (Exception ex)
            {
                Log("解析目录 URI 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 把单文件 document URI 还原成文件系统路径。
        /// 单文件 URI 的 LastPathSegment 形如 "document:primary%3A..."，按冒号切分会误判卷名，
        /// 因此必须用 DocumentsContract 取 docId。
        /// </summary>
        /// <param name="uri">文件选择器返回的 document URI。</param>
        /// <returns>真实路径，如 /storage/emulated/0/galgame/x/y.exe；无法解析时返回 null。</returns>
        string DocumentUriToPath(Android.Net.Uri uri)
        {
            try
            {
                string docId = null;
                try { docId = Android.Provider.DocumentsContract.GetDocumentId(uri); }
                catch { /* 非 documents provider（媒体库等）会抛异常，回退到 LastPathSegment */ }
                if (string.IsNullOrEmpty(docId)) docId = uri.LastPathSegment;
                if (string.IsNullOrEmpty(docId)) return null;

                // raw: 前缀表示 docId 本身就是绝对路径
                if (docId.StartsWith("raw:", StringComparison.OrdinalIgnoreCase)) return docId.Substring(4);

                var idx = docId.IndexOf(':');
                if (idx <= 0) return null;

                var volume = docId.Substring(0, idx);
                var relative = docId.Substring(idx + 1);

                // 这些不是真实卷名，说明该 provider 不提供文件系统路径
                if (volume.Equals("document", StringComparison.OrdinalIgnoreCase)
                    || volume.Equals("msf", StringComparison.OrdinalIgnoreCase)
                    || volume.Equals("downloads", StringComparison.OrdinalIgnoreCase))
                    return null;

                var root = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
                    ? "/storage/emulated/0"
                    : "/storage/" + volume;

                return string.IsNullOrEmpty(relative) ? root : root + "/" + relative;
            }
            catch (Exception ex)
            {
                Log("解析文件 URI 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>按清单项创建桌面快捷方式：自动带上工作目录、语言与解析出的图标。</summary>
        /// <param name="entry">清单项。</param>
        /// <returns>创建完成的任务。</returns>
        async Task CreateShortcutForEntryAsync(AppEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Exe)) return;

            // 图标解析可能要走 root 读 exe，必须放到后台线程，否则会卡住界面
            var icon = await Task.Run(() => ResolveIcon(entry));
            await CreateShortcutAsync(entry.DisplayName, entry.Exe, entry.ResolvedWorkDir, ResolveLang(entry), icon);
        }

        /// <summary>生成一行灰色提示文字。</summary>
        /// <param name="text">提示内容。</param>
        /// <returns>可直接加入容器的文本控件。</returns>
        TextView MakeTip(string text)
        {
            var t = new TextView(this) { Text = text, TextSize = 11f };
            t.SetTextColor(Color.Gray);
            return t;
        }

        /// <summary>把文本复制到剪贴板（.NET 绑定未导出 SetPrimaryClip，用 Java 反射调用）。</summary>
        /// <param name="label">剪贴板标签。</param>
        /// <param name="text">要复制的文本。</param>
        void CopyToClipboard(string label, string text)
        {
            try
            {
                var clip = Android.Content.ClipData.NewPlainText(label ?? "text", text ?? "");
                var cm = GetSystemService(ClipboardService);
                var method = cm.Class.GetMethod("setPrimaryClip",
                    new Java.Lang.Class[] { Java.Lang.Class.FromType(typeof(Android.Content.ClipData)) });
                method.Invoke(cm, new Java.Lang.Object[] { clip });
                Log("✔ 已复制：" + (text ?? "").Length + " 字符");
            }
            catch (Exception ex) { Log("复制失败：" + ex.Message); }
        }

        /// <summary>跳转系统「所有文件访问」授权页；Android 11+ 没有普通权限弹窗，只能由用户手动允许。</summary>
        void EnsureAllFilesAccess()
        {
            try
            {
                var intent = new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission,
                    Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
            }
            catch (Exception ex) { Log("打开授权页失败：" + ex.Message); }
        }

        static string TakeSafe(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        // ---------- ⑩ 导出日志 ----------
        async Task ExportLog()
        {
            await Task.Run(() =>
            {
                try
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var log = BuildLog(stamp);
                    var values = new ContentValues();
                    values.Put(MediaStore.Downloads.InterfaceConsts.DisplayName, "winplay_log_" + stamp + ".log");
                    values.Put(MediaStore.Downloads.InterfaceConsts.MimeType, "text/plain");
                    values.Put(MediaStore.Downloads.InterfaceConsts.RelativePath, "Download/");
                    var uri = ContentResolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
                    if (uri == null) { Log("✕ 日志保存被拒绝（存储权限）"); return; }
                    using (var os = ContentResolver.OpenOutputStream(uri))
                    using (var sw = new System.IO.StreamWriter(os))
                        sw.Write(log);
                    Log("✔ 日志已保存 /sdcard/Download/winplay_log_" + stamp + ".log（" + log.Length + " 字符）");
                    RunOnUiThread(() =>
                    {
                        try
                        {
                            var share = new Intent(Intent.ActionSend);
                            share.SetType("text/plain");
                            share.PutExtra(Intent.ExtraText, log);
                            share.PutExtra(Intent.ExtraSubject, "WinPlay 安装器日志 " + stamp);
                            StartActivity(Intent.CreateChooser(share, "分享日志"));
                        }
                        catch { }
                    });
                }
                catch (Exception ex) { Log("✕ 导出日志失败：" + ex.Message); }
            });
        }

        string BuildLog(string stamp)
        {
            var dev = "";
            try
            {
                var b = new System.Text.StringBuilder();
                b.AppendLine("机型: " + Build.Manufacturer + " " + Build.Model);
                b.AppendLine("型号: " + Build.Device);
                b.AppendLine("Android: " + Build.VERSION.Release + " (SDK " + Build.VERSION.SdkInt + ")");
                b.AppendLine("App: " + PackageManager.GetPackageInfo(PackageName, 0).VersionName);
                dev = b.ToString();
            }
            catch { }
            return stamp + "\n" + dev + "\n── 日志开始 ──\n" + LogStore.Full() + "\n── 日志结束 ──\n";
        }

        // ---------- ⑪ 反馈：自动上传 GitHub Issue（bug + feedback 标签） ----------
        const string FbRepo = "liuyuchen012/winplay-installer";

        // ⑪ 生成反馈内容并打开 GitHub 预填提交页（issues/new?title=&body=&labels=bug）。
        // 用户在自己的 GitHub 账号中登录并点击提交 → 议题归属用户，无需 App 持有任何 Token。
        async Task OpenFeedbackSubmit()
        {
            var desc = _fbDesc.Text?.Trim();
            if (string.IsNullOrWhiteSpace(desc)) { Log("请先填写问题描述。"); return; }
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var title = (desc.Length > 40 ? desc.Substring(0, 40) : desc).Replace("\n", " ");
            var body = BuildFeedbackBody(desc, stamp, withLog: true);
            var url = "https://github.com/" + FbRepo + "/issues/new?title=" + System.Net.WebUtility.UrlEncode(title)
                      + "&body=" + System.Net.WebUtility.UrlEncode(body.Length > 60000 ? body.Substring(0, 60000) : body)
                      + "&labels=bug%2Cfeedback";
            Log("→ 已生成反馈内容，正在打开 GitHub 提交页…");
            try
            {
                var i = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                i.AddFlags(ActivityFlags.NewTask);
                StartActivity(i);
                Log("请在打开的页面确认标题/内容后点「Submit new issue」（登录你自己的 GitHub 账号）。");
            }
            catch (Exception ex)
            {
                Log("✕ 打开失败：" + ex.Message + "，可用下方「复制反馈内容」手动提交。");
            }
            await Task.CompletedTask;
        }

        async Task<string> PostIssueAsync(string token, string title, string body)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var payload = "{\"title\":" + JsonEscape(title) + ",\"body\":" + JsonEscape(body) + ",\"labels\":[\"bug\",\"feedback\"]}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                    var conn = (Java.Net.HttpURLConnection)(new Java.Net.URL("https://api.github.com/repos/" + FbRepo + "/issues")).OpenConnection();
                    conn.RequestMethod = "POST";
                    conn.SetRequestProperty("Authorization", "Bearer " + token);
                    conn.SetRequestProperty("Accept", "application/vnd.github+json");
                    conn.SetRequestProperty("Content-Type", "application/json");
                    conn.DoOutput = true;
                    conn.ConnectTimeout = 20000;
                    conn.ReadTimeout = 30000;
                    using (var os = conn.OutputStream)
                    {
                        os.Write(bytes, 0, bytes.Length);
                        os.Flush();
                    }
                    int code = (int)conn.ResponseCode;
                    var stream = code >= 400 ? conn.ErrorStream : conn.InputStream;
                    if (stream == null) return null;
                    using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                    {
                        var resp = reader.ReadToEnd();
                        if (code >= 300) { LogStore.Add("[feedback] HTTP " + code + ": " + (resp.Length > 200 ? resp.Substring(0, 200) : resp)); return null; }
                        var m = System.Text.RegularExpressions.Regex.Match(resp, "\"number\":(\\d+)");
                        return m.Success ? ("#" + m.Groups[1].Value + " https://github.com/" + FbRepo + "/issues/" + m.Groups[1].Value) : ("HTTP " + code);
                    }
                }
                catch (Exception ex) { LogStore.Add("[feedback] POST 异常: " + ex.Message); return null; }
            });
        }

        static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");

        int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);
    }
}
