using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Safe720.Services;

/// <summary>
/// 临时文件清理：删除用户临时目录与 C:\Windows\Temp 下超过 <see cref="MinAge"/> 未修改的文件，
/// 再移除清空后的子目录。规则见 <see cref="FolderCleaner"/>。
/// Windows\Temp 非管理员时基本都会被跳过。
/// </summary>
public sealed class TempCleaner
{
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    public CleanStats Clean() => Clean(GetRoots());

    public CleanStats Clean(IEnumerable<string> roots) => FolderCleaner.Run(roots, MinAge, dryRun: false);

    /// <summary>
    /// 只返回 Windows 默认的两个临时目录。用户临时目录不直接信任 %TEMP%：
    /// 有人把 TEMP 改成 D:\、桌面或工作目录，按 %TEMP% 清会把那里 24 小时前的文件全删掉（而且是每天静默自动清）。
    /// %TEMP% 不是默认的 AppData\Local\Temp 时直接跳过，宁可少清。
    /// </summary>
    public static IEnumerable<string> GetRoots()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        string userTemp = LongPath(Path.GetTempPath());
        if (SamePath(userTemp, expected) && Directory.Exists(expected))
            yield return expected;

        string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (Directory.Exists(winTemp))
            yield return winTemp;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint len);

    /// <summary>
    /// %TEMP% 常是 8.3 短路径（用户名带点或较长时如 C:\Users\CHEN~1.ZHU\...），不展开就和长路径对不上。
    /// </summary>
    private static string LongPath(string path)
    {
        var sb = new StringBuilder(1024);
        uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }
}
