using System;
using System.Collections.Generic;
using System.Text;
using Android.Graphics;

namespace WinPlayInstaller
{
    /// <summary>
    /// 从 PE 文件（exe/dll）的资源节中提取图标，并提供同目录候选扫描。
    /// 一期只支持 PNG 编码的 RT_ICON（Vista 之后的 256×256 图标基本都是这种），
    /// 遇到老式 DIB 编码直接返回 null，由调用方回退到下一级方案。
    /// </summary>
    public static class IconExtractor
    {
        /// <summary>资源类型：图标数据。</summary>
        private const int RtIcon = 3;

        /// <summary>资源类型：图标目录（记录每个尺寸图标对应哪个 RT_ICON）。</summary>
        private const int RtGroupIcon = 14;

        /// <summary>单次从磁盘读取资源节的上限，避免异常 PE 声明巨大尺寸导致内存暴涨。</summary>
        private const int MaxResourceBytes = 8 * 1024 * 1024;

        /// <summary>自动扫描同目录 exe 时需要排除的文件名片段（卸载器、安装器、运行库等通常无图标或图标无意义）。</summary>
        private static readonly string[] ExcludedFragments =
        {
            "unins", "setup", "install", "vcredist", "dxsetup", "crashpad", "crashreport", "report"
        };

        /// <summary>优先尝试的文件名关键字：带这些词的程序更可能是游戏主程序或启动器。</summary>
        private static readonly string[] PreferredFragments =
        {
            "launcher", "game", "start", "play", "main"
        };

        /// <summary>从 PE 文件中提取尺寸最大的 PNG 编码图标。</summary>
        /// <param name="peUnixPath">PE 文件的 Android 路径（exe 或 dll）。</param>
        /// <returns>解码成功返回位图；文件不合法、无资源节、或图标为老式 DIB 编码时返回 null。</returns>
        public static Bitmap ExtractPngIcon(string peUnixPath)
        {
            if (string.IsNullOrWhiteSpace(peUnixPath)) return null;

            try
            {
                // 1) DOS 头：0x3C 处是 PE 头偏移
                var dosHead = FileAccess.ReadRange(peUnixPath, 0, 64);
                if (dosHead == null || dosHead.Length < 64) return null;
                int peOffset = BitConverter.ToInt32(dosHead, 0x3C);
                if (peOffset <= 0 || peOffset > 0x1000) return null;

                // 2) PE 签名 + COFF 头（节数与可选头大小决定后续读取范围）
                var peHead = FileAccess.ReadRange(peUnixPath, peOffset, 24);
                if (peHead == null || peHead.Length < 24) return null;
                if (peHead[0] != 'P' || peHead[1] != 'E' || peHead[2] != 0 || peHead[3] != 0) return null;
                int sectionCount = BitConverter.ToUInt16(peHead, 6);
                int optionalHeaderSize = BitConverter.ToUInt16(peHead, 20);
                if (sectionCount <= 0 || sectionCount > 96 || optionalHeaderSize <= 0) return null;

                // 3) 可选头：区分 PE32(x86) 与 PE32+，两者 DataDirectory 起始偏移不同
                int optionalStart = peOffset + 24;
                var optional = FileAccess.ReadRange(peUnixPath, optionalStart, Math.Max(optionalHeaderSize, 128));
                if (optional == null || optional.Length < 120) return null;
                ushort magic = BitConverter.ToUInt16(optional, 0);
                int dataDirStart = magic == 0x20B ? 112 : 96;      // 0x20B = PE32+，其余按 PE32 处理
                if (optional.Length < dataDirStart + 16) return null;
                uint resourceRva = BitConverter.ToUInt32(optional, dataDirStart + 2 * 8);   // DataDirectory[2] = 资源表
                uint resourceSize = BitConverter.ToUInt32(optional, dataDirStart + 2 * 8 + 4);
                if (resourceRva == 0 || resourceSize == 0) return null;

                // 4) 节表：找到包含资源 RVA 的节，换算成文件偏移
                var sections = FileAccess.ReadRange(peUnixPath, optionalStart + optionalHeaderSize, sectionCount * 40);
                if (sections == null || sections.Length < sectionCount * 40) return null;

                long resourceFileOffset = -1;
                uint resourceVaddr = 0;
                int resourceRawSize = 0;
                for (int i = 0; i < sectionCount; i++)
                {
                    int b = i * 40;
                    uint virtualSize = BitConverter.ToUInt32(sections, b + 8);
                    uint virtualAddress = BitConverter.ToUInt32(sections, b + 12);
                    uint rawSize = BitConverter.ToUInt32(sections, b + 16);
                    uint rawPointer = BitConverter.ToUInt32(sections, b + 20);
                    uint span = Math.Max(virtualSize, rawSize);
                    if (resourceRva >= virtualAddress && resourceRva < virtualAddress + span)
                    {
                        resourceVaddr = virtualAddress;
                        resourceFileOffset = (long)(resourceRva - virtualAddress) + rawPointer;
                        resourceRawSize = (int)Math.Min(rawSize, (uint)MaxResourceBytes);
                        break;
                    }
                }
                if (resourceFileOffset < 0 || resourceRawSize <= 0) return null;

                // 5) 整块读入资源节，在内存里做三层目录解析（层级小，一次读比多次 root 往返快得多）
                var resource = FileAccess.ReadRange(peUnixPath, resourceFileOffset, resourceRawSize);
                if (resource == null || resource.Length < 16) return null;

                int groupOffset, groupSize;
                if (!ReadResourceData(resource, resourceVaddr, RtGroupIcon, -1, out groupOffset, out groupSize)) return null;
                var group = Slice(resource, groupOffset, groupSize);
                if (group == null || group.Length < 6) return null;

                // GRPICONDIR: Reserved(2) Type(2) Count(2)，随后是 Count 个 14 字节条目
                int iconCount = BitConverter.ToUInt16(group, 4);
                if (iconCount <= 0 || group.Length < 6 + iconCount * 14) return null;

                int bestId = -1;
                int bestArea = -1;
                for (int i = 0; i < iconCount; i++)
                {
                    int b = 6 + i * 14;
                    int width = group[b]; if (width == 0) width = 256;      // 0 表示 256
                    int height = group[b + 1]; if (height == 0) height = 256;
                    int id = BitConverter.ToUInt16(group, b + 12);
                    int area = width * height;
                    if (area > bestArea) { bestArea = area; bestId = id; }
                }
                if (bestId < 0) return null;

                int iconOffset, iconSize;
                if (!ReadResourceData(resource, resourceVaddr, RtIcon, bestId, out iconOffset, out iconSize)) return null;
                var iconData = Slice(resource, iconOffset, iconSize);
                if (iconData == null || iconData.Length < 8) return null;

                // 一期只吃 PNG 编码：老式 DIB 需要额外补 BITMAPINFOHEADER 并跳过 AND 掩码，留待二期
                if (!IsPng(iconData)) return null;
                return BitmapFactory.DecodeByteArray(iconData, 0, iconData.Length);
            }
            catch (Exception ex)
            {
                AppCatalog.Notify?.Invoke("[icon] 提取失败（" + peUnixPath + "）：" + ex.Message);
                return null;
            }
        }

        /// <summary>直接读取图片文件（png/jpg/webp）。</summary>
        /// <param name="unixPath">图片文件的 Android 路径。</param>
        /// <returns>解码成功返回位图，否则返回 null。</returns>
        public static Bitmap LoadImageFile(string unixPath)
        {
            if (string.IsNullOrWhiteSpace(unixPath)) return null;
            try
            {
                var bytes = FileAccess.ReadAllBytes(unixPath);
                if (bytes == null || bytes.Length == 0) return null;
                return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                AppCatalog.Notify?.Invoke("[icon] 图片解码失败（" + unixPath + "）：" + ex.Message);
                return null;
            }
        }

        /// <summary>在 exe 同目录查找常见图标图片（icon.png / logo.png / cover.png）。</summary>
        /// <param name="exeUnixPath">exe 的 Android 路径。</param>
        /// <returns>找到并解码成功返回位图，否则返回 null。</returns>
        public static Bitmap FindDirImage(string exeUnixPath)
        {
            var dir = AppEntry.ExeDirOf(exeUnixPath);
            if (string.IsNullOrWhiteSpace(dir)) return null;

            foreach (var name in new[] { "icon.png", "logo.png", "cover.png" })
            {
                var bmp = LoadImageFile(dir.TrimEnd('/') + "/" + name);
                if (bmp != null) return bmp;
            }
            return null;
        }

        /// <summary>扫描 exe 同目录下的其它 exe，尝试从中提取图标（排除 exe 自身）。</summary>
        /// <param name="exeUnixPath">当前清单项的 exe 路径。</param>
        /// <param name="maxTry">最多尝试的候选个数，取自清单的 iconScanLimit。</param>
        /// <returns>第一个提取成功的图标；全部失败返回 null。</returns>
        public static Bitmap ScanSiblings(string exeUnixPath, int maxTry)
        {
            if (string.IsNullOrWhiteSpace(exeUnixPath)) return null;
            var dir = AppEntry.ExeDirOf(exeUnixPath);
            if (string.IsNullOrWhiteSpace(dir)) return null;

            try
            {
                var files = FileAccess.List(dir, "*.exe");
                if (files == null || files.Length == 0) return null;

                var self = AppEntry.Normalize(exeUnixPath);
                var preferred = new List<string>();
                var others = new List<string>();

                foreach (var f in files)
                {
                    var name = AppEntry.LastSegment(f).ToLowerInvariant();
                    if (AppEntry.Normalize(f) == self) continue;            // 自身已在上层试过，跳过
                    if (IsExcluded(name)) continue;

                    var isPreferred = false;
                    foreach (var key in PreferredFragments)
                    {
                        if (name.IndexOf(key, StringComparison.Ordinal) >= 0) { isPreferred = true; break; }
                    }
                    if (isPreferred) preferred.Add(f); else others.Add(f);
                }

                var limit = maxTry > 0 ? maxTry : 5;
                var tried = 0;
                foreach (var f in preferred)
                {
                    if (tried >= limit) return null;
                    tried++;
                    if (AppCatalog.Debug) AppCatalog.Notify?.Invoke("[icon] 尝试提取：" + AppEntry.LastSegment(f));
                    var bmp = ExtractPngIcon(f);
                    if (bmp != null) return bmp;
                }
                foreach (var f in others)
                {
                    if (tried >= limit) return null;
                    tried++;
                    if (AppCatalog.Debug) AppCatalog.Notify?.Invoke("[icon] 尝试提取：" + AppEntry.LastSegment(f));
                    var bmp = ExtractPngIcon(f);
                    if (bmp != null) return bmp;
                }
            }
            catch (Exception ex)
            {
                AppCatalog.Notify?.Invoke("[icon] 扫描同目录失败：" + ex.Message);
            }
            return null;
        }

        /// <summary>判断文件名是否应排除在图标扫描之外。</summary>
        /// <param name="lowerName">小写文件名。</param>
        /// <returns>需要排除返回 true。</returns>
        private static bool IsExcluded(string lowerName)
        {
            foreach (var frag in ExcludedFragments)
            {
                if (lowerName.IndexOf(frag, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>判断字节流是否以 PNG 签名开头。</summary>
        /// <param name="data">待检查数据。</param>
        /// <returns>是 PNG 返回 true。</returns>
        private static bool IsPng(byte[] data)
        {
            return data != null && data.Length >= 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
        }

        /// <summary>安全截取字节数组的一部分。</summary>
        /// <param name="source">源数组。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">长度。</param>
        /// <returns>截取结果；越界时返回 null。</returns>
        private static byte[] Slice(byte[] source, int offset, int count)
        {
            if (source == null || offset < 0 || count <= 0 || offset + count > source.Length) return null;
            var result = new byte[count];
            Array.Copy(source, offset, result, 0, count);
            return result;
        }

        /// <summary>
        /// 按「资源类型 → 图标 ID（-1 表示取第一个子项）→ 第一个语言」三层目录定位数据项，并取出数据区间。
        /// 偏移量均相对于资源节起始位置换算，避免 RVA 与节起始不一致时读错位置。
        /// </summary>
        /// <param name="resource">已读入内存的资源节数据。</param>
        /// <param name="resourceVaddr">资源节的虚拟地址（用于把数据项里的 RVA 换成节内偏移）。</param>
        /// <param name="type">资源类型（RT_ICON / RT_GROUP_ICON）。</param>
        /// <param name="nameId">资源 ID；传 -1 表示取该类型下的第一项。</param>
        /// <param name="offset">输出：数据在 resource 数组中的起始下标。</param>
        /// <param name="size">输出：数据长度。</param>
        /// <returns>定位成功返回 true。</returns>
        private static bool ReadResourceData(byte[] resource, uint resourceVaddr, int type, int nameId,
            out int offset, out int size)
        {
            offset = 0;
            size = 0;

            int typeDir;
            bool isDir;
            if (!TryGetEntry(resource, 0, type, out typeDir, out isDir) || !isDir) return false;

            int nameDir;
            if (nameId < 0)
            {
                if (!TryGetFirst(resource, typeDir, out nameDir, out isDir) || !isDir) return false;
            }
            else
            {
                if (!TryGetEntry(resource, typeDir, nameId, out nameDir, out isDir) || !isDir) return false;
            }

            int dataEntry;
            if (!TryGetFirst(resource, nameDir, out dataEntry, out isDir) || isDir) return false;

            // IMAGE_RESOURCE_DATA_ENTRY：DataRVA(4) Size(4) CodePage(4) Reserved(4)
            if (dataEntry < 0 || dataEntry + 16 > resource.Length) return false;
            uint dataRva = BitConverter.ToUInt32(resource, dataEntry);
            uint dataSize = BitConverter.ToUInt32(resource, dataEntry + 4);
            if (dataSize == 0 || dataSize > MaxResourceBytes) return false;

            var relative = (long)dataRva - resourceVaddr;
            if (relative < 0 || relative + dataSize > resource.Length) return false;

            offset = (int)relative;
            size = (int)dataSize;
            return true;
        }

        /// <summary>在指定资源目录中按数字 ID 查找条目。</summary>
        /// <param name="resource">资源节数据。</param>
        /// <param name="dirOffset">目录节点头在数组中的下标。</param>
        /// <param name="key">要匹配的数字 ID。</param>
        /// <param name="offset">输出：目标偏移（子目录偏移或数据项偏移，取决于 isDir）。</param>
        /// <param name="isDir">输出：true 表示指向子目录，false 表示指向数据项。</param>
        /// <returns>找到返回 true。</returns>
        private static bool TryGetEntry(byte[] resource, int dirOffset, int key, out int offset, out bool isDir)
        {
            offset = 0;
            isDir = false;
            if (dirOffset < 0 || dirOffset + 16 > resource.Length) return false;

            int namedCount = BitConverter.ToUInt16(resource, dirOffset + 12);
            int idCount = BitConverter.ToUInt16(resource, dirOffset + 14);
            int count = namedCount + idCount;
            int entriesStart = dirOffset + 16;
            if (count <= 0 || entriesStart + count * 8 > resource.Length) return false;

            for (int i = 0; i < count; i++)
            {
                int e = entriesStart + i * 8;
                uint nameOrId = BitConverter.ToUInt32(resource, e);
                if ((nameOrId & 0x80000000) != 0) continue;      // 字符串命名的条目不参与数字匹配
                if ((int)nameOrId != key) continue;

                uint raw = BitConverter.ToUInt32(resource, e + 4);
                isDir = (raw & 0x80000000) != 0;                  // 高位为 1 表示子目录
                offset = (int)(raw & 0x7FFFFFFF);
                return true;
            }
            return false;
        }

        /// <summary>取指定资源目录中的第一项，用于「语言」这种无需关心具体值的层级。</summary>
        /// <param name="resource">资源节数据。</param>
        /// <param name="dirOffset">目录节点头在数组中的下标。</param>
        /// <param name="offset">输出：目标偏移。</param>
        /// <param name="isDir">输出：true 表示指向子目录。</param>
        /// <returns>存在条目返回 true。</returns>
        private static bool TryGetFirst(byte[] resource, int dirOffset, out int offset, out bool isDir)
        {
            offset = 0;
            isDir = false;
            if (dirOffset < 0 || dirOffset + 16 > resource.Length) return false;

            int namedCount = BitConverter.ToUInt16(resource, dirOffset + 12);
            int idCount = BitConverter.ToUInt16(resource, dirOffset + 14);
            if (namedCount + idCount <= 0) return false;

            int entriesStart = dirOffset + 16;
            if (entriesStart + 8 > resource.Length) return false;

            // 优先挑数字 ID 项：语言层几乎都是数字，字符串名多为自定义资源，取到的概率更低
            for (int i = 0; i < namedCount + idCount; i++)
            {
                int e = entriesStart + i * 8;
                if (e + 8 > resource.Length) return false;
                uint nameOrId = BitConverter.ToUInt32(resource, e);
                if ((nameOrId & 0x80000000) != 0) continue;

                uint raw = BitConverter.ToUInt32(resource, e + 4);
                isDir = (raw & 0x80000000) != 0;
                offset = (int)(raw & 0x7FFFFFFF);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 图标缓存：内存一层 + 磁盘一层（/sdcard/WinPlay/icons/cache/&lt;hash&gt;.png）。
    /// 磁盘缓存用于避免每次进入清单页都为同一批 exe 重复解析 PE（单次提取需 5~8 次 root 往返）。
    /// </summary>
    public static class IconCache
    {
        /// <summary>内存缓存，键为规范化后的 exe 路径。</summary>
        private static readonly Dictionary<string, Bitmap> Memory = new Dictionary<string, Bitmap>();

        /// <summary>磁盘缓存目录。</summary>
        private static string CacheDir => AppCatalog.PreferDir + "/icons/cache";

        /// <summary>尝试从缓存取图标。</summary>
        /// <param name="exePath">exe 路径（用作缓存键）。</param>
        /// <returns>命中返回位图，未命中返回 null。</returns>
        public static Bitmap TryGet(string exePath)
        {
            var key = AppEntry.Normalize(exePath);
            if (key.Length == 0) return null;

            if (Memory.TryGetValue(key, out var cached) && cached != null) return cached;

            try
            {
                var bytes = FileAccess.ReadAllBytes(CachePath(key));
                if (bytes == null || bytes.Length == 0) return null;
                var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bmp != null) Memory[key] = bmp;
                return bmp;
            }
            catch (Exception ex)
            {
                AppCatalog.Notify?.Invoke("[icon] 读缓存失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>把图标写入内存与磁盘缓存。</summary>
        /// <param name="exePath">exe 路径（用作缓存键）。</param>
        /// <param name="bitmap">要缓存的位图；为 null 时忽略。</param>
        public static void Put(string exePath, Bitmap bitmap)
        {
            if (bitmap == null) return;
            var key = AppEntry.Normalize(exePath);
            if (key.Length == 0) return;

            Memory[key] = bitmap;

            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    // PNG 无损且体积小，便于用户直接打开查看缓存命中情况
                    bitmap.Compress(Bitmap.CompressFormat.Png, 100, ms);
                    FileAccess.WriteAllBytes(CachePath(key), ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                AppCatalog.Notify?.Invoke("[icon] 写缓存失败：" + ex.Message);
            }
        }

        /// <summary>计算某个 exe 对应的磁盘缓存路径。</summary>
        /// <param name="normalizedKey">规范化后的 exe 路径。</param>
        /// <returns>缓存文件完整路径。</returns>
        private static string CachePath(string normalizedKey) => CacheDir + "/" + Hash(normalizedKey) + ".png";

        /// <summary>取字符串的稳定短哈希（SHA1 前 8 字节），用作缓存文件名。</summary>
        /// <param name="text">输入文本。</param>
        /// <returns>16 位十六进制字符串。</returns>
        private static string Hash(string text)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
