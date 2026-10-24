using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public enum RegRoot { User, Machine }

    public class PrivacyValue
    {
        public RegRoot Root;
        public string Path = "";
        public string Name = "";
        public int Off;
        public int? Default;

        public PrivacyValue(RegRoot root, string path, string name, int off, int? def)
        { Root = root; Path = path; Name = name; Off = off; Default = def; }

        public string FullName => (Root == RegRoot.User ? "HKCU\\" : "HKLM\\") + Path + "\\" + Name;
    }

    public enum PrivacyState { Default, Off, Partial, Hidden }

    public class PrivacyItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        public string Group { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Recommended { get; set; }
        public bool RestartExplorer { get; set; }
        public List<PrivacyValue> Values { get; } = new List<PrivacyValue>();
        public Func<bool>? AlreadyHidden { get; set; }

        PrivacyState _state;
        public PrivacyState State
        {
            get => _state;
            set { _state = value; Raise(nameof(State)); Raise(nameof(IsOff)); Raise(nameof(IsOn)); Raise(nameof(StateText)); }
        }
        public bool IsOff => State == PrivacyState.Off;
        public bool IsOn => State != PrivacyState.Off && State != PrivacyState.Hidden;
        public string StateText => State == PrivacyState.Off ? L.T("已关闭") : State == PrivacyState.Hidden ? L.T("已隐藏")
                                 : State == PrivacyState.Partial ? L.T("部分关闭") : L.T("默认");
        public string? StateTip => State == PrivacyState.Hidden
            ? L.T("你已经在 Windows 自己的设置里把它隐藏了，或者这个版本的 Windows 默认就不显示。点开关可以把它彻底关掉，以后 Windows 更新也不会再打开。") : null;
        public bool AdminTag { get; set; }
        public bool NeedsAdmin => Values.Any(v => v.Root == RegRoot.Machine);
        public string Details => string.Join("\n", Values.Select(v => $"{v.FullName} = {v.Off}"));

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public static class Privacy
    {
        const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string Pol = @"SOFTWARE\Policies\Microsoft\Windows";
        const string UserPol = @"Software\Policies\Microsoft\Windows";

        public static Func<RegRoot, string, string, object?> Read = RealRead;
        public static Action<RegRoot, string, string, int?> Write = RealWrite;

        public static List<PrivacyItem> List()
        {
            string ads = L.T("广告与推荐"), search = L.T("搜索"), ai = L.T("AI 功能"), data = L.T("数据收集");
            var list = new List<PrivacyItem>
            {
                Item("start", ads, L.T("开始菜单里的推荐"), L.T("开始菜单里推荐的应用、提示和网站。"), true, true,
                    U(Cdm, "SubscribedContent-338388Enabled", 0, 1), U(Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                    U(Adv, "Start_IrisRecommendations", 0, null)),
                Item("lock", ads, L.T("锁屏上的趣闻和广告"), L.T("锁屏图片上叠加的“趣闻、提示”和推广内容。锁屏图片本身不变。"), true, false,
                    U(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1), U(Cdm, "SubscribedContent-338387Enabled", 0, 1)),
                Item("tips", ads, L.T("使用时弹出的提示和建议"), L.T("Windows 时不时弹出的“小技巧”和使用建议。"), true, false,
                    U(Cdm, "SubscribedContent-338389Enabled", 0, 1), U(Cdm, "SoftLandingEnabled", 0, 1)),
                Item("settings", ads, L.T("“设置”里的推荐内容"), L.T("“设置”应用里推荐的功能和微软服务。"), true, false,
                    U(Cdm, "SubscribedContent-338393Enabled", 0, 1), U(Cdm, "SubscribedContent-353694Enabled", 0, 1),
                    U(Cdm, "SubscribedContent-353696Enabled", 0, 1)),
                Item("silent", ads, L.T("自动安装推荐的应用"), L.T("Windows 有时会自己装上游戏之类的推荐应用。关闭后不再自动安装，已经装上的不受影响。"), true, false,
                    U(Cdm, "SilentInstalledAppsEnabled", 0, 1)),
                Item("welcome", ads, L.T("更新后的“欢迎使用”页面"), L.T("大版本更新后全屏弹出的“完成设置”和微软服务推广。"), true, false,
                    U(Cdm, "SubscribedContent-310093Enabled", 0, 1),
                    U(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null)),
                Item("sync", ads, L.T("文件资源管理器里的推广"), L.T("文件资源管理器顶部推荐 OneDrive 和 Microsoft 365 的通知。"), true, true,
                    U(Adv, "ShowSyncProviderNotifications", 0, null)),
                Item("adid", ads, L.T("广告 ID"), L.T("应用用这个编号跨应用追踪你，推送个性化广告。关闭后广告数量不变，只是不再按你的喜好推送。"), true, false,
                    U(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)),
                Item("tailored", ads, L.T("根据诊断数据推送的内容"), L.T("微软根据你的诊断数据推荐提示、广告和服务。"), true, false,
                    U(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)),
                Item("lang", ads, L.T("网站读取语言列表"), L.T("让网站通过 Windows 读取你装了哪些语言，用来显示本地内容。浏览器自己的语言设置不受影响。"), true, false,
                    U(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, null)),

                Item("bing", search, L.T("搜索里的 Bing 网页结果"), L.T("在开始菜单和任务栏里搜索时，混在本机结果里的 Bing 网页结果。关闭后只搜索本机的应用、文件和设置。"), false, true,
                    U(UserPol + @"\Explorer", "DisableSearchBoxSuggestions", 1, null),
                    U(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null)),
                Item("highlights", search, L.T("搜索要点"), L.T("搜索框里每天变化的插图、节日和热门话题。"), true, false,
                    U(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, null)),

                Item("copilot", ai, "Copilot", L.T("任务栏上的 Copilot 按钮和旧版 Copilot 侧边栏。新版 Copilot 是单独的应用，可以在“更多功能”的“预装应用清理”里删掉。"), false, true,
                    U(UserPol + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1, null), U(Adv, "ShowCopilotButton", 0, null)),
                Item("recall", ai, L.T("“回顾”（Recall）"), L.T("定时给屏幕截图，方便以后搜索看过的内容。只有 Copilot+ 电脑有这个功能。"), true, false,
                    U(UserPol + @"\WindowsAI", "DisableAIDataAnalysis", 1, null)),
                Item("widgets", ai, L.T("任务栏上的资讯和天气"), L.T("任务栏上的小组件（Windows 11）或“资讯和兴趣”（Windows 10），里面大多是新闻推送。"), false, true,
                    M(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null), M(Pol + @"\Windows Feeds", "EnableFeeds", 0, null)),

                Item("telemetry", data, L.T("可选诊断数据"), L.T("把诊断数据限制在“必需”这一档，不再发送浏览记录、应用使用情况等可选数据。家庭版和专业版最低只能设到这一档。"), true, false,
                    M(Pol + @"\DataCollection", "AllowTelemetry", 1, null)),
                Item("typing", data, L.T("发送墨迹和键入数据"), L.T("把你手写和打字的内容发给微软，用来改进识别和输入建议。"), true, false,
                    U(@"Software\Microsoft\Input\TIPC", "Enabled", 0, null)),
                Item("feedback", data, L.T("反馈调查弹窗"), L.T("Windows 时不时弹出来问“你有多大可能推荐 Windows”。"), true, false,
                    U(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)),
                Item("activity", data, L.T("活动历史"), L.T("记录你打开过的应用和文件，并上传到微软账户用来在其他设备上继续。"), true, false,
                    M(Pol + @"\System", "PublishUserActivities", 0, null), M(Pol + @"\System", "UploadUserActivities", 0, null)),
            };
            var copilot = list.First(i => i.Id == "copilot");
            copilot.AlreadyHidden = () =>
                Read(RegRoot.User, Adv, "ShowCopilotButton") is int n ? n == 0 : Build() >= 26100;
            list.First(i => i.Id == "widgets").AlreadyHidden = () =>
                Read(RegRoot.User, Adv, "TaskbarDa") is int da && da == 0 ||
                Read(RegRoot.User, @"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode") is int m && m == 2;
            foreach (var i in list) i.State = Evaluate(i);
            return list;
        }

        static PrivacyItem Item(string id, string group, string title, string desc, bool recommended, bool explorer, params PrivacyValue[] values)
        {
            var i = new PrivacyItem { Id = id, Group = group, Title = title, Description = desc, Recommended = recommended, RestartExplorer = explorer };
            i.Values.AddRange(values);
            return i;
        }
        static PrivacyValue U(string path, string name, int off, int? def) => new PrivacyValue(RegRoot.User, path, name, off, def);
        static PrivacyValue M(string path, string name, int off, int? def) => new PrivacyValue(RegRoot.Machine, path, name, off, def);

        public static PrivacyState Evaluate(PrivacyItem item)
        {
            int off = item.Values.Count(v => Read(v.Root, v.Path, v.Name) is int n && n == v.Off);
            if (off == item.Values.Count) return PrivacyState.Off;
            if (item.AlreadyHidden?.Invoke() == true) return PrivacyState.Hidden;
            return off == 0 ? PrivacyState.Default : PrivacyState.Partial;
        }

        static int Build()
        {
            var v = Read(RegRoot.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");
            return v != null && int.TryParse(v.ToString(), out int b) ? b : 0;
        }

        public static void Set(PrivacyItem item, bool off)
        {
            var before = item.Values.Select(v => Read(v.Root, v.Path, v.Name)).ToList();
            try
            {
                foreach (var v in item.Values) Write(v.Root, v.Path, v.Name, off ? v.Off : v.Default);
            }
            finally
            {
                item.State = Evaluate(item);
            }
            OpLog.Write(off ? "PRIVACY_OFF" : "PRIVACY_DEFAULT", item.Title + "\t" + string.Join("; ",
                item.Values.Select((v, i) => $"{v.FullName}: {Show(before[i])} -> {Show(off ? v.Off : v.Default)}")));
        }

        static string Show(object? v) => v == null ? "(无)" : v.ToString() ?? "";

        static RegistryKey Base(RegRoot root) =>
            RegistryKey.OpenBaseKey(root == RegRoot.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

        static object? RealRead(RegRoot root, string path, string name)
        {
            try
            {
                using (var b = Base(root))
                using (var k = b.OpenSubKey(path))
                    return k?.GetValue(name);
            }
            catch (Exception) { return null; }
        }

        static void RealWrite(RegRoot root, string path, string name, int? value)
        {
            using (var b = Base(root))
            {
                bool empty;
                if (value == null)
                {
                    using (var k = b.OpenSubKey(path, true))
                    {
                        if (k == null) return;
                        k.DeleteValue(name, false);
                        empty = k.ValueCount == 0 && k.SubKeyCount == 0;
                    }
                    if (empty && path.IndexOf(@"\Policies\", StringComparison.OrdinalIgnoreCase) >= 0)
                        try { b.DeleteSubKey(path, false); } catch (Exception) { }
                    return;
                }
                using (var k = b.CreateSubKey(path, true))
                {
                    if (k == null) throw new UnauthorizedAccessException(L.T("无法打开注册表项"));
                    k.SetValue(name, value.Value, RegistryValueKind.DWord);
                }
            }
        }
    }
}
