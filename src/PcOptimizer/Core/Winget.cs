using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PcOptimizer.Core
{
    public enum UpdateState { Waiting, Updating, Done, Failed }

    public class SoftwareUpdate : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Available { get; set; } = "";
        public string Source { get; set; } = "";

        public string Initial => Name.Length == 0 ? "?" : Name.Substring(0, 1).ToUpperInvariant();
        public string VersionText => $"{Version} → {Available}";
        public string Meta => Id + (Source.Length > 0 ? " · " + Source : "");

        bool _selected;
        public bool Selected { get => _selected; set { _selected = value; Raise(nameof(Selected)); } }

        UpdateState _state = UpdateState.Waiting;
        string _message = "";
        public UpdateState State { get => _state; set { _state = value; Raise(nameof(State)); Raise(nameof(StateText)); Raise(nameof(CanSelect)); } }
        public string Message { get => _message; set { _message = value; Raise(nameof(Message)); Raise(nameof(StateText)); } }
        public bool CanSelect => State == UpdateState.Waiting || State == UpdateState.Failed;
        public string StateText => State == UpdateState.Updating ? L.T("正在更新…")
                                 : State == UpdateState.Done ? L.T("已更新")
                                 : State == UpdateState.Failed ? (Message.Length > 0 ? Message : L.T("没有更新成功"))
                                 : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public static class Winget
    {
        public const string StoreLink = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

        public static Func<string, int, (string Output, int ExitCode)> Run = RealRun;

        static (string, int) RealRun(string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo("winget.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            Process p;
            try { p = Process.Start(psi)!; }
            catch (Win32Exception) { throw new FileNotFoundException(L.T("没有找到 winget。")); }
            using (p)
            {
                var err = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return (output, -1); }
                return (output + err.Result, p.ExitCode);
            }
        }

        public static List<SoftwareUpdate> ListUpdates()
        {
            var (output, code) = Run("upgrade --accept-source-agreements --disable-interactivity", 180000);
            var list = ParseTable(output);
            uint c = unchecked((uint)code);
            if (list.Count == 0 && code != 0 && c != 0x8A15002B && c != 0x8A150014 && !output.Contains("----------"))
                throw new InvalidOperationException(ExplainExitCode(code, output));
            if (list.Any(u => u.Id.EndsWith("…")))
            {
                string file = Path.Combine(Path.GetTempPath(), "pcopt-winget-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    Run($"export -o \"{file}\" --accept-source-agreements --disable-interactivity", 180000);
                    if (File.Exists(file)) ResolveIds(list, File.ReadAllText(file));
                }
                finally { try { File.Delete(file); } catch (Exception) { } }
            }
            return list;
        }

        public static string? Upgrade(SoftwareUpdate u)
        {
            if (u.Id.EndsWith("…")) return L.T("winget 显示的 Id 不完整，请在命令行里手动更新。");
            string args = $"upgrade --id \"{u.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
            if (u.Source.Length > 0) args += $" --source \"{u.Source}\"";
            var (output, code) = Run(args, 30 * 60 * 1000);
            string? error = code == 0 ? null : ExplainExitCode(code, output);
            OpLog.Write(error == null ? "UPDATE_OK" : "UPDATE_FAIL", $"{u.Name}\t{u.Id}\t{u.Version} -> {u.Available}" + (error == null ? "" : "\t" + error));
            return error;
        }

        public static string ExplainExitCode(int code, string output)
        {
            switch (unchecked((uint)code))
            {
                case 0xFFFFFFFF: return L.T("超过 30 分钟还没有完成，已停止。");
                case 0x8A15002B: return L.T("没有适用的新版本。");
                case 0x8A150056: return L.T("安装程序要求先关掉这个软件，请关掉后再试。");
                case 0x8A15010B: case 0x8A150101: return L.T("这个软件正在运行，请关掉后再试。");
                case 0x8A150102: return L.T("安装程序需要重启电脑才能完成。");
                case 0x8A150049: case 0x8A15004A: return L.T("安装程序的哈希校验没有通过，已停止安装。");
                case 0x8A150008: case 0x8A150007: return L.T("下载失败，请检查网络。");
                case 0x8A15000F: case 0x8A150003: return L.T("连不上软件源，请检查网络。");
            }
            string last = output.Split('\n').Select(Clean).LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            return L.F("winget 返回 0x{0:X8}", unchecked((uint)code)) + (last.Length > 0 ? L.T("：") + last : "");
        }

        static string Clean(string line)
        {
            line = line.TrimEnd('\r');
            int r = line.LastIndexOf('\r');
            if (r >= 0) line = line.Substring(r + 1);
            return Regex.Replace(line, @"\x1B\[[0-9;?]*[A-Za-z]", "").TrimEnd();
        }

        public static int Width(char c) =>
            (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
            (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6) || char.IsHighSurrogate(c) ? 2 : char.IsLowSurrogate(c) ? 0 : 1;

        static string Slice(string line, int from, int to)
        {
            var sb = new StringBuilder();
            int col = 0;
            foreach (char c in line)
            {
                int w = Width(c);
                if (col >= from && col < to) sb.Append(c);
                col += w;
            }
            return sb.ToString().Trim();
        }

        static int LineWidth(string line) => line.Sum(Width);

        public static List<SoftwareUpdate> ParseTable(string output)
        {
            var lines = output.Split('\n').Select(Clean).ToList();
            var list = new List<SoftwareUpdate>();
            int dash = lines.FindIndex(l => l.Length >= 10 && l.Trim().All(c => c == '-'));
            if (dash < 1) return list;

            string header = lines[dash - 1];
            var starts = new List<int>();
            int col = 0; bool prevSpace = true;
            foreach (char c in header)
            {
                if (c != ' ' && prevSpace) starts.Add(col);
                prevSpace = c == ' ';
                col += Width(c);
            }
            if (starts.Count < 4) return list;
            starts.Add(int.MaxValue);

            for (int i = dash + 1; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) break;
                if (LineWidth(line) < starts[3]) break;
                var u = new SoftwareUpdate
                {
                    Name = Slice(line, starts[0], starts[1]),
                    Id = Slice(line, starts[1], starts[2]),
                    Version = Slice(line, starts[2], starts[3]),
                    Available = Slice(line, starts[3], starts[4]),
                    Source = starts.Count > 5 ? Slice(line, starts[4], starts[5]) : "",
                };
                if (u.Id.Length == 0 || u.Id.Contains(" ") || u.Available.Length == 0 || u.Available.Contains(" ")) break;
                list.Add(u);
            }
            return list;
        }

        public static void ResolveIds(List<SoftwareUpdate> list, string exportJson)
        {
            var ids = Regex.Matches(exportJson, "\"PackageIdentifier\"\\s*:\\s*\"([^\"]+)\"")
                           .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            foreach (var u in list.Where(u => u.Id.EndsWith("…")))
            {
                string prefix = u.Id.TrimEnd('…');
                var hits = ids.Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1) u.Id = hits[0];
            }
        }
    }
}
