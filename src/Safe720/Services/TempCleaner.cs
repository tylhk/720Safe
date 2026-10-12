using System;
using System.Collections.Generic;
using System.IO;

namespace Safe720.Services;

/// <summary>
/// 临时文件清理：删除用户 %TEMP% 与 C:\Windows\Temp 下超过 <see cref="MinAge"/> 未修改的文件，
/// 再移除清空后的子目录。规则见 <see cref="FolderCleaner"/>。
/// Windows\Temp 非管理员时基本都会被跳过。
/// </summary>
public sealed class TempCleaner
{
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    public CleanStats Clean() => Clean(GetRoots());

    public CleanStats Clean(IEnumerable<string> roots) => FolderCleaner.Run(roots, MinAge, dryRun: false);

    public static IEnumerable<string> GetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string userTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (seen.Add(userTemp)) yield return userTemp;

        string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (seen.Add(winTemp)) yield return winTemp;
    }
}
