using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WinPlayInstaller
{
    /// <summary>扩展存储访问模式，决定文件读取走「直读」还是「root su」通道。</summary>
    public enum StorageMode
    {
        /// <summary>按当前权限自动选择：已获「所有文件访问」则直读，否则回退 root。</summary>
        Auto = 0,

        /// <summary>强制直读。要求已授予 MANAGE_EXTERNAL_STORAGE，否则读取会失败。</summary>
        Direct = 1,

        /// <summary>强制走 root su 通道。读取引擎容器等跨应用私有目录时必须使用它。</summary>
        Root = 2
    }

    /// <summary>
    /// 统一文件访问入口：在「直读」与「root su」两条通道之间自动切换，调用方无需感知走哪条路。
    /// 直读依赖 MANAGE_EXTERNAL_STORAGE 运行时权限；root 通道依赖设备 su 与 <see cref="RootShell"/>。
    /// 注意：本类所有方法都是同步阻塞实现（内部等待 root 命令），调用方必须自行放到后台线程，
    /// 严禁在 UI 线程直接调用，否则会卡界面。
    /// </summary>
    public static class FileAccess
    {
        /// <summary>当前访问模式，由 apps.json 的 storageMode 决定，默认自动。</summary>
        public static StorageMode Mode { get; set; } = StorageMode.Auto;

        /// <summary>日志回调，由 MainActivity 注入；为 null 时静默处理，不会抛异常。</summary>
        public static Action<string> Notify { get; set; }

        /// <summary>权限检查结果的有效时长。设置页授权返回后 5 秒内即可感知，又不至于每次读文件都查系统服务。</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>上次检查「所有文件访问」权限的时间戳。</summary>
        private static DateTime _checkedAt = DateTime.MinValue;

        /// <summary>上一次权限检查的结果缓存。</summary>
        private static bool _hasAllFiles;

        /// <summary>本进程是否已获得「所有文件访问」（MANAGE_EXTERNAL_STORAGE 运行时开关）。</summary>
        public static bool HasAllFiles
        {
            get
            {
                if (DateTime.Now - _checkedAt < CheckInterval) return _hasAllFiles;
                _checkedAt = DateTime.Now;
                try
                {
                    var ctx = Android.App.Application.Context;
                    var appOps = (Android.App.AppOpsManager)ctx.GetSystemService(Android.Content.Context.AppOpsService);
                    if (appOps == null)
                    {
                        _hasAllFiles = false;
                        return false;
                    }

                    // 直接用字符串常量而不用绑定字段名，避免不同版本绑定命名差异导致编译失败
                    var opMode = appOps.CheckOpNoThrow(
                        "android:manage_external_storage",
                        Android.OS.Process.MyUid(), ctx.PackageName);
                    _hasAllFiles = (int)opMode == 0;   // 0 == MODE_ALLOWED
                }
                catch (Exception ex)
                {
                    Notify?.Invoke("[file] 权限检查异常，按未授权处理：" + ex.Message);
                    _hasAllFiles = false;
                }
                return _hasAllFiles;
            }
        }

        /// <summary>当前实际是否走直读通道（由模式与权限共同决定）。</summary>
        public static bool UsingDirect
        {
            get
            {
                switch (Mode)
                {
                    case StorageMode.Direct: return true;    // 强制直读，失败由调用方从 null 返回值得知
                    case StorageMode.Root: return false;
                    default: return HasAllFiles;
                }
            }
        }

        /// <summary>读取整个文件内容。</summary>
        /// <param name="path">目标文件的绝对路径（Android 路径）。</param>
        /// <returns>文件字节；不可读或不存在时返回 null。</returns>
        public static byte[] ReadAllBytes(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (UsingDirect)
            {
                try { return File.ReadAllBytes(path); }
                catch (Exception ex) { Notify?.Invoke("[file] 直读失败，改用 root：" + ex.Message); }
            }

            // root 通道必须用 base64 传输：若用 cat 直接输出，二进制内容会被终端编码破坏
            var r = Run("base64 \"" + Escape(path) + "\" 2>/dev/null");
            if (r == null || !r.Ok || string.IsNullOrEmpty(r.Output)) return null;
            try
            {
                return Convert.FromBase64String(r.Output.Replace("\n", "").Replace("\r", "").Trim());
            }
            catch (Exception ex)
            {
                Notify?.Invoke("[file] base64 解码失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>按区间读取文件的一部分字节，用于解析 PE 文件时避免把几百 MB 的 exe 整块读入。</summary>
        /// <param name="path">目标文件的绝对路径（Android 路径）。</param>
        /// <param name="offset">起始偏移（字节，0 基）。</param>
        /// <param name="count">要读取的字节数。</param>
        /// <returns>读到的字节；越界或读失败时返回 null。</returns>
        public static byte[] ReadRange(string path, long offset, int count)
        {
            if (string.IsNullOrWhiteSpace(path) || count <= 0 || offset < 0) return null;

            if (UsingDirect)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (offset >= fs.Length) return null;
                        fs.Seek(offset, SeekOrigin.Begin);
                        var available = fs.Length - offset;
                        var want = (int)Math.Min(count, Math.Min(int.MaxValue, available));
                        if (want <= 0) return null;
                        var buf = new byte[want];
                        int read = 0;
                        while (read < want)
                        {
                            int n = fs.Read(buf, read, want - read);
                            if (n <= 0) break;      // 文件被截断时提前退出
                            read += n;
                        }
                        if (read <= 0) return null;
                        if (read == want) return buf;
                        var trimmed = new byte[read];
                        Array.Copy(buf, trimmed, read);
                        return trimmed;
                    }
                }
                catch (Exception ex) { Notify?.Invoke("[file] 区间直读失败，改用 root：" + ex.Message); }
            }

            // tail -c +N 是 1 基偏移，配合 head -c 精确取区间，比 dd bs=1 逐字节快得多
            var r = Run("tail -c +" + (offset + 1) + " \"" + Escape(path) + "\" 2>/dev/null | head -c " + count + " | base64");
            if (r == null || !r.Ok || string.IsNullOrEmpty(r.Output)) return null;
            try
            {
                return Convert.FromBase64String(r.Output.Replace("\n", "").Replace("\r", "").Trim());
            }
            catch (Exception ex)
            {
                Notify?.Invoke("[file] 区间 base64 解码失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>判断文件或目录是否存在。</summary>
        /// <param name="path">目标路径（Android 路径）。</param>
        /// <returns>存在返回 true；不存在或不可判定返回 false。</returns>
        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (UsingDirect)
            {
                try { return File.Exists(path) || Directory.Exists(path); }
                catch (Exception ex) { Notify?.Invoke("[file] 直读判断存在失败，改用 root：" + ex.Message); }
            }

            var r = Run("[ -e \"" + Escape(path) + "\" ] && echo 1 || echo 0");
            return r != null && r.Ok && r.Output != null && r.Output.Trim() == "1";
        }

        /// <summary>列出目录下匹配的文件（不递归）。</summary>
        /// <param name="path">目标目录（Android 路径）。</param>
        /// <param name="pattern">文件名模式，如 "*.exe"；为空表示全部。</param>
        /// <returns>匹配到的完整路径数组；失败返回空数组。</returns>
        public static string[] List(string path, string pattern)
        {
            if (string.IsNullOrWhiteSpace(path)) return new string[0];

            if (UsingDirect)
            {
                try { return Directory.GetFiles(path, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern); }
                catch (Exception ex) { Notify?.Invoke("[file] 直读列目录失败，改用 root：" + ex.Message); }
            }

            var r = Run("ls -1 \"" + Escape(path) + "\" 2>/dev/null");
            if (r == null || !r.Ok || string.IsNullOrEmpty(r.Output)) return new string[0];

            var suffix = (pattern ?? "").Replace("*", "").ToLowerInvariant();
            var lines = r.Output.Split('\n');
            var result = new System.Collections.Generic.List<string>();
            foreach (var raw in lines)
            {
                var name = raw.Trim().TrimEnd('\r');
                if (name.Length == 0) continue;
                if (suffix.Length > 0 && !name.ToLowerInvariant().EndsWith(suffix)) continue;
                result.Add(path.TrimEnd('/') + "/" + name);
            }
            return result.ToArray();
        }

        /// <summary>写入文本文件（先写 .tmp 再替换，避免写一半断电产生坏文件）。</summary>
        /// <param name="path">目标文件（Android 路径）。</param>
        /// <param name="text">要写入的文本，按 UTF-8 无 BOM 编码。</param>
        /// <returns>写入成功返回 true。</returns>
        public static bool WriteAllText(string path, string text)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var utf8 = new UTF8Encoding(false);

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text ?? "", utf8);
                File.Move(tmp, path, true);        // 覆盖式移动，保证读写方永远看到完整文件
                return true;
            }
            catch (Exception ex) { Notify?.Invoke("[file] 直写失败，改用 root：" + ex.Message); }

            // root 兜底：heredoc 写入。定界符带随机串，避免与文件内容意外相同导致截断
            var marker = "WPTEXT_" + Guid.NewGuid().ToString("N");
            var cmd = "cat > \"" + Escape(path) + "\" << '" + marker + "'\n" + (text ?? "") + "\n" + marker;
            var r = Run(cmd);
            if (r == null || !r.Ok)
            {
                Notify?.Invoke("[file] root 写入失败：" + (r == null ? "命令无返回" : r.Error));
                return false;
            }
            return true;
        }

        /// <summary>写入二进制文件（先写 .tmp 再替换，避免写一半断电产生坏文件）。</summary>
        /// <param name="path">目标文件（Android 路径）。</param>
        /// <param name="data">要写入的字节；为 null 时按空内容处理。</param>
        /// <returns>写入成功返回 true。</returns>
        public static bool WriteAllBytes(string path, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var payload = data ?? new byte[0];

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, payload);
                File.Move(tmp, path, true);        // 覆盖式移动，保证读取方永远看到完整文件
                return true;
            }
            catch (Exception ex) { Notify?.Invoke("[file] 直写二进制失败，改用 root：" + ex.Message); }

            // root 兜底：base64 解码写回。大图标（数十 KB）的 base64 文本较长，
            // 但走 heredoc 由 stdin 传入，不受单条命令参数长度限制的常见约束影响。
            var marker = "WPBIN_" + Guid.NewGuid().ToString("N");
            var cmd = "base64 -d > \"" + Escape(path) + "\" << '" + marker + "'\n"
                      + Convert.ToBase64String(payload) + "\n" + marker;
            var r = Run(cmd);
            if (r == null || !r.Ok)
            {
                Notify?.Invoke("[file] root 写入二进制失败：" + (r == null ? "命令无返回" : r.Error));
                return false;
            }
            return true;
        }

        /// <summary>把路径中的双引号转义，防止拼进 shell 命令时被截断。</summary>
        /// <param name="path">原始路径。</param>
        /// <returns>可用于双引号包裹的路径字符串。</returns>
        private static string Escape(string path) => (path ?? "").Replace("\"", "\\\"");

        /// <summary>同步执行一条 root 命令。</summary>
        /// <param name="command">要执行的 shell 命令。</param>
        /// <returns>执行结果；异常时返回 null。</returns>
        private static RootShell.Result Run(string command)
        {
            try
            {
                return RootShell.ExecAsync(command).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Notify?.Invoke("[file] root 命令异常：" + ex.Message);
                return null;
            }
        }
    }
}
