using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WinPlayInstaller
{
    /// <summary>最近使用的一条记录。时间存 ISO8601 往返格式字符串，避免 DateTime 序列化差异。</summary>
    public sealed class RecentEntry
    {
        /// <summary>显示名（启动当时的名字，后续改清单不会回改历史记录）。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>主程序路径，同时作为去重键的来源。</summary>
        [JsonPropertyName("exe")]
        public string Exe { get; set; }

        /// <summary>启动参数。</summary>
        [JsonPropertyName("args")]
        public string Args { get; set; }

        /// <summary>工作目录（可为空，为空时启动时按 exe 所在目录重新推算）。</summary>
        [JsonPropertyName("workdir")]
        public string WorkDir { get; set; }

        /// <summary>语言设置。</summary>
        [JsonPropertyName("lang")]
        public string Lang { get; set; }

        /// <summary>最后使用时间，ISO8601 往返格式（DateTime.ToString("O")）。</summary>
        [JsonPropertyName("lastUsed")]
        public string LastUsed { get; set; }

        /// <summary>转换成可再次启动的清单项。</summary>
        /// <returns>等价于本次记录的 <see cref="AppEntry"/>。</returns>
        public AppEntry ToAppEntry()
        {
            return new AppEntry
            {
                Name = Name,
                Exe = Exe,
                Args = Args,
                WorkDir = WorkDir,
                Lang = Lang
            };
        }
    }

    /// <summary>
    /// 「最近使用」列表的读写层。纯数据、不依赖任何 Activity，失败只上报不抛异常。
    /// 去重键为规范化后的 exe 路径（同一程序再次启动只置顶不新增）。
    /// </summary>
    public static class RecentStore
    {
        /// <summary>默认保留条数上限（清单里 recentLimit 未配置时使用）。</summary>
        public const int MaxItems = 20;

        /// <summary>记录文件名。</summary>
        private const string FileName = "recent.json";

        /// <summary>日志回调，由 MainActivity 注入；为 null 时静默。</summary>
        public static Action<string> Notify { get; set; }

        /// <summary>列表变化通知，界面订阅后刷新列表。</summary>
        public static event Action Changed;

        /// <summary>内存缓存，切换 tab 时不必重复读盘。</summary>
        public static List<RecentEntry> Cached { get; } = new List<RecentEntry>();

        /// <summary>实际使用的记录文件路径（首选 /sdcard/WinPlay，写入失败时自动切私有目录）。</summary>
        public static string RecentPath { get; private set; } = AppCatalog.PreferDir + "/" + FileName;

        /// <summary>是否已完成首次加载，避免每次刷新都读盘。</summary>
        private static bool _loaded;

        /// <summary>私有目录下的记录文件路径（首选目录不可写时的回退位置）。</summary>
        private static string PrivatePath
        {
            get
            {
                try { return Path.Combine(Android.App.Application.Context.FilesDir.AbsolutePath, FileName); }
                catch { return FileName; }
            }
        }

        /// <summary>读取最近使用列表；文件不存在或解析失败时返回空列表。</summary>
        /// <returns>按时间倒序的记录列表，永不为 null。</returns>
        public static async Task<List<RecentEntry>> LoadAsync()
        {
            if (_loaded) return Cached;

            var text = await Task.Run(() =>
            {
                var bytes = FileAccess.ReadAllBytes(RecentPath);
                if (bytes == null || bytes.Length == 0)
                {
                    // 首选路径读不到时再试私有目录（首次运行或用户已清过 SD 卡时常见）
                    bytes = FileAccess.ReadAllBytes(PrivatePath);
                    if (bytes != null && bytes.Length > 0) RecentPath = PrivatePath;
                }
                return bytes == null || bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
            });

            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<RecentEntry>>(text, AppCatalog.JsonOptions);
                    if (list != null)
                    {
                        Cached.Clear();
                        Cached.AddRange(list);
                    }
                }
                catch (JsonException ex)
                {
                    Notify?.Invoke("[recent] 解析失败：" + ex.Message + "；按空列表处理");
                }
            }

            _loaded = true;
            return Cached;
        }

        /// <summary>记录一次使用：按规范化 exe 去重、置顶、按上限截断并落盘。</summary>
        /// <param name="entry">本次启动的清单项；exe 为空时直接忽略。</param>
        public static async Task TouchAsync(AppEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Exe)) return;

            await LoadAsync();

            var key = Key(entry.Exe);
            Cached.RemoveAll(x => x != null && Key(x.Exe) == key);
            Cached.Insert(0, new RecentEntry
            {
                Name = entry.DisplayName,
                Exe = entry.Exe,
                Args = entry.Args,
                WorkDir = entry.WorkDir,
                Lang = entry.Lang,
                LastUsed = DateTime.Now.ToString("O")
            });

            var limit = AppCatalog.RecentLimit > 0 ? AppCatalog.RecentLimit : MaxItems;
            if (Cached.Count > limit) Cached.RemoveRange(limit, Cached.Count - limit);

            await SaveAsync();
            RaiseChanged();
        }

        /// <summary>按 exe 路径移除一条记录并落盘。</summary>
        /// <param name="exe">要移除的程序路径。</param>
        public static async Task RemoveAsync(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;

            await LoadAsync();

            var key = Key(exe);
            var removed = Cached.RemoveAll(x => x != null && Key(x.Exe) == key);
            if (removed <= 0) return;

            await SaveAsync();
            RaiseChanged();
        }

        /// <summary>生成去重键（与 <see cref="AppEntry.NormalizedExe"/> 同一实现，避免两套规则不一致）。</summary>
        /// <param name="exe">程序路径。</param>
        /// <returns>规范化后的键。</returns>
        internal static string Key(string exe) => AppEntry.Normalize(exe);

        /// <summary>把当前缓存写入磁盘；首选路径失败时自动改用私有目录重试一次。</summary>
        /// <returns>写入成功返回 true。</returns>
        private static async Task<bool> SaveAsync()
        {
            var json = JsonSerializer.Serialize(Cached, AppCatalog.JsonOptions);

            var ok = await Task.Run(() => FileAccess.WriteAllText(RecentPath, json));
            if (!ok && RecentPath != PrivatePath)
            {
                // /sdcard 目录不可写（无 root 或未授权）时退到应用私有目录，保证功能不中断
                var fallback = PrivatePath;
                ok = await Task.Run(() => FileAccess.WriteAllText(fallback, json));
                if (ok) RecentPath = fallback;
            }

            if (!ok) Notify?.Invoke("[recent] 保存失败：" + RecentPath);
            return ok;
        }

        /// <summary>触发列表变化通知，订阅方异常不影响数据层。</summary>
        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Notify?.Invoke("[recent] 刷新回调异常：" + ex.Message); }
        }
    }
}
