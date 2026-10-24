using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PcOptimizer.Core;

int passed = 0, failed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"  通过  {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"  失败  {name}: {e.Message}"); }
}
void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

string NewTemp()
{
    string d = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(d);
    return d;
}
void Make(string path, int size, double ageHours)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[size]);
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
}

OpLog.LogDir = Path.Combine(NewTemp(), "logs");

(string root, string outside, CleanTarget target) Setup()
{
    string tmp = NewTemp();
    string root = Path.Combine(tmp, "junk"), outside = Path.Combine(tmp, "important");
    Make(Path.Combine(root, "old.tmp"), 100, 48);
    Make(Path.Combine(root, "sub", "old2.log"), 50, 48);
    Make(Path.Combine(root, "fresh.tmp"), 10, 0);
    Make(Path.Combine(outside, "keep.docx"), 999, 48);
    return (root, outside, new CleanTarget { Key = "t", Name = "测试", Roots = { root } });
}

Console.WriteLine("Cleaner");
Test("扫描只找旧文件，且不删除任何东西", () =>
{
    var (root, _, t) = Setup();
    var found = Cleaner.Scan(new[] { t });
    var names = found.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToList();
    Assert(names.SequenceEqual(new[] { "old.tmp", "old2.log" }), string.Join(",", names));
    Assert(File.Exists(Path.Combine(root, "old.tmp")), "扫描不应删除文件");
});
Test("文件名通配符过滤", () =>
{
    var (_, _, t) = Setup();
    t.Patterns = new[] { "*.log" };
    var found = Cleaner.Scan(new[] { t });
    Assert(found.Count == 1 && found[0].Path.EndsWith("old2.log"), "应只找到 old2.log");
});
Test("只删除列表中的文件，新文件和范围外文件保留", () =>
{
    var (root, outside, t) = Setup();
    var r = Cleaner.Delete(Cleaner.Scan(new[] { t }));
    Assert(r.Deleted == 2 && r.Freed == 150 && r.Skipped.Count == 0, $"deleted={r.Deleted} freed={r.Freed}");
    Assert(File.Exists(Path.Combine(root, "fresh.tmp")), "新文件不应被删");
    Assert(File.Exists(Path.Combine(outside, "keep.docx")), "范围外文件不应被删");
});
Test("不顺着符号链接进入其他目录", () =>
{
    var (root, outside, t) = Setup();
    try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside); }
    catch (Exception) { Console.WriteLine("    （系统不允许创建链接，跳过）"); return; }
    var found = Cleaner.Scan(new[] { t });
    Assert(!found.Any(f => f.Path.Contains("keep.docx")), "不应通过链接扫到 keep.docx");
});
Test("拒绝删除 ..\\ 逃逸到清理范围外的路径", () =>
{
    var (root, outside, _) = Setup();
    string keep = Path.Combine(outside, "keep.docx");
    var evil = new FoundFile { TargetKey = "t", Root = root, Path = Path.Combine(root, "..", "important", "keep.docx"),
                               Size = 999, MtimeUtc = File.GetLastWriteTimeUtc(keep) };
    var r = Cleaner.Delete(new[] { evil });
    Assert(r.Deleted == 0 && r.Skipped.Count == 1, "应跳过");
    Assert(File.Exists(keep), "keep.docx 必须还在");
});
Test("扫描后被改动的文件跳过", () =>
{
    var (_, _, t) = Setup();
    var f = Cleaner.Scan(new[] { t }).First(x => x.Path.EndsWith("old.tmp"));
    Make(f.Path, 500, 0);
    var r = Cleaner.Delete(new[] { f });
    Assert(r.Deleted == 0 && r.Skipped[0].Value.Contains("变化"), "应因变化而跳过");
});

Console.WriteLine("DiskUsage");
Test("统计子目录大小和最大文件", () =>
{
    string d = NewTemp();
    Make(Path.Combine(d, "a", "big.bin"), 3000, 0);
    Make(Path.Combine(d, "a", "x", "s.bin"), 100, 0);
    Make(Path.Combine(d, "b", "m.bin"), 1000, 0);
    Make(Path.Combine(d, "loose.bin"), 10, 0);
    var r = DiskUsage.Analyze(d, 2);
    Assert(r.Total == 4110, $"total={r.Total}");
    Assert(r.Children[0].Name == "a" && r.Children[0].Size == 3100, "最大的应是 a");
    Assert(r.Biggest.Select(b => b.Size).SequenceEqual(new long[] { 3000, 1000 }), "最大文件排序错误");
});

Console.WriteLine("FsUtil / StartupManager");
Test("大小格式化", () =>
{
    Assert(FsUtil.HumanSize(512) == "512 B", FsUtil.HumanSize(512));
    Assert(FsUtil.HumanSize(1536) == "1.5 KB", FsUtil.HumanSize(1536));
    Assert(FsUtil.HumanSize(5L << 30) == "5.0 GB", FsUtil.HumanSize(5L << 30));
});
Test("通配符", () =>
{
    Assert(FsUtil.WildcardMatch("thumbcache_256.db", "thumbcache_*.db"), "1");
    Assert(FsUtil.WildcardMatch("THUMBCACHE_1.DB", "thumbcache_*.db"), "大小写");
    Assert(!FsUtil.WildcardMatch("thumbcache_1.db.bak", "thumbcache_*.db"), "3");
});
Test("从启动命令解析程序路径", () =>
{
    Assert(StartupManager.ParseExePath("\"C:\\Program Files\\A\\a.exe\" --min") == "C:\\Program Files\\A\\a.exe", "带引号");
    Assert(StartupManager.ParseExePath("C:\\Tools\\b.exe /silent") == "C:\\Tools\\b.exe", "不带引号");
});

Console.WriteLine("Programs / Residue / RegBackup");
Test("拆分卸载命令", () =>
{
    var (e1, a1) = Programs.SplitCommand("\"C:\\Program Files\\A\\uninst.exe\" /S");
    Assert(e1 == "C:\\Program Files\\A\\uninst.exe" && a1 == "/S", $"{e1}|{a1}");
    var (e2, a2) = Programs.SplitCommand("MsiExec.exe /X{1234-ABCD}");
    Assert(e2 == "MsiExec.exe" && a2 == "/X{1234-ABCD}", $"{e2}|{a2}");
    var (e3, a3) = Programs.SplitCommand("C:\\Tools\\My App\\unins000.exe");
    Assert(e3 == "C:\\Tools\\My App\\unins000.exe" && a3 == "", $"{e3}|{a3}");
});
Test("判断失效的卸载记录", () =>
{
    Func<string, bool> none = _ => false, all = _ => true;
    string abs = Path.Combine(Path.GetTempPath(), "gone", "uninst.exe");
    Assert(Programs.IsBrokenEntry($"\"{abs}\" /S", none), "文件不存在应判定失效");
    Assert(!Programs.IsBrokenEntry($"\"{abs}\" /S", all), "文件存在不应判定失效");
    Assert(!Programs.IsBrokenEntry("MsiExec.exe /X{1234}", none), "MSI 不判断");
    Assert(!Programs.IsBrokenEntry("uninst.exe /S", none), "相对路径不判断");
});
Test("名称归一化", () =>
{
    Assert(Residue.Normalize("Foo Player 2.3.1 (x64)") == "fooplayer", Residue.Normalize("Foo Player 2.3.1 (x64)"));
    Assert(Residue.Normalize("QQ音乐") == "qq音乐", Residue.Normalize("QQ音乐"));
    Assert(Residue.Normalize("Some-App v10.2") == "someapp", Residue.Normalize("Some-App v10.2"));
});
Test("残留文件夹：只认同名，排除其他软件的目录", () =>
{
    string d = NewTemp();
    string pf = Path.Combine(d, "ProgramFiles"), ad = Path.Combine(d, "AppData");
    Make(Path.Combine(pf, "FooSoft", "Foo Player", "a.dll"), 10, 0);
    Make(Path.Combine(ad, "FooPlayer", "cfg.ini"), 10, 0);
    Make(Path.Combine(ad, "FooSoft", "Bar Editor", "x.ini"), 10, 0);
    Make(Path.Combine(ad, "Foo Player Plus", "y.ini"), 10, 0);
    Make(Path.Combine(ad, "Shared", "z.ini"), 10, 0);
    var protectedNames = new HashSet<string> { Residue.Normalize("Bar Editor") };
    var found = Residue.FindFolders("Foo Player 2.0", "FooSoft", "", new[] { pf, ad },
                                    new[] { Path.Combine(ad, "Shared") }, protectedNames);
    var rel = found.Select(f => Path.GetRelativePath(d, f).Replace('\\', '/')).OrderBy(x => x).ToList();
    Assert(rel.SequenceEqual(new[] { "AppData/FooPlayer", "ProgramFiles/FooSoft/Foo Player" }), string.Join(",", rel));
});
Test("残留文件夹：不碰根目录和其他软件的安装目录", () =>
{
    string d = NewTemp();
    string pf = Path.Combine(d, "ProgramFiles");
    Make(Path.Combine(pf, "Foo", "a.dll"), 10, 0);
    var f1 = Residue.FindFolders("Foo", "", pf, new[] { pf }, new string[0], new HashSet<string>());
    Assert(f1.Count == 1 && f1[0].EndsWith("Foo"), string.Join(",", f1));
    var f2 = Residue.FindFolders("Foo", "", "", new[] { pf }, new[] { Path.Combine(pf, "Foo", "Plugin") }, new HashSet<string>());
    Assert(f2.Count == 0, string.Join(",", f2));
    Make(Path.Combine(pf, "Update", "u.exe"), 10, 0);
    var f3 = Residue.FindFolders("Update", "", "", new[] { pf }, new string[0], new HashSet<string>());
    Assert(f3.Count == 0, string.Join(",", f3));
});
Test(".reg 备份格式", () =>
{
    Assert(RegBackup.FormatValue("Path", "C:\\a \"b\"", Microsoft.Win32.RegistryValueKind.String) == "\"Path\"=\"C:\\\\a \\\"b\\\"\"\r\n", "字符串转义");
    Assert(RegBackup.FormatValue("", "x", Microsoft.Win32.RegistryValueKind.String) == "@=\"x\"\r\n", "默认值");
    Assert(RegBackup.FormatValue("N", 255, Microsoft.Win32.RegistryValueKind.DWord) == "\"N\"=dword:000000ff\r\n", "DWORD");
    Assert(RegBackup.FormatValue("B", new byte[] { 3, 0, 0xab }, Microsoft.Win32.RegistryValueKind.Binary) == "\"B\"=hex:03,00,ab\r\n", "二进制");
    Assert(RegBackup.FormatValue("E", "%A%", Microsoft.Win32.RegistryValueKind.ExpandString) == "\"E\"=hex(2):25,00,41,00,25,00,00,00\r\n", "可扩展字符串");
});

Test("MD5 计算正确", () =>
{
    string d = NewTemp();
    string f = Path.Combine(d, "abc.txt");
    File.WriteAllText(f, "abc");
    Assert(Dedup.Md5Of(f) == "900150983cd24fb0d6963f7d28e17f72", Dedup.Md5Of(f) ?? "null");
});
Test("文件去重：只按 MD5 判断，大小相同内容不同的不算重复", () =>
{
    string d = NewTemp();
    File.WriteAllText(Path.Combine(d, "a.txt"), "hello world");
    Directory.CreateDirectory(Path.Combine(d, "sub"));
    File.WriteAllText(Path.Combine(d, "sub", "a 副本.txt"), "hello world");
    File.WriteAllText(Path.Combine(d, "b.txt"), "hello WORLD");
    File.WriteAllText(Path.Combine(d, "c.txt"), "hello world!");
    File.SetLastWriteTime(Path.Combine(d, "a.txt"), DateTime.Now.AddDays(-3));
    var groups = Dedup.Find(new[] { d, Path.Combine(d, "sub") }, 1, null, default);
    Assert(groups.Count == 1, $"组数 {groups.Count}");
    var g = groups[0];
    Assert(g.Files.Count == 2, $"文件数 {g.Files.Count}（子目录不应重复计入）");
    Assert(g.Files[0].Path.EndsWith("a.txt") && !g.Files[0].Checked, "最早的保留、不勾选");
    Assert(g.Files[1].Checked, "其余默认勾选");
});

Test("文件粉碎：内容被随机数据覆盖，长度不变", () =>
{
    string d = NewTemp();
    string f = Path.Combine(d, "secret.txt");
    var original = System.Text.Encoding.UTF8.GetBytes(new string('A', 3_000_000));
    File.WriteAllBytes(f, original);
    long reported = 0;
    Shredder.Overwrite(f, 1, n => reported += n, default);
    var after = File.ReadAllBytes(f);
    Assert(after.Length == original.Length, "长度变了");
    Assert(reported == original.Length, $"进度 {reported}");
    int same = 0; for (int i = 0; i < after.Length; i++) if (after[i] == (byte)'A') same++;
    Assert(same < after.Length / 100, $"还有 {same} 个字节没变");
});
Test("文件粉碎：文件和文件夹被删掉，目录外的不动", () =>
{
    string d = NewTemp();
    string target = Path.Combine(d, "to-shred");
    Make(Path.Combine(target, "a.bin"), 5000, 0);
    Make(Path.Combine(target, "sub", "b.bin"), 100, 0);
    Make(Path.Combine(d, "keep.bin"), 100, 0);
    var r = Shredder.Shred(new[] { target }, 1, null, default);
    Assert(r.Files == 2 && r.Failed.Count == 0, $"粉碎 {r.Files} 个，失败 {string.Join(",", r.Failed)}");
    Assert(!Directory.Exists(target), "文件夹还在");
    Assert(Directory.GetFileSystemEntries(d).Length == 1 && File.Exists(Path.Combine(d, "keep.bin")), "目录外文件被动了或留下了改名后的残余");
});
Test("文件粉碎：拒绝磁盘根目录和用户文件夹本身", () =>
{
    Assert(Shredder.CheckAllowed(Path.GetPathRoot(Path.GetTempPath())!) != null, "根目录");
    Assert(Shredder.CheckAllowed(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) != null, "用户目录");
    Assert(Shredder.CheckAllowed(Path.Combine(NewTemp(), "x.txt")) == null, "普通文件应允许");
});

Dictionary<string, string> LoadLang(string code)
{
    var asm = typeof(L).Assembly;
    string name = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + code + ".lang"));
    var map = new Dictionary<string, string>();
    string? key = null;
    using var r = new StreamReader(asm.GetManifestResourceStream(name)!);
    for (string? line; (line = r.ReadLine()) != null;)
    {
        if (line.StartsWith("#")) continue;
        if (line.StartsWith("=> ") && key != null) { map[key] = line.Substring(3); key = null; }
        else if (line.Length > 0) key = line;
    }
    return map;
}
Test("翻译表：繁体、日语、英语条目一致，且每条都有译文", () =>
{
    var en = LoadLang("en");
    foreach (var code in new[] { "zh-TW", "ja", "en" })
    {
        var m = LoadLang(code);
        Assert(m.Count == en.Count && m.Keys.All(en.ContainsKey), $"{code} 条目数 {m.Count}，英语 {en.Count}");
        var empty = m.Where(kv => kv.Value.Trim().Length == 0 && kv.Key.Trim().Length > 0).Select(kv => kv.Key).ToList();
        Assert(empty.Count == 0, $"{code} 缺译文：{string.Join(" | ", empty.Take(3))}");
    }
});
Test("翻译表：占位符和换行与原文一致，格式化不会出错", () =>
{
    var ph = new System.Text.RegularExpressions.Regex(@"(?<!\{)\{(\d+)[^{}]*\}");
    foreach (var code in new[] { "zh-TW", "ja", "en" })
        foreach (var kv in LoadLang(code))
        {
            var a = ph.Matches(kv.Key).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            var b = ph.Matches(kv.Value).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            Assert(a.SequenceEqual(b), $"{code} 占位符不一致：{kv.Key} => {kv.Value}");
            int CountNl(string s) => s.Split(new[] { "\\n" }, StringSplitOptions.None).Length;
            Assert(CountNl(kv.Key) == CountNl(kv.Value), $"{code} 换行数不一致：{kv.Key}");
            if (a.Any()) string.Format(kv.Value, Enumerable.Range(0, 10).Select(i => (object)i).ToArray());
        }
});
Test("翻译表：切换语言后能查到译文，切回简体不变", () =>
{
    L.Use("en");
    Assert(L.T("垃圾清理") != "垃圾清理", "英语没有译文");
    Assert(L.F("共 {0} 条", 3).Contains("3"), "格式化失败");
    L.Use("zh-CN");
    Assert(L.T("垃圾清理") == "垃圾清理", "简体应保持原文");
});

Test("设置文件：保存一项不会丢掉其他项", () =>
{
    string old = AppSettings.FilePath;
    AppSettings.FilePath = Path.Combine(NewTemp(), "settings.ini");
    try
    {
        Assert(AppSettings.Get("lang") == null, "新文件应该没有设置");
        Assert(AppSettings.Set("lang", "ja") && AppSettings.Set("theme", "dark"), "保存失败");
        AppSettings.Set("lang", "en");
        Assert(AppSettings.Get("lang") == "en" && AppSettings.Get("theme") == "dark", "设置被覆盖或丢失");
        Assert(File.ReadAllLines(AppSettings.FilePath).Length == 2, "同一个键不应重复");
    }
    finally { AppSettings.FilePath = old; }
});

Console.WriteLine("Privacy");
var fakeReg = new Dictionary<string, int>();
Privacy.Read = (r, p, n) => fakeReg.TryGetValue($"{r}\\{p}\\{n}", out var v) ? v : (object?)null;
Privacy.Write = (r, p, n, v) => { string k = $"{r}\\{p}\\{n}"; if (v == null) fakeReg.Remove(k); else fakeReg[k] = v.Value; };
Test("隐私开关：关闭后状态正确，恢复默认后回到 Windows 默认值", () =>
{
    fakeReg.Clear();
    var items = Privacy.List();
    Assert(items.All(i => i.State == PrivacyState.Default), "空注册表应该都是默认");
    foreach (var i in items) Privacy.Set(i, true);
    Assert(Privacy.List().All(i => i.State == PrivacyState.Off), "关闭后应该都是已关闭");
    foreach (var i in items) Privacy.Set(i, false);
    var again = Privacy.List();
    Assert(again.All(i => i.State == PrivacyState.Default), "恢复后应该都是默认");
    int expected = items.SelectMany(i => i.Values).Count(v => v.Default != null);
    Assert(fakeReg.Count == expected, $"默认没有的值应被删掉：剩 {fakeReg.Count} 个，应为 {expected}");
});
Test("隐私开关：只改了一部分时显示“部分关闭”", () =>
{
    fakeReg.Clear();
    var item = Privacy.List().First(i => i.Values.Count > 1);
    var v = item.Values[0];
    Privacy.Write(v.Root, v.Path, v.Name, v.Off);
    Assert(Privacy.Evaluate(item) == PrivacyState.Partial, "应为部分关闭");
});
Test("隐私开关：在 Windows 设置里自己隐藏的显示为“已隐藏”，开关显示为关", () =>
{
    fakeReg.Clear();
    const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    fakeReg[$"User\\{adv}\\TaskbarDa"] = 0;
    fakeReg[$"User\\{adv}\\ShowCopilotButton"] = 0;
    var items = Privacy.List();
    var w = items.First(i => i.Id == "widgets"); var c = items.First(i => i.Id == "copilot");
    Assert(w.State == PrivacyState.Hidden && !w.IsOn, "小组件应为已隐藏");
    Assert(c.State == PrivacyState.Hidden && !c.IsOn, "Copilot 应为已隐藏");
    Privacy.Set(w, true);
    Assert(w.State == PrivacyState.Off, "从已隐藏点开关应彻底关闭");
    Privacy.Set(w, false);
    Assert(w.State == PrivacyState.Hidden, "恢复默认后用户自己的隐藏设置不受影响");
    fakeReg[$"User\\{adv}\\TaskbarDa"] = 1;
    Assert(Privacy.Evaluate(w) == PrivacyState.Default, "重新显示后应为默认");
});
Test("隐私开关：编号不重复，每个值关闭后都和默认值不同", () =>
{
    var items = Privacy.List();
    Assert(items.Select(i => i.Id).Distinct().Count() == items.Count, "编号重复");
    var same = items.SelectMany(i => i.Values).Where(v => v.Default == v.Off).Select(v => v.Name).ToList();
    Assert(same.Count == 0, "关闭值等于默认值：" + string.Join(",", same));
});

Console.WriteLine("StoreApps");
string AppLine(string name, bool nonRemovable = false) =>
    $"{name}\t{name}_1.0.0.0_x64__8wekyb3d8bbwe\t{name}_8wekyb3d8bbwe\tCN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond\t{nonRemovable}\tC:\\Apps\\{name}\t1.0.0.0";
Test("预装应用：系统组件和不可删除的不列出，认识的应用按建议归类", () =>
{
    string output = string.Join("\r\n", new[]
    {
        AppLine("king.com.CandyCrushSaga"), AppLine("Microsoft.BingWeather"), AppLine("Microsoft.WindowsCalculator"),
        AppLine("Microsoft.WindowsStore"), AppLine("Microsoft.VCLibs.140.00"), AppLine("Microsoft.HEIFImageExtension"),
        AppLine("Microsoft.Windows.Photos"), AppLine("Microsoft.Windows.DevHome"), AppLine("Microsoft.DesktopAppInstaller"),
        AppLine("Microsoft.BingNews", nonRemovable: true), AppLine("king.com.CandyCrushSaga"), "garbage line",
    });
    var apps = StoreApps.Parse(output);
    var names = apps.Select(a => a.Name).ToList();
    Assert(names.OrderBy(n => n).SequenceEqual(new[] { "king.com.CandyCrushSaga", "Microsoft.BingWeather", "Microsoft.Windows.DevHome", "Microsoft.WindowsCalculator" }),
        string.Join(",", names));
    Assert(apps.First(a => a.Name.StartsWith("king")).Advice == AppAdvice.Recommend, "Candy Crush 应建议删除");
    Assert(apps.First(a => a.Name == "Microsoft.BingWeather").Advice == AppAdvice.Optional, "天气应为看需要");
    var calc = apps.First(a => a.Name == "Microsoft.WindowsCalculator");
    Assert(calc.Advice == AppAdvice.Other && calc.Title == "Windows Calculator" && calc.Description == "Microsoft Corporation", calc.Title + "|" + calc.Description);
});
Test("预装应用：删除记录可读回，重新装回后自动去掉", () =>
{
    string old = OpLog.LogDir;
    OpLog.LogDir = Path.Combine(NewTemp(), "logs");
    try
    {
        Directory.CreateDirectory(OpLog.LogDir);
        File.WriteAllText(StoreApps.RemovedFile, "2026-10-22 10:00\tA_1\t甲\r\n2026-10-22 11:00\tB_1\t乙\r\n");
        var r = StoreApps.Removed();
        Assert(r.Count == 2 && r[0].Title == "乙", "应按时间倒序");
        StoreApps.ForgetReinstalled(new[] { new StoreApp { FamilyName = "A_1" } });
        Assert(StoreApps.Removed().Select(x => x.Title).SequenceEqual(new[] { "乙" }), "装回的应去掉");
    }
    finally { OpLog.LogDir = old; }
});

Console.WriteLine("Winget");
Test("软件更新：解析英文表格，跳过进度动画和总结行", () =>
{
    string output = "   - \r   \\ \r\r\n" +
        "Name                       Id                         Version      Available    Source\r\n" +
        "--------------------------------------------------------------------------------------\r\n" +
        "Git                        Git.Git                    2.40.0       2.47.1       winget\r\n" +
        "Mozilla Firefox (x64 en-US) Mozilla.Firefox           < 120.0      132.0.2      winget\r\n" +
        "Visual Studio Code         Microsoft.VisualStudioCode 1.94.0       1.95.3       winget\r\n" +
        "3 upgrades available.\r\n";
    var list = Winget.ParseTable(output);
    Assert(list.Count == 3, "应有 3 项：" + list.Count);
    Assert(list[0].Name == "Git" && list[0].Id == "Git.Git" && list[0].Version == "2.40.0" && list[0].Available == "2.47.1" && list[0].Source == "winget", list[0].Meta);
    Assert(list[1].Version == "< 120.0" && list[1].Id == "Mozilla.Firefox", list[1].Name + "|" + list[1].Id + "|" + list[1].Version);
});
Test("软件更新：中文表头和中文软件名（全角占两格）", () =>
{
    string output =
        "名称           ID                 版本     可用     源\n" +
        "-----------------------------------------------------\n" +
        "微信           Tencent.WeChat     3.9.10   3.9.12   winget\n" +
        "7-Zip 23.01    7zip.7zip          23.01    24.08    winget\n" +
        "2 升级可用。\n";
    var list = Winget.ParseTable(output);
    Assert(list.Count == 2, "应有 2 项：" + list.Count);
    Assert(list[0].Name == "微信" && list[0].Id == "Tencent.WeChat" && list[0].Available == "3.9.12", list[0].Name + "|" + list[0].Id + "|" + list[0].Available);
    Assert(list[1].Id == "7zip.7zip" && list[1].Version == "23.01", list[1].Id);
});
Test("软件更新：没有可更新的软件时返回空列表", () =>
{
    Assert(Winget.ParseTable("No installed package found matching input criteria.\r\n").Count == 0, "应为空");
});
Test("软件更新：被截断的 Id 用 export 结果补全，前缀不唯一时保持原样", () =>
{
    var list = new List<SoftwareUpdate> { new SoftwareUpdate { Id = "Microsoft.VisualStudio.2022.Comm…" }, new SoftwareUpdate { Id = "Foo.…" } };
    string json = "{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\" : \"Microsoft.VisualStudio.2022.Community\"},{\"PackageIdentifier\":\"Foo.A\"},{\"PackageIdentifier\":\"Foo.B\"}]}]}";
    Winget.ResolveIds(list, json);
    Assert(list[0].Id == "Microsoft.VisualStudio.2022.Community", list[0].Id);
    Assert(list[1].Id == "Foo.…", "前缀不唯一不应替换");
});

Console.WriteLine("Ports");
Test("监听端口：同一进程的 IPv4 和 IPv6 合并，能被其他电脑连上的排前面", () =>
{
    var any4 = System.Net.IPAddress.Any; var any6 = System.Net.IPAddress.IPv6Any;
    var lo4 = System.Net.IPAddress.Loopback; var lo6 = System.Net.IPAddress.IPv6Loopback;
    var list = Ports.Merge(new[]
    {
        new ListenSocket("TCP", lo4, 9100, 300),
        new ListenSocket("TCP", any4, 445, 4), new ListenSocket("TCP", any6, 445, 4),
        new ListenSocket("TCP", any4, 135, 900),
        new ListenSocket("TCP", lo4, 6379, 500), new ListenSocket("TCP", lo6, 6379, 500),
        new ListenSocket("UDP", any4, 5353, 700),
    });
    Assert(list.Count == 5, "应合并成 5 个：" + list.Count);
    Assert(list[0].Port == 445 && list[0].Addresses.Count == 2 && list[0].Risky, "445 应排第一且标注意");
    Assert(list[1].Port == 135 && list[1].Exposed && !list[1].Risky, "135 能连但不算风险");
    var redis = list.First(p => p.Port == 6379);
    Assert(!redis.Exposed && !redis.Risky, "只在本机监听的数据库不算风险");
    Assert(list.Take(3).All(p => p.Exposed) && list.Skip(3).All(p => !p.Exposed), "能被连上的应排在只有本机能连的前面");
});
Test("监听端口：认识常见端口，TCP 和 UDP 分开认", () =>
{
    Assert(Ports.Describe("TCP", 3389)?.Risky == true, "远程桌面应标注意");
    Assert(Ports.Describe("TCP", 49666) != null, "RPC 动态端口应认识");
    Assert(Ports.Describe("UDP", 3389) == null, "UDP 3389 不认识");
    Assert(Ports.Describe("TCP", 5353) == null && Ports.Describe("UDP", 5353) != null, "5353 只在 UDP 上认识");
    Assert(Ports.Describe("TCP", 51234) == null, "随机端口不认识");
});

Console.WriteLine("WindowsResidue");
Test("系统组件：解析 DISM 分析结果", () =>
{
    string output = "Deployment Image Servicing and Management tool\r\nVersion: 10.0.22621.2792\r\n\r\nImage Version: 10.0.22631.4460\r\n\r\n" +
        "[===========================100.0%==========================]\r\n" +
        "Component Store (WinSxS) information:\r\n\r\n" +
        "Windows Explorer Reported Size of Component Store : 9.65 GB\r\n\r\n" +
        "Actual Size of Component Store : 9.27 GB\r\n\r\n" +
        "    Shared with Windows : 5.49 GB\r\n    Backups and Disabled Features : 3.18 GB\r\n    Cache and Temporary Data :  512.30 MB\r\n\r\n" +
        "Date of Last Cleanup : 2024-10-03 09:12:45\r\n\r\n" +
        "Number of Reclaimable Packages : 7\r\nComponent Store Cleanup Recommended : Yes\r\n\r\nThe operation completed successfully.\r\n";
    var i = WindowsResidue.ParseAnalyze(output);
    Assert(i.ActualSize == (long)(9.27 * 1024 * 1024 * 1024), "实际大小：" + i.ActualSize);
    Assert(i.Backups == (long)(3.18 * 1024 * 1024 * 1024) && i.Cache == (long)(512.30 * 1024 * 1024), "备份/缓存");
    Assert(i.ReclaimablePackages == 7 && i.Recommended && i.LastCleanup == "2024-10-03 09:12:45", "包数/建议/日期");
    Assert(i.Estimated == i.Backups + i.Cache, "可清理 = 备份 + 缓存");
    Assert(!WindowsResidue.ParseAnalyze("Component Store Cleanup Recommended : No").Recommended, "No 应为不建议");
});
Test("系统组件：大小换算", () =>
{
    Assert(WindowsResidue.ParseSize("0 bytes") == 0, "0 bytes");
    Assert(WindowsResidue.ParseSize("1,024 bytes") == 1024, "千位分隔");
    Assert(WindowsResidue.ParseSize("1.50 KB") == 1536, "KB");
    Assert(WindowsResidue.ParseSize("2 GB") == 2L * 1024 * 1024 * 1024, "GB");
    Assert(WindowsResidue.ParseSize("") == 0 && WindowsResidue.ParseSize("n/a") == 0, "读不出来时为 0");
});

Console.WriteLine("DiskHealth");
Test("硬盘健康：解析输出，读不到的计数为空", () =>
{
    string output = "DISK\t0\tSamsung SSD 980 PRO 1TB\tSSD\tNVMe\t1000204886016\tHealthy\tOK\t5B2QGXA7\tGPT\tTrue\t8760\t41\t82\t3\t0\t0\t0\t0\t1520\tCD\r\n" +
                    "DISK\t1\tWD Elements\tHDD\tUSB\t2000398934016\tWarning\tOK\t1065\tMBR\tFalse\t\t\t\t\t\t\t\t\t\tE\r\n" +
                    "garbage line\r\n";
    var list = DiskHealth.Parse(output);
    Assert(list.Count == 2, "应有 2 块：" + list.Count);
    var a = list[0];
    Assert(a.Model == "Samsung SSD 980 PRO 1TB" && a.BusType == "NVMe" && a.PowerOnHours == 8760 && a.Wear == 3 && a.Temperature == 41, "第一块字段");
    Assert(a.Volumes.Select(v => v.Letter).SequenceEqual(new[] { "C", "D" }), "分区盘符");
    Assert(DiskHealth.Assess(a).Level == HealthLevel.Good, "健康应为良好");
    var b = list[1];
    Assert(!b.CountersRead && b.PowerOnHours == null && b.Wear == null, "读不到时为空");
    Assert(DiskHealth.Assess(b).Level == HealthLevel.Warn, "Windows 报警告应为需留意");
});
Test("硬盘健康：寿命用完、无法纠正的错误、没有状态时的判断", () =>
{
    var d = new PhysicalDiskInfo { Health = "Healthy", Operational = "OK", Wear = 95 };
    Assert(DiskHealth.Assess(d).Level == HealthLevel.Warn, "寿命 95% 应留意");
    d.Wear = 100;
    Assert(DiskHealth.Assess(d).Level == HealthLevel.Bad, "寿命用完应有问题");
    var e = new PhysicalDiskInfo { Health = "Healthy", Operational = "OK", ReadUncorrected = 2 };
    Assert(DiskHealth.Assess(e).Level == HealthLevel.Warn, "无法纠正的错误应留意");
    var u = new PhysicalDiskInfo { Health = "Unknown" };
    var r = DiskHealth.Assess(u);
    Assert(r.Level == HealthLevel.Unknown && r.Reasons.Count == 1, "没有状态应为未知并说明原因");
    Assert(DiskHealth.Assess(new PhysicalDiskInfo { Health = "Unhealthy", Wear = 95 }).Level == HealthLevel.Bad, "不正常优先");
});

Console.WriteLine("SpaceHints");
Test("大文件说明：系统文件不给删，下载的安装包可以删", () =>
{
    var r = new SpaceHints.Roots { Windows = "/w/Windows", ProgramFiles = "/w/Program Files", ProgramFilesX86 = "/w/Program Files (x86)", ProgramData = "/w/ProgramData", Profile = "/w/Users/a" };
    Assert(SpaceHints.Explain("/hiberfil.sys", false, r)?.Kind == HintKind.UseTool, "休眠文件");
    Assert(SpaceHints.Explain("/pagefile.sys", false, r)?.Kind == HintKind.Keep, "虚拟内存");
    Assert(SpaceHints.RecycleBlocked("/pagefile.sys", r) != null, "虚拟内存不能删");
    Assert(SpaceHints.RecycleBlocked("/w/Windows/System32/big.dll", r) != null, "Windows 目录不能删");
    Assert(SpaceHints.RecycleBlocked("/w/Program Files/App/app.exe", r) != null, "安装目录不能直接删");
    Assert(SpaceHints.Explain("/w/Windows/Installer/x.msi", false, r)?.Kind == HintKind.Keep, "Installer");
    Assert(SpaceHints.Explain("/w/Users/a/Downloads/setup.iso", false, r)?.Kind == HintKind.CanDelete, "下载的 iso");
    Assert(SpaceHints.RecycleBlocked("/w/Users/a/Downloads/setup.iso", r) == null, "下载的 iso 可以删");
    Assert(SpaceHints.RecycleBlocked("/w/Users/a/Videos/movie.mkv", r) == null && SpaceHints.Explain("/w/Users/a/Videos/movie.mkv", false, r) == null, "普通文件不说明但可以删");
    Assert(SpaceHints.Explain("/w/Users/a/AppData/Local/Docker/wsl/ext4.vhdx", false, r)?.Kind == HintKind.UseTool, "虚拟磁盘");
    Assert(SpaceHints.Explain("/w/Users/a/AppData/Roaming/Apple Computer/MobileSync/Backup", true, r)?.Kind == HintKind.UseTool, "iPhone 备份");
    Assert(SpaceHints.Explain("/w/Windows/WinSxS", true, r) != null, "WinSxS 文件夹");
});

Console.WriteLine("LogFiles");
Test("日志管理：默认只勾选今天以前的操作日志", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "pcopt-logs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(dir, "backup"));
    try
    {
        var today = new DateTime(2026, 10, 22);
        File.WriteAllText(Path.Combine(dir, "2026-10-01.log"), "old");
        File.SetLastWriteTime(Path.Combine(dir, "2026-10-01.log"), today.AddDays(-21));
        File.WriteAllText(Path.Combine(dir, "2026-10-22.log"), "today");
        File.SetLastWriteTime(Path.Combine(dir, "2026-10-22.log"), today.AddHours(9));
        File.WriteAllText(Path.Combine(dir, "removed-apps.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "backup", "a.reg"), "Windows Registry Editor Version 5.00");
        File.WriteAllText(Path.Combine(dir, ".write-test"), "");
        var list = LogFiles.List(dir, today);
        Assert(list.Count == 4, "应有 4 个文件：" + list.Count);
        Assert(list.Where(f => f.Selected).Select(f => f.Name).SequenceEqual(new[] { "2026-10-01.log" }), "只勾选旧日志");
        Assert(list.Single(f => f.Name == "a.reg").HasWarning && list.Single(f => f.Name == "removed-apps.txt").HasWarning, "备份和删除记录要提示");
        Assert(LogFiles.Preview(Path.Combine(dir, "2026-10-01.log")) == "old", "预览内容");
        string big = Path.Combine(dir, "big.log");
        File.WriteAllText(big, string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n")));
        string tail = LogFiles.Preview(big, 1000);
        Assert(tail.EndsWith("line 4999\n") && !tail.Contains("line 0\n") && tail.Length < 1100, "大文件只显示末尾");
        Assert(tail.Split('\n')[1].StartsWith("line "), "末尾第一行应该是完整的一行");
    }
    finally { Directory.Delete(dir, true); }
});

Test("持续记录：累积所有联过网的程序，进程号被重用时重新识别", () =>
{
    var r = new NetRecorder();
    var t0 = new DateTime(2026, 10, 24, 9, 0, 0);
    var info = new Dictionary<int, PidInfo>
    {
        [100] = new PidInfo(@"C:\App\a.exe", "A", @"C:\App\a.exe", false),
        [200] = new PidInfo(@"C:\Windows\svchost.exe", "svchost", @"C:\Windows\svchost.exe", true),
    };
    var s1 = new List<RemoteConn> { new RemoteConn(100, "1.1.1.1:443"), new RemoteConn(100, "2.2.2.2:443"), new RemoteConn(200, "3.3.3.3:80") };
    Assert(r.UnknownPids(s1).Count == 2, "第一次两个进程都要识别");
    var added = r.Add(t0, s1, info, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\app\A.exe" });
    Assert(added.Count == 2 && r.Apps.Count == 2, "应发现两个程序");
    Assert(r.Apps[0].Blocked && !r.Apps[1].Blocked, "已有防火墙规则的要标出来");

    var s2 = new List<RemoteConn> { new RemoteConn(100, "1.1.1.1:443") };
    Assert(r.UnknownPids(s2).Count == 0, "连续出现的进程不用重新识别");
    Assert(r.Add(t0.AddSeconds(5), s2, new Dictionary<int, PidInfo>()).Count == 0, "没有新程序");
    var a = r.Apps[0];
    Assert(a.Remotes.Count == 2 && a.FirstSeen == t0 && a.LastSeen == t0.AddSeconds(5), "地址和时间要累积");

    r.Add(t0.AddSeconds(6), new List<RemoteConn>(), new Dictionary<int, PidInfo>());
    var s3 = new List<RemoteConn> { new RemoteConn(100, "4.4.4.4:443") };
    Assert(r.UnknownPids(s3).SequenceEqual(new[] { 100 }), "进程号断开后再出现要重新识别");
    var reused = new Dictionary<int, PidInfo> { [100] = new PidInfo(@"C:\B\b.exe", "B", @"C:\B\b.exe", false) };
    Assert(r.Add(t0.AddSeconds(7), s3, reused).Count == 1 && r.Apps.Count == 3, "重用的进程号属于新程序");
    Assert(a.Remotes.Count == 2, "旧程序不应记上新程序的地址");

    r.Clear();
    Assert(r.Apps.Count == 0 && r.Samples == 0, "重新开始要清空");
});

Test("精确模式：联网事件的解析和 ETW 结构体大小", () =>
{
    byte[] V4(int pid, string d, string s, int dport, int sport)
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes(pid)); b.AddRange(BitConverter.GetBytes(100));
        b.AddRange(System.Net.IPAddress.Parse(d).GetAddressBytes()); b.AddRange(System.Net.IPAddress.Parse(s).GetAddressBytes());
        b.Add((byte)(dport >> 8)); b.Add((byte)dport); b.Add((byte)(sport >> 8)); b.Add((byte)sport);
        b.AddRange(new byte[8]);
        return b.ToArray();
    }
    var local = new HashSet<System.Net.IPAddress> { System.Net.IPAddress.Parse("192.168.1.5") };
    var e = V4(1234, "93.184.216.34", "192.168.1.5", 443, 50000);
    Assert(NetEvents.TryParse(12, e, e.Length, local, out int pid, out string remote) && pid == 1234 && remote == "93.184.216.34:443", "TCP 连接：" + remote);
    var r = V4(1234, "192.168.1.5", "8.8.8.8", 50001, 53);
    Assert(NetEvents.TryParse(43, r, r.Length, local, out _, out remote) && remote == "8.8.8.8:53", "地址反过来时取对方：" + remote);
    var lo = V4(1234, "127.0.0.1", "127.0.0.1", 80, 50002);
    Assert(!NetEvents.TryParse(10, lo, lo.Length, local, out _, out _), "本机回环不算联网");
    Assert(!NetEvents.TryParse(13, e, e.Length, local, out _, out _), "断开事件不处理");
    Assert(!NetEvents.TryParse(12, e, 10, local, out _, out _), "数据太短不处理");
    Assert(NetEvents.TryKey(12, e, e.Length, out var k1) && NetEvents.TryKey(12, V4(1234, "93.184.216.34", "192.168.1.5", 443, 50000), e.Length, out var k2) && k1.Equals(k2), "相同连接的 key 相同");

    var b6 = new List<byte>();
    b6.AddRange(BitConverter.GetBytes(77)); b6.AddRange(BitConverter.GetBytes(0));
    b6.AddRange(System.Net.IPAddress.Parse("2606:4700::1111").GetAddressBytes()); b6.AddRange(System.Net.IPAddress.Parse("fe80::1").GetAddressBytes());
    b6.AddRange(new byte[] { 0x01, 0xBB, 0xC3, 0x50 });
    var a6 = b6.ToArray();
    Assert(NetEvents.TryParse(28, a6, a6.Length, local, out pid, out remote) && pid == 77 && remote == "[2606:4700::1111]:443", "IPv6：" + remote);

    if (IntPtr.Size == 8)
    {
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.EVENT_TRACE_PROPERTIES)) == 120, "EVENT_TRACE_PROPERTIES 应为 120 字节");
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.EVENT_TRACE)) == 88, "EVENT_TRACE 应为 88 字节");
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.TRACE_LOGFILE_HEADER)) == 280, "TRACE_LOGFILE_HEADER 应为 280 字节");
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.EVENT_TRACE_LOGFILE)) == 448, "EVENT_TRACE_LOGFILE 应为 448 字节");
        Assert(NativeMethods.EVENT_RECORD_UserData == 96, "UserData 偏移应为 96");
    }
});

Console.WriteLine($"\n{passed} 通过，{failed} 失败");
return failed == 0 ? 0 : 1;
