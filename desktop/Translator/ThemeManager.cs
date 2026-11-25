using System.Windows;
using Microsoft.Win32;

namespace RenpyTranslator;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// 应用主题管理：支持「跟随系统 / 浅色 / 深色」三种模式，并在系统深浅色切换时实时更新。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightThemeUri = "Themes/Light.xaml";
    private const string DarkThemeUri = "Themes/Dark.xaml";
    private static bool subscribed;

    public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;
    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;

    public static AppThemeMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppThemeMode.Light,
        "dark" => AppThemeMode.Dark,
        _ => AppThemeMode.System
    };

    public static string FormatMode(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "light",
        AppThemeMode.Dark => "dark",
        _ => "system"
    };

    public static string DisplayName(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "浅色",
        AppThemeMode.Dark => "深色",
        _ => "跟随系统"
    };

    /// <summary>按用户模式与系统当前深浅色解析最终是否启用深色主题。</summary>
    public static bool ResolveDark(AppThemeMode mode, bool systemIsDark) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => systemIsDark
    };

    /// <summary>读取 Windows 应用主题偏好；键值缺失或受限时默认回退浅色。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>启动时初始化主题；overrideMode 非空时仅用于当次进程预览（如截图），不改写用户设置。</summary>
    public static void Initialize(AppThemeMode? overrideMode = null)
    {
        var initial = overrideMode ?? ParseMode(Core.ReadSetting(Core.ThemeSettingKey, "system"));
        SetMode(initial, persist: false, force: true);
        if (!subscribed)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            subscribed = true;
        }
    }

    public static void Shutdown()
    {
        if (!subscribed) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        subscribed = false;
    }

    public static void SetMode(AppThemeMode mode, bool persist = true) => SetMode(mode, persist, force: false);

    private static void SetMode(AppThemeMode mode, bool persist, bool force)
    {
        var modeChanged = CurrentMode != mode;
        CurrentMode = mode;
        if (persist) Core.WriteSetting(Core.ThemeSettingKey, FormatMode(mode));

        var targetDark = ResolveDark(mode, IsSystemDark());
        var paletteChanged = targetDark != IsDark;
        if (force || paletteChanged)
        {
            IsDark = targetDark;
            ApplyPalette(targetDark);
            ApplyToOpenWindows(targetDark);
        }

        if (force || modeChanged || paletteChanged) ThemeChanged?.Invoke();
    }

    /// <summary>响应 Windows 浅色/深色系统切换；仅在跟随系统模式下生效。</summary>
    public static void RefreshSystemTheme()
    {
        if (CurrentMode != AppThemeMode.System) return;
        var targetDark = IsSystemDark();
        if (targetDark == IsDark) return;
        IsDark = targetDark;
        ApplyPalette(targetDark);
        ApplyToOpenWindows(targetDark);
        ThemeChanged?.Invoke();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(RefreshSystemTheme);
    }

    private static void ApplyPalette(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        var merged = app.Resources.MergedDictionaries;
        var targetUri = new Uri(dark ? DarkThemeUri : LightThemeUri, UriKind.Relative);
        var next = new ResourceDictionary { Source = targetUri };

        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString ?? "";
            if (source.EndsWith(LightThemeUri, StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(DarkThemeUri, StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = next;
                return;
            }
        }
        merged.Insert(0, next);
    }

    private static void ApplyToOpenWindows(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        foreach (Window window in app.Windows) WindowChromeHelper.ApplyDwmTheme(window, dark);
    }
}
