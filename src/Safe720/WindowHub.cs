using System;
using System.Collections.Generic;
using System.Windows;

namespace Safe720;

/// <summary>功能窗口单例：悬浮球菜单、托盘菜单、其他窗口里的链接都从这里打开，已打开就切到前台。</summary>
public static class WindowHub
{
    private static readonly Dictionary<Type, Window> Open = new();

    public static T Show<T>(Func<T> create) where T : Window
    {
        if (Open.TryGetValue(typeof(T), out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return (T)existing;
        }

        T w = create();
        Open[typeof(T)] = w;
        w.Closed += (_, _) => Open.Remove(typeof(T));
        w.Show();
        w.Activate();
        return w;
    }

    public static T Show<T>() where T : Window, new() => Show(() => new T());
}
