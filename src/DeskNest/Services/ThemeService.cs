using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskNest.Services;

public static class ThemeService
{
    public static void Apply(string mode)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dark = mode == "dark" || (mode == "system" && (int?)key?.GetValue("AppsUseLightTheme") == 0);
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["WindowBrush"] = "#2A2A2A",
                ["ContentBrush"] = "#202022",
                ["SidebarBrush"] = "#28282A",
                ["CardBrush"] = "#F42B2B2E",
                ["LabelBrush"] = "#E5FFFFFF",
                ["SecondaryBrush"] = "#99EBEBF5",
                ["FillBrush"] = "#33787880",
                ["LineBrush"] = "#28EBEBF5",
                ["AccentBrush"] = "#0A84FF",
                ["SelectionBrush"] = "#380A84FF",
            }
            : new Dictionary<string, string>
            {
                ["WindowBrush"] = "#ECECEC",
                ["ContentBrush"] = "#FAFAFA",
                ["SidebarBrush"] = "#F1F1F3",
                ["CardBrush"] = "#F4FAFAFC",
                ["LabelBrush"] = "#DE17171B",
                ["SecondaryBrush"] = "#8F3C3C43",
                ["FillBrush"] = "#16787880",
                ["LineBrush"] = "#1B3C3C43",
                ["AccentBrush"] = "#007AFF",
                ["SelectionBrush"] = "#18007AFF",
            };
        foreach (var color in colors)
        {
            Application.Current.Resources[color.Key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color.Value));
        }
    }
}
