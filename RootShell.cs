using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WinPlayInstaller
{
    /// <summary>通过 SukiSU/KernelSU 执行 root 命令。首次调用前需在 SukiSU 超级用户页授权本应用。</summary>
    public static class RootShell
    {
        // 全局日志接收器（由 MainActivity.LogStore 填充）
        public static Action<string> OnLog;

        static void Log(string msg)
        {
            try { OnLog?.Invoke(msg); } catch { }
        }

        public sealed class Result
        {
            public int ExitCode = -1;
            public string Output = "";
            public string Error = "";
            public bool Ok => ExitCode == 0;
        }

        public static Task<Result> ExecAsync(string command) => Task.Run(() => Exec(command));

        public static Result Exec(string command)
        {
            Log("[root] $ su -c " + Shorten(command));
            try
            {
                var process = Java.Lang.Runtime.GetRuntime().Exec(new[] { "su", "-c", command });
                using var stdout = process.InputStream;
                using var stderr = process.ErrorStream;
                var outTask = ReadAllAsync(stdout);
                var errTask = ReadAllAsync(stderr);
                int code;
                try { code = process.WaitFor(); }
                catch (Java.Lang.InterruptedException) { code = -1; }
                Task.WaitAll(new[] { outTask, errTask });
                Log("[root] exit=" + code);
                if (!string.IsNullOrWhiteSpace(outTask.Result)) Log("[root] out: " + Shorten(outTask.Result, 500));
                if (!string.IsNullOrWhiteSpace(errTask.Result)) Log("[root] err: " + Shorten(errTask.Result, 300));
                return new Result { ExitCode = code, Output = outTask.Result, Error = errTask.Result };
            }
            catch (System.Exception ex)
            {
                Log("[root] FAIL: " + ex.Message);
                return new Result { ExitCode = -99, Error = "SU_EXEC_FAIL: " + ex.Message };
            }
        }

        static string Shorten(string s, int n = 300)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        public static Task<Result> ExecScriptAsync(IEnumerable<string> lines) => Task.Run(() => ExecScript(lines));

        public static Result ExecScript(IEnumerable<string> lines)
        {
            Log("[root] $ su <<script"); foreach (var l in lines) Log("[root]   " + Shorten(l, 200));
            try
            {
                var process = Java.Lang.Runtime.GetRuntime().Exec(new[] { "su" });
                var outTask = ReadAllAsync(process.InputStream);
                var errTask = ReadAllAsync(process.ErrorStream);
                using (var stdin = new Java.IO.DataOutputStream(process.OutputStream))
                {
                    foreach (var line in lines)
                    {
                        var bytes = Encoding.UTF8.GetBytes(line + "\n");
                        stdin.Write(bytes, 0, bytes.Length);
                    }
                    stdin.Flush();
                    stdin.Close();
                }
                int code;
                try { code = process.WaitFor(); }
                catch (Java.Lang.InterruptedException) { code = -1; }
                Task.WaitAll(new[] { outTask, errTask });
                Log("[root] exit=" + code);
                if (!string.IsNullOrWhiteSpace(outTask.Result)) Log("[root] out: " + Shorten(outTask.Result, 500));
                if (!string.IsNullOrWhiteSpace(errTask.Result)) Log("[root] err: " + Shorten(errTask.Result, 300));
                return new Result { ExitCode = code, Output = outTask.Result, Error = errTask.Result };
            }
            catch (System.Exception ex)
            {
                Log("[root] FAIL: " + ex.Message);
                return new Result { ExitCode = -99, Error = "SU_EXEC_FAIL: " + ex.Message };
            }
        }


        private static async Task<string> ReadAllAsync(Stream s)
        {
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (true)
            {
                int n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            return sb.ToString();
        }
    }
}
