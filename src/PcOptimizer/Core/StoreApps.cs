using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PcOptimizer.Core
{
    public enum AppAdvice { Recommend, Optional, Other }

    public class StoreApp : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FullName { get; set; } = "";
        public string FamilyName { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Version { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public bool NonRemovable { get; set; }

        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public AppAdvice Advice { get; set; } = AppAdvice.Other;
        public bool ThirdParty { get; set; }

        public string Initial => Title.Length == 0 ? "?" : Title.Substring(0, 1).ToUpperInvariant();
        public string Meta => Name + " · " + Version;
        public string GroupText => Advice == AppAdvice.Recommend ? L.T("建议删除") : Advice == AppAdvice.Optional ? L.T("看需要") : L.T("其他应用");

        bool _selected;
        public bool Selected { get => _selected; set { _selected = value; Raise(nameof(Selected)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class RemovedApp
    {
        public string FamilyName { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime When { get; set; }
        public string WhenText => When.ToString("yyyy-MM-dd HH:mm");
    }

    public static class StoreApps
    {
        static List<(string Pattern, string Title, string Desc, AppAdvice Advice, bool ThirdParty)> Catalog() => new List<(string, string, string, AppAdvice, bool)>
        {
            ("king.com.*", L.T("King 游戏（Candy Crush 等）"), L.T("预装的推广游戏。"), AppAdvice.Recommend, true),
            ("Microsoft.MicrosoftSolitaireCollection", L.T("Microsoft 纸牌合集"), L.T("带广告的纸牌游戏，去广告要付费订阅。"), AppAdvice.Recommend, false),
            ("Microsoft.BingNews", L.T("资讯"), L.T("微软的新闻推送应用。"), AppAdvice.Recommend, false),
            ("Microsoft.BingSearch", L.T("必应搜索"), L.T("把系统里的网页搜索交给 Bing 的组件。删掉不影响搜索本机文件。"), AppAdvice.Recommend, false),
            ("Microsoft.GetHelp", L.T("获取帮助"), L.T("微软的在线客服入口。"), AppAdvice.Recommend, false),
            ("Microsoft.Getstarted", L.T("使用技巧"), L.T("介绍 Windows 功能的“提示”应用。"), AppAdvice.Recommend, false),
            ("Microsoft.WindowsFeedbackHub", L.T("反馈中心"), L.T("给微软提交意见和问题报告用的。"), AppAdvice.Recommend, false),
            ("Microsoft.MicrosoftOfficeHub", L.T("Microsoft 365（Office）入口"), L.T("推广 Office 订阅的入口应用，不是 Office 本身。删掉不影响已安装的 Word、Excel。"), AppAdvice.Recommend, false),
            ("Microsoft.549981C3F5F10", "Cortana", L.T("已经停止服务的语音助手。"), AppAdvice.Recommend, false),
            ("Microsoft.People", L.T("人脉"), L.T("旧版联系人应用，很少有人用。"), AppAdvice.Recommend, false),
            ("Microsoft.SkypeApp", "Skype", L.T("微软已经在 2025 年停止 Skype 服务。"), AppAdvice.Recommend, false),
            ("Microsoft.MixedReality.Portal", L.T("混合现实门户"), L.T("只有 VR 头显才用得到。"), AppAdvice.Recommend, false),
            ("Microsoft.Microsoft3DViewer", L.T("3D 查看器"), L.T("查看 3D 模型文件用的。"), AppAdvice.Recommend, false),
            ("Microsoft.MSPaint", "Paint 3D", L.T("3D 画图，微软已经不再更新。经典的“画图”是另一个应用，不受影响。"), AppAdvice.Recommend, false),
            ("Microsoft.Print3D", L.T("3D 打印"), L.T("只有 3D 打印机才用得到。"), AppAdvice.Recommend, false),
            ("Microsoft.ZuneVideo", L.T("电影和电视"), L.T("微软的视频商店和播放器。删掉后视频会用“媒体播放器”或你装的播放器打开。"), AppAdvice.Recommend, false),
            ("Microsoft.Office.OneNote", L.T("OneNote for Windows 10"), L.T("已经停止更新的旧版 OneNote。Office 里的 OneNote 不受影响。"), AppAdvice.Recommend, false),
            ("Microsoft.OneConnect", L.T("付费 WLAN 和手机套餐"), L.T("购买移动数据套餐用的，大多数地区用不到。"), AppAdvice.Recommend, false),
            ("Microsoft.Messaging", L.T("消息"), L.T("旧版短信应用，现在已经没有用了。"), AppAdvice.Recommend, false),
            ("Microsoft.Wallet", L.T("Microsoft 钱包"), L.T("已经停止服务。"), AppAdvice.Recommend, false),
            ("Microsoft.PowerAutomateDesktop", "Power Automate", L.T("自动化流程工具，一般用户用不到。"), AppAdvice.Recommend, false),
            ("Clipchamp.Clipchamp", "Clipchamp", L.T("微软的在线视频编辑器，高级功能要订阅。"), AppAdvice.Recommend, false),
            ("MicrosoftCorporationII.MicrosoftFamily", L.T("Microsoft 家庭安全"), L.T("家长控制的推广应用。需要管孩子用电脑的话可以留着。"), AppAdvice.Recommend, false),
            ("Microsoft.Windows.DevHome", "Dev Home", L.T("面向开发者的工具，微软已经停止开发。"), AppAdvice.Recommend, false),

            ("Microsoft.BingWeather", L.T("天气"), L.T("微软的天气应用。"), AppAdvice.Optional, false),
            ("Microsoft.WindowsMaps", L.T("地图"), L.T("离线地图和导航。"), AppAdvice.Optional, false),
            ("Microsoft.Todos", "Microsoft To Do", L.T("待办事项清单，和微软账户同步。"), AppAdvice.Optional, false),
            ("Microsoft.ZuneMusic", L.T("媒体播放器"), L.T("Windows 11 自带的音乐和视频播放器（以前叫 Groove 音乐）。装了别的播放器可以删掉。"), AppAdvice.Optional, false),
            ("MicrosoftCorporationII.QuickAssist", L.T("快速助手"), L.T("让别人远程帮你操作电脑。需要远程协助时会用到。"), AppAdvice.Optional, false),
            ("MicrosoftTeams", L.T("Teams（个人版）"), L.T("微软的聊天和视频会议软件。工作要用就留着。"), AppAdvice.Optional, false),
            ("MSTeams", "Microsoft Teams", L.T("微软的聊天和视频会议软件。工作要用就留着。"), AppAdvice.Optional, false),
            ("Microsoft.OutlookForWindows", L.T("新版 Outlook"), L.T("取代“邮件和日历”的新版邮件应用。用别的邮件软件可以删掉。"), AppAdvice.Optional, false),
            ("microsoft.windowscommunicationsapps", L.T("邮件和日历"), L.T("旧版邮件和日历应用，微软已经停止支持。"), AppAdvice.Optional, false),
            ("Microsoft.Copilot", "Copilot", L.T("微软的 AI 助手应用。"), AppAdvice.Optional, false),
            ("Microsoft.YourPhone", L.T("手机连接"), L.T("在电脑上收发手机短信、看手机照片。用不到可以删掉。"), AppAdvice.Optional, false),
            ("Microsoft.WindowsAlarms", L.T("时钟"), L.T("闹钟、计时器和专注时段。"), AppAdvice.Optional, false),
            ("Microsoft.WindowsSoundRecorder", L.T("录音机"), L.T("简单的录音应用。"), AppAdvice.Optional, false),
            ("Microsoft.XboxApp", "Xbox", L.T("旧版 Xbox 应用。不玩 Xbox 和 PC Game Pass 游戏可以删掉。"), AppAdvice.Optional, false),
            ("Microsoft.GamingApp", "Xbox", L.T("Xbox 应用，用来玩 PC Game Pass 游戏。不玩可以删掉。"), AppAdvice.Optional, false),
            ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", L.T("按 Win+G 打开的游戏工具栏，也能录屏。不玩游戏可以删掉。"), AppAdvice.Optional, false),

            ("SpotifyAB.SpotifyMusic", "Spotify", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("Disney.*", "Disney+", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("BytedancePte.Ltd.TikTok", "TikTok", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("Facebook.*", "Facebook / Instagram", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("AmazonVideo.PrimeVideo", "Prime Video", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("4DF9E0F8.Netflix", "Netflix", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("7EE7776C.LinkedInforWindows", "LinkedIn", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("9E2F88E3.Twitter", "X (Twitter)", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("AdobeSystemsIncorporated.AdobePhotoshopExpress", "Photoshop Express", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("Duolingo-LearnLanguagesforFree", "Duolingo", L.T("预装的第三方推广应用。如果是你自己装的、正在用，就留着。"), AppAdvice.Optional, true),
            ("5A894077.McAfeeSecurity", "McAfee", L.T("厂商预装的杀毒软件试用版。到期后会不停提醒付费，Windows 自带的 Defender 已经够用。"), AppAdvice.Recommend, true),
        };

        static readonly string[] Protected =
        {
            "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
            "Microsoft.VCLibs*", "Microsoft.UI.Xaml*", "Microsoft.NET.*", "Microsoft.WindowsAppRuntime*", "Microsoft.Services.Store.Engagement",
            "Microsoft.*Extension", "Microsoft.*Extensions", "Microsoft.Xbox.TCUI", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay",
            "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.MicrosoftEdge*", "Microsoft.LanguageExperiencePack*",
            "Microsoft.Windows.*", "Microsoft.Win32WebViewHost", "Microsoft.WindowsTerminal", "Microsoft.ApplicationCompatibilityEnhancements",
            "MicrosoftWindows.*", "Microsoft.WidgetsPlatformRuntime", "Microsoft.StartExperiencesApp", "Microsoft.OneDriveSync",
        };

        static bool Match(string name, string pattern)
        {
            if (pattern.Contains("*"))
                return Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsProtected(string name) => Protected.Any(p => Match(name, p)) && !Catalog().Any(c => Match(name, c.Pattern));

        public const string ListScript =
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' } | ForEach-Object { " +
            "'{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}' -f $_.Name, $_.PackageFullName, $_.PackageFamilyName, $_.Publisher, $_.NonRemovable, $_.InstallLocation, $_.Version }";

        public static List<StoreApp> Parse(string output, Func<StoreApp, string?>? displayName = null)
        {
            var catalog = Catalog();
            var list = new List<StoreApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in output.Split('\n'))
            {
                var f = raw.TrimEnd('\r').Split('\t');
                if (f.Length < 7 || f[0].Length == 0 || !seen.Add(f[0])) continue;
                var app = new StoreApp
                {
                    Name = f[0], FullName = f[1], FamilyName = f[2], Publisher = f[3],
                    NonRemovable = f[4].Equals("True", StringComparison.OrdinalIgnoreCase), InstallLocation = f[5], Version = f[6],
                };
                if (app.NonRemovable || IsProtected(app.Name)) continue;
                var c = catalog.FirstOrDefault(x => Match(app.Name, x.Pattern));
                if (c.Pattern != null)
                {
                    app.Title = c.Title; app.Description = c.Desc; app.Advice = c.Advice; app.ThirdParty = c.ThirdParty;
                }
                else
                {
                    app.Title = displayName?.Invoke(app) ?? Pretty(app.Name);
                    app.Description = Publisher(app.Publisher);
                }
                list.Add(app);
            }
            return list.OrderBy(a => a.Advice).ThenBy(a => a.Title, StringComparer.CurrentCulture).ToList();
        }

        public static List<StoreApp> List()
        {
            string? output = PowerShell.Run(ListScript, out string err, 60000);
            if (output == null || (output.Trim().Length == 0 && err.Trim().Length > 0))
                throw new InvalidOperationException(err.Trim().Length > 0 ? err.Trim() : L.T("PowerShell 没有返回结果。"));
            return Parse(output, ManifestName);
        }

        public static void Remove(StoreApp app)
        {
            PowerShell.Run("Remove-AppxPackage -Package '" + app.FullName.Replace("'", "''") + "' -ErrorAction Stop", out string err, 180000);
            if (err.Trim().Length > 0) throw new InvalidOperationException(FirstLine(err));
            OpLog.Write("APPX_REMOVE", $"{app.Title}\t{app.FullName}");
            SaveRemoved(app);
        }

        static string FirstLine(string s) => s.Trim().Split('\n')[0].Trim();

        public static string RemovedFile => Path.Combine(OpLog.LogDir, "removed-apps.txt");

        public static List<RemovedApp> Removed()
        {
            var list = new List<RemovedApp>();
            try
            {
                if (!File.Exists(RemovedFile)) return list;
                foreach (var line in File.ReadAllLines(RemovedFile, Encoding.UTF8))
                {
                    var f = line.Split('\t');
                    if (f.Length < 3) continue;
                    DateTime.TryParse(f[0], out var when);
                    list.RemoveAll(r => r.FamilyName.Equals(f[1], StringComparison.OrdinalIgnoreCase));
                    list.Add(new RemovedApp { When = when, FamilyName = f[1], Title = f[2] });
                }
            }
            catch (Exception) { }
            list.Reverse();
            return list;
        }

        static void SaveRemoved(StoreApp app)
        {
            try
            {
                Directory.CreateDirectory(OpLog.LogDir);
                File.AppendAllText(RemovedFile, $"{DateTime.Now:yyyy-MM-dd HH:mm}\t{app.FamilyName}\t{app.Title}\r\n", Encoding.UTF8);
            }
            catch (Exception) { }
        }

        public static void ForgetReinstalled(IEnumerable<StoreApp> installed)
        {
            try
            {
                if (!File.Exists(RemovedFile)) return;
                var have = new HashSet<string>(installed.Select(a => a.FamilyName), StringComparer.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(RemovedFile, Encoding.UTF8);
                var keep = lines.Where(l => { var f = l.Split('\t'); return f.Length < 3 || !have.Contains(f[1]); }).ToArray();
                if (keep.Length != lines.Length) File.WriteAllLines(RemovedFile, keep, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        public static string StoreLink(string familyName) => "ms-windows-store://pdp/?PFN=" + familyName;

        static string? ManifestName(StoreApp app)
        {
            try
            {
                string file = Path.Combine(app.InstallLocation, "AppxManifest.xml");
                if (!File.Exists(file)) return null;
                var doc = XDocument.Load(file);
                string? name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties")
                                 ?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value?.Trim();
                if (string.IsNullOrEmpty(name)) return null;
                if (!name!.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return name;
                string key = name.Substring("ms-resource:".Length);
                var tries = key.StartsWith("//") ? new[] { "ms-resource:" + key }
                    : key.StartsWith("/") ? new[] { $"ms-resource://{app.Name}{key}" }
                    : new[] { $"ms-resource://{app.Name}/Resources/{key}", $"ms-resource://{app.Name}/{key}" };
                foreach (var t in tries)
                {
                    var sb = new StringBuilder(512);
                    if (NativeMethods.SHLoadIndirectString($"@{{{app.FullName}?{t}}}", sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                        return sb.ToString();
                }
            }
            catch (Exception) { }
            return null;
        }

        public static string Pretty(string name)
        {
            string s = name;
            int dot = s.IndexOf('.');
            if (dot > 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            s = s.Replace('.', ' ');
            s = Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");
            return s.Trim();
        }

        public static string Publisher(string publisher)
        {
            var m = Regex.Match(publisher, @"(?:^|,\s*)O=(""[^""]*""|[^,]*)");
            if (!m.Success) m = Regex.Match(publisher, @"(?:^|,\s*)CN=(""[^""]*""|[^,]*)");
            return m.Success ? m.Groups[1].Value.Trim('"', ' ') : publisher;
        }
    }
}
