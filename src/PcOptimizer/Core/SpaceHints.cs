using System;
using System.IO;
using System.Linq;

namespace PcOptimizer.Core
{
    public enum HintKind
    {
        Keep,
        UseTool,
        CanDelete,
    }

    public class SpaceHint
    {
        public string Text = "";
        public HintKind Kind;
    }

    public static class SpaceHints
    {
        public class Roots
        {
            public string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            public string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            public string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            public string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            public string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        static Roots? _default;
        static Roots Default => _default ??= new Roots();

        static SpaceHint H(HintKind k, string text) => new SpaceHint { Kind = k, Text = text };

        static bool Under(string path, string root) =>
            root.Length > 0 && (path.Equals(root, StringComparison.OrdinalIgnoreCase) || FsUtil.IsWithin(path, root));

        static bool HasSegment(string path, params string[] segs)
        {
            var parts = path.Split('\\', '/');
            for (int i = 0; i + segs.Length <= parts.Length; i++)
                if (segs.Select((s, j) => parts[i + j].Equals(s, StringComparison.OrdinalIgnoreCase)).All(x => x)) return true;
            return false;
        }

        public static SpaceHint? Explain(string path, bool isDir) => Explain(path, isDir, Default);

        public static SpaceHint? Explain(string path, bool isDir, Roots r)
        {
            path = path.TrimEnd('\\', '/');
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string? parent = Path.GetDirectoryName(path);
            bool atRoot = parent != null && Path.GetPathRoot(path)!.TrimEnd('\\').Equals(parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (atRoot && !isDir)
            {
                if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.UseTool, L.T("休眠文件：电脑休眠和快速启动时用来保存内存内容，大小接近内存容量。不能直接删。不用休眠的话，可以在管理员命令提示符里运行 powercfg /h off 关掉休眠，它会自动消失。"));
                if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.Keep, L.T("虚拟内存：Windows 在内存不够用时使用。不要删，也不建议关掉。"));
            }
            if (atRoot && isDir)
            {
                if (name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.UseTool, L.T("大版本更新后留下的旧系统，用来退回之前的版本。Windows 一般会在退回期限过后自己删除它。"));
                if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.UseTool, L.T("系统还原点。可以在“系统属性 → 系统保护 → 配置”里调整它最多占多少空间。"));
                if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.UseTool, L.T("回收站。可以在“垃圾清理”页底部清空。"));
                if (name.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Equals("AMD", StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.CanDelete, L.T("显卡驱动安装时解压出来的文件。驱动装好以后一般可以删。"));
            }

            if (ext == ".vhdx" || ext == ".vhd" || ext == ".vmdk" || ext == ".vdi")
                return H(HintKind.UseTool, L.T("虚拟机、WSL 或 Docker 的虚拟磁盘。不要直接删，请在对应的软件里清理或删除虚拟机。"));
            if (ext == ".pst" || ext == ".ost")
                return H(HintKind.Keep, L.T("Outlook 的邮件数据。不要删，否则邮件可能丢失。"));
            if (HasSegment(path, "MobileSync", "Backup"))
                return H(HintKind.UseTool, L.T("iPhone 或 iPad 的备份。请在 iTunes 或“Apple 设备”应用里删除不需要的备份。"));
            if (HasSegment(path, "steamapps"))
                return H(HintKind.UseTool, L.T("Steam 的游戏文件。请在 Steam 里卸载不玩的游戏。"));

            if (Under(path, r.Windows))
            {
                string w = r.Windows;
                if (Under(path, Path.Combine(w, "WinSxS")))
                    return H(HintKind.UseTool, L.T("Windows 系统组件。不要手动删，可以用“垃圾清理”里的“系统组件旧版本”清理。"));
                if (Under(path, Path.Combine(w, "Installer")))
                    return H(HintKind.Keep, L.T("已安装软件的安装包，卸载和修复软件时要用。不要删。"));
                if (Under(path, Path.Combine(w, "SoftwareDistribution")))
                    return H(HintKind.UseTool, L.T("Windows 更新的下载缓存。可以用“垃圾清理”里的“Windows 更新缓存”清理。"));
                if (!isDir && ext == ".dmp")
                    return H(HintKind.UseTool, L.T("蓝屏时留下的转储文件，用来分析原因。不需要的话，可以在 Windows 自带的“磁盘清理”里勾选“系统错误内存转储文件”删除。"));
                return H(HintKind.Keep, L.T("Windows 系统文件。不要删。"));
            }

            if (Under(path, r.ProgramFiles) || Under(path, r.ProgramFilesX86))
                return H(HintKind.UseTool, L.T("软件的安装文件。不要直接删，请用“彻底卸载”卸载不用的软件。"));

            if (r.Profile.Length > 0)
            {
                if (!isDir && ext == ".dmp" && HasSegment(path, "CrashDumps"))
                    return H(HintKind.CanDelete, L.T("程序崩溃时留下的转储文件。不需要分析崩溃原因的话可以删。"));
                if (Under(path, Path.Combine(r.Profile, "Downloads")))
                {
                    if (isDir && path.Equals(Path.Combine(r.Profile, "Downloads"), StringComparison.OrdinalIgnoreCase))
                        return H(HintKind.CanDelete, L.T("下载文件夹，常有装完就不再需要的安装包和压缩包。"));
                    if (!isDir && new[] { ".iso", ".zip", ".7z", ".rar", ".exe", ".msi" }.Contains(ext))
                        return H(HintKind.CanDelete, L.T("下载的安装包或压缩包。软件已经装好、内容已经解压的话，一般可以删。"));
                }
                if (isDir && path.Equals(Path.Combine(r.Profile, "AppData"), StringComparison.OrdinalIgnoreCase))
                    return H(HintKind.Keep, L.T("各个软件的设置和缓存。不要整个删除，可以进去看是哪个软件占得多。"));
            }
            return null;
        }

        public static string? RecycleBlocked(string path) => RecycleBlocked(path, Default);

        public static string? RecycleBlocked(string path, Roots r)
        {
            var hint = Explain(path, false, r);
            if (hint != null && hint.Kind != HintKind.CanDelete) return hint.Text;
            if (Under(path, r.Windows)) return L.T("Windows 系统文件。不要删。");
            return null;
        }
    }
}
