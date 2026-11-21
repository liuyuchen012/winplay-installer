using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WinPlayInstaller
{
    /// <summary>目录映射项：把一个容器盘符指向 Android 上的真实目录，同时供路径重写与 dosdevices 软链使用。</summary>
    public sealed class MountEntry
    {
        /// <summary>容器内盘符，形如 "E:"。只取首字母，大小写不敏感。</summary>
        [JsonPropertyName("drive")]
        public string Drive { get; set; }

        /// <summary>Windows 侧可见路径，用于安装器做路径重写，如 "E:\\" 或 "E:\\Games"；为空时等价于 "盘符\"。</summary>
        [JsonPropertyName("win")]
        public string WinPath { get; set; }

        /// <summary>Android 侧真实目录，如 /storage/1A2B-3C4D/ROMs。该项必填。</summary>
        [JsonPropertyName("android")]
        public string AndroidPath { get; set; }

        /// <summary>取规范化后的单字符盘符（小写，无冒号）；非法时返回空串。</summary>
        /// <returns>单个小写字母，如 "e"；无法解析时返回空字符串。</returns>
        public string Letter()
        {
            var d = (Drive ?? "").Trim().TrimEnd(':').ToLowerInvariant();
            return d.Length == 1 ? d : "";
        }

        /// <summary>取实际用于路径重写的 Windows 前缀；未配置 win 时回退为 "盘符\"。</summary>
        /// <returns>可用于 StartsWith 比较的 Windows 路径前缀。</returns>
        public string WinPrefix()
        {
            if (!string.IsNullOrWhiteSpace(WinPath)) return WinPath.Trim();
            var letter = Letter();
            return letter.Length == 1 ? letter.ToUpperInvariant() + ":\\" : "";
        }
    }

    /// <summary>程序清单中的单个程序项。name 与 exe 是有效项的最低要求，其余字段可省略。</summary>
    public sealed class AppEntry
    {
        /// <summary>列表显示名与快捷方式标题。为空时依次回退到父目录名、exe 文件名。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>主程序路径。支持 D:\Games\x\x.exe、D:/Games/x/x.exe、/sdcard/Games/x/x.exe 三种写法。</summary>
        [JsonPropertyName("exe")]
        public string Exe { get; set; }

        /// <summary>启动参数，原样拼在 exe 之后，如 "-windowed -lang=ja"。</summary>
        [JsonPropertyName("args")]
        public string Args { get; set; }

        /// <summary>可选工作目录；为空时自动取 exe 所在目录（本功能的核心默认行为）。</summary>
        [JsonPropertyName("workdir")]
        public string WorkDir { get; set; }

        /// <summary>可选语言，如 ja_JP.UTF-8；为空时用清单顶层 defaultLang，再为空用 zh_CN.UTF-8。</summary>
        [JsonPropertyName("lang")]
        public string Lang { get; set; }

        /// <summary>可选图标：可填图片路径（png/jpg），也可填某个带图标的 exe/dll 路径；为空时走分级回退。</summary>
        [JsonPropertyName("icon")]
        public string Icon { get; set; }

        /// <summary>实际生效的工作目录：WorkDir 非空取它，否则取 Exe 的父目录。</summary>
        public string ResolvedWorkDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(WorkDir)) return WorkDir.Trim().TrimEnd('/', '\\');
                return ExeDirOf(Exe);
            }
        }

        /// <summary>用于去重与缓存比较的规范键：反斜杠统一为斜杠、去首尾空白、转小写。</summary>
        public string NormalizedExe => Normalize(Exe);

        /// <summary>最终显示的友好名称：Name → 父目录名 → exe 文件名去扩展名。</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
                var dirName = LastSegment(ExeDirOf(Exe));
                if (!string.IsNullOrWhiteSpace(dirName)) return dirName;
                return StripExtension(LastSegment(Exe));
            }
        }

        /// <summary>判断该项是否可用（exe 非空）。</summary>
        /// <returns>可用返回 true。</returns>
        public bool IsValid() => !string.IsNullOrWhiteSpace(Exe);

        /// <summary>取路径的父目录，兼容 Windows 与 Android 两种分隔符。</summary>
        /// <param name="path">文件或目录路径。</param>
        /// <returns>父目录路径；无法解析时返回空字符串。</returns>
        public static string ExeDirOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var p = path.Trim();
            var i = p.LastIndexOfAny(new[] { '\\', '/' });
            if (i < 0) return "";
            if (i == 0) return p.Substring(0, 1);                      // 根目录 "/"
            if (i == 2 && p.Length > 1 && p[1] == ':') return p.Substring(0, 3); // 盘符根 "D:\"
            return p.Substring(0, i);
        }

        /// <summary>取路径的最后一段（文件名或目录名），自动忽略末尾分隔符。</summary>
        /// <param name="path">任意路径。</param>
        /// <returns>最后一段文本；输入为空时返回空字符串。</returns>
        public static string LastSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var p = path.Trim().TrimEnd('/', '\\');
            var i = p.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>去掉文件名的扩展名。</summary>
        /// <param name="name">含扩展名的文件名。</param>
        /// <returns>不带扩展名的名称；无扩展名时原样返回。</returns>
        public static string StripExtension(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var i = name.LastIndexOf('.');
            return i > 0 ? name.Substring(0, i) : name;
        }

        /// <summary>把路径规范化为去重键：反斜杠转斜杠、去首尾空白、统一小写。</summary>
        /// <param name="path">原始路径。</param>
        /// <returns>规范化后的字符串；输入为空时返回空字符串。</returns>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Trim().Replace('\\', '/').ToLowerInvariant();
        }
    }

    /// <summary>apps.json 的顶层结构，便于以后追加 globals 而不破坏旧文件。</summary>
    internal sealed class CatalogFile
    {
        /// <summary>是否开启详细诊断日志。</summary>
        [JsonPropertyName("debug")]
        public bool Debug { get; set; }

        /// <summary>日志面板是否自动滚动到底部。</summary>
        [JsonPropertyName("autoScrollLog")]
        public bool AutoScrollLog { get; set; } = true;

        /// <summary>未单独指定语言的程序所使用的默认语言。</summary>
        [JsonPropertyName("defaultLang")]
        public string DefaultLang { get; set; }

        /// <summary>「最近使用」保留的最大条数。</summary>
        [JsonPropertyName("recentLimit")]
        public int RecentLimit { get; set; } = 20;

        /// <summary>自动扫描同目录 exe 提取图标时的最多尝试个数。</summary>
        [JsonPropertyName("iconScanLimit")]
        public int IconScanLimit { get; set; } = 5;

        /// <summary>存储访问模式：auto / direct / root。</summary>
        [JsonPropertyName("storageMode")]
        public string StorageMode { get; set; }

        /// <summary>目录映射列表。</summary>
        [JsonPropertyName("mounts")]
        public List<MountEntry> Mounts { get; set; }

        /// <summary>程序清单。</summary>
        [JsonPropertyName("apps")]
        public List<AppEntry> Apps { get; set; }
    }

    /// <summary>
    /// 程序清单与应用全局配置的读写层（apps.json）。
    /// 设计要点：不引用任何 Activity，失败不抛异常，只通过 <see cref="Notify"/> 回传原因；
    /// 解析失败时保留上一份内存数据，避免界面因一次手误而整体空白。
    /// </summary>
    public static class AppCatalog
    {
        /// <summary>清单首选目录（引擎 D 盘的映射根，用户可直接用文件管理器编辑）。</summary>
        public const string PreferDir = "/sdcard/WinPlay";

        /// <summary>清单文件名。</summary>
        public const string FileName = "apps.json";

        /// <summary>JSON 序列化选项：容忍注释与尾逗号，中文与反斜杠不转义，便于用户手改。</summary>
        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>日志回调，由 MainActivity 注入；为 null 时静默。</summary>
        public static Action<string> Notify { get; set; }

        /// <summary>实际读取成功的清单路径；未成功时为将要写入的首选路径。供界面显示。</summary>
        public static string CatalogPath { get; private set; } = PreferDir + "/" + FileName;

        /// <summary>是否开启详细诊断日志（由清单的 debug 字段控制）。</summary>
        public static bool Debug { get; private set; }

        /// <summary>日志面板是否自动滚底。</summary>
        public static bool AutoScrollLog { get; private set; } = true;

        /// <summary>默认语言。</summary>
        public static string DefaultLang { get; private set; } = "zh_CN.UTF-8";

        /// <summary>最近使用保留条数。</summary>
        public static int RecentLimit { get; private set; } = 20;

        /// <summary>图标扫描同目录 exe 的最多尝试个数。</summary>
        public static int IconScanLimit { get; private set; } = 5;

        /// <summary>存储访问模式。</summary>
        public static StorageMode StorageAccess { get; private set; } = StorageMode.Auto;

        /// <summary>最近一次成功加载的目录映射列表（供启动时应用盘符与路径重写）。</summary>
        public static List<MountEntry> Mounts { get; private set; } = new List<MountEntry>();

        /// <summary>最近一次成功加载的程序列表；解析失败时继续持有它。</summary>
        private static List<AppEntry> _lastApps = new List<AppEntry>();

        /// <summary>私有目录下的清单路径（首选目录不可写时的回退位置）。</summary>
        private static string PrivatePath
        {
            get
            {
                try { return Path.Combine(Android.App.Application.Context.FilesDir.AbsolutePath, FileName); }
                catch { return FileName; }
            }
        }

        /// <summary>读取清单。文件不存在时生成示例文件并返回示例列表。</summary>
        /// <returns>程序列表；任何失败都返回上一份内存数据（可能为空列表），不会返回 null。</returns>
        public static async Task<List<AppEntry>> LoadAsync()
        {
            var candidates = new[] { PreferDir + "/" + FileName, PrivatePath };
            foreach (var path in candidates)
            {
                var text = await ReadTextAsync(path);
                if (string.IsNullOrWhiteSpace(text)) continue;

                try
                {
                    CatalogFile file;
                    if (text.TrimStart().StartsWith("["))
                    {
                        // 兼容顶层直接是数组的简写形式
                        file = new CatalogFile { Apps = JsonSerializer.Deserialize<List<AppEntry>>(text, JsonOptions) };
                    }
                    else
                    {
                        file = JsonSerializer.Deserialize<CatalogFile>(text, JsonOptions);
                    }

                    if (file == null) continue;

                    CatalogPath = path;
                    ApplyGlobals(file);
                    _lastApps = SanitizeApps(file.Apps);
                    Mounts = SanitizeMounts(file.Mounts);

                    if (Debug)
                    {
                        Notify?.Invoke("[catalog] 已加载 " + path + "，程序 " + _lastApps.Count + " 个，映射 " + Mounts.Count + " 条");
                    }
                    else
                    {
                        Notify?.Invoke("[catalog] 已加载 " + _lastApps.Count + " 个程序");
                    }
                    return _lastApps;
                }
                catch (JsonException ex)
                {
                    // 保留上一份数据，只报告行号级错误，避免用户手改出错时界面整体消失
                    Notify?.Invoke("[catalog] 解析失败（" + path + "）：" + ex.Message + "；继续使用上一份清单");
                    return _lastApps;
                }
            }

            // 两个位置都没有 → 生成示例，让用户有个能直接改的模板
            CatalogPath = PreferDir + "/" + FileName;
            ApplyGlobals(new CatalogFile());
            Mounts = new List<MountEntry>();
            _lastApps = DefaultApps();
            var ok = await SaveAsync(_lastApps);
            Notify?.Invoke(ok
                ? "[catalog] 未找到清单，已生成示例：" + CatalogPath
                : "[catalog] 未找到清单，且示例写入失败（无 root 或无权限）：" + CatalogPath);
            return _lastApps;
        }

        /// <summary>把程序列表与当前全局配置写回清单文件。</summary>
        /// <param name="apps">要保存的程序列表；为 null 时按空列表处理。</param>
        /// <returns>写入成功返回 true。</returns>
        public static async Task<bool> SaveAsync(List<AppEntry> apps)
        {
            var file = new CatalogFile
            {
                Debug = Debug,
                AutoScrollLog = AutoScrollLog,
                DefaultLang = DefaultLang,
                RecentLimit = RecentLimit,
                IconScanLimit = IconScanLimit,
                StorageMode = StorageModeText(StorageAccess),
                Mounts = Mounts,
                Apps = apps ?? new List<AppEntry>()
            };

            var json = JsonSerializer.Serialize(file, JsonOptions);
            var text = SampleHeader() + json;

            return await Task.Run(() => FileAccess.WriteAllText(CatalogPath, text));
        }

        /// <summary>生成示例清单文本，供首次运行时写盘，也作为用户手改的模板。</summary>
        /// <returns>带注释的完整 JSON 字符串。</returns>
        public static string SampleJson()
        {
            return SampleHeader() + @"{
  ""debug"": false,
  ""autoScrollLog"": true,
  ""defaultLang"": ""zh_CN.UTF-8"",
  ""recentLimit"": 20,
  ""iconScanLimit"": 5,
  ""storageMode"": ""auto"",

  ""mounts"": [
    // 给容器加 E 盘，指向手机 SD 卡上的游戏目录（需要 root）
    { ""drive"": ""E:"", ""android"": ""/storage/1A2B-3C4D/ROMs"" }
  ],

  ""apps"": [
    // ① 最简写法：名字取父目录名，工作目录自动取 exe 所在文件夹
    { ""name"": ""示例游戏"", ""exe"": ""D:\\Games\\Sample\\sample.exe"" },

    // ② 同一会社多款都叫 boot.exe：靠 name 区分，图标从带图标的 game.exe 提取
    { ""name"": ""某会社游戏"", ""exe"": ""D:\\Games\\A\\boot.exe"", ""icon"": ""D:\\Games\\A\\game.exe"" },

    // ③ 日文游戏：单独指定语言与启动参数
    { ""name"": ""日文 Galgame"", ""exe"": ""E:\\gal\\game.exe"", ""args"": ""-windowed"", ""lang"": ""ja_JP.UTF-8"" },

    // ④ 便携版工具：手动指定工作目录
    { ""name"": ""便携工具"", ""exe"": ""D:\\Tools\\tool.exe"", ""args"": ""-silent"", ""workdir"": ""D:\\Tools"" }
  ]
}";
        }

        /// <summary>生成示例清单的注释头，说明三条最常被问到的规则。</summary>
        /// <returns>JSON 注释文本（解析时会被跳过）。</returns>
        private static string SampleHeader()
        {
            return @"// ===== WinPlay 安装器 程序清单 =====
// name 与 exe 必填；workdir 省略即 exe 所在文件夹；改完回 App 点「重新加载清单」。
// 图标四级回退：icon 字段 → exe 同目录 icon.png/logo.png/cover.png/*.ico → 同目录 exe 自动提取 → 渐变图+名字首字。
// 语言优先级：项 lang → defaultLang → zh_CN.UTF-8。玩日文游戏填 ja_JP.UTF-8。
// 盘符映射从 E: 开始，c: 与 d: 被引擎占用，请勿改写。
";
        }

        /// <summary>把 storageMode 枚举转回配置文本。</summary>
        /// <param name="mode">枚举值。</param>
        /// <returns>auto / direct / root 三者之一。</returns>
        private static string StorageModeText(StorageMode mode)
        {
            switch (mode)
            {
                case StorageMode.Direct: return "direct";
                case StorageMode.Root: return "root";
                default: return "auto";
            }
        }

        /// <summary>把全局配置字段应用到静态属性上。</summary>
        /// <param name="file">反序列化得到的顶层配置。</param>
        private static void ApplyGlobals(CatalogFile file)
        {
            Debug = file.Debug;
            AutoScrollLog = file.AutoScrollLog;
            DefaultLang = string.IsNullOrWhiteSpace(file.DefaultLang) ? "zh_CN.UTF-8" : file.DefaultLang.Trim();
            RecentLimit = file.RecentLimit > 0 ? file.RecentLimit : 20;
            IconScanLimit = file.IconScanLimit > 0 ? file.IconScanLimit : 5;

            var mode = (file.StorageMode ?? "auto").Trim().ToLowerInvariant();
            StorageAccess = mode == "direct" ? StorageMode.Direct
                          : mode == "root" ? StorageMode.Root
                          : StorageMode.Auto;
            FileAccess.Mode = StorageAccess;
        }

        /// <summary>清洗程序列表：丢弃 exe 为空的无效项。</summary>
        /// <param name="apps">原始列表。</param>
        /// <returns>清洗后的列表，永不为 null。</returns>
        private static List<AppEntry> SanitizeApps(List<AppEntry> apps)
        {
            var result = new List<AppEntry>();
            if (apps == null) return result;
            foreach (var a in apps)
            {
                if (a == null || !a.IsValid())
                {
                    Notify?.Invoke("[catalog] 跳过一条缺少 exe 的程序项");
                    continue;
                }
                result.Add(a);
            }
            return result;
        }

        /// <summary>清洗映射列表：丢弃 android 为空或盘符非法的项，并禁止占用 c:/d:。</summary>
        /// <param name="mounts">原始映射列表。</param>
        /// <returns>清洗后的列表，永不为 null。</returns>
        private static List<MountEntry> SanitizeMounts(List<MountEntry> mounts)
        {
            var result = new List<MountEntry>();
            if (mounts == null) return result;
            foreach (var m in mounts)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.AndroidPath)) continue;
                var letter = m.Letter();
                if (letter.Length != 1)
                {
                    Notify?.Invoke("[mounts] 跳过盘符非法的映射：" + (m.Drive ?? "空"));
                    continue;
                }
                // c: 是容器系统盘、d: 是共享存储根，改动会直接破坏引擎，必须拦住
                if (letter == "c" || letter == "d")
                {
                    Notify?.Invoke("[mounts] 跳过受保护盘符 " + letter + ":(c/d 由引擎占用)");
                    continue;
                }
                result.Add(m);
            }
            return result;
        }

        /// <summary>生成默认示例程序列表（用于首次运行时产出可见内容）。</summary>
        /// <returns>示例程序列表。</returns>
        private static List<AppEntry> DefaultApps()
        {
            return new List<AppEntry>
            {
                new AppEntry { Name = "示例游戏", Exe = "D:\\Games\\Sample\\sample.exe" }
            };
        }

        /// <summary>读取文本文件内容（走 FileAccess，兼容直读与 root 两条通道）。</summary>
        /// <param name="path">目标文件路径。</param>
        /// <returns>文本内容；读不到时返回 null。</returns>
        private static async Task<string> ReadTextAsync(string path)
        {
            var bytes = await Task.Run(() => FileAccess.ReadAllBytes(path));
            if (bytes == null || bytes.Length == 0) return null;
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
