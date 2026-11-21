using System.Diagnostics;
using System.Windows;

namespace WinNetworkManager.App.Services;

/// <summary>Small UI-side actions: clipboard, opening Explorer, save dialogs.</summary>
public static class ShellActions
{
    /// <summary>Copies text to the clipboard (retries briefly when another app holds it).</summary>
    public static bool Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(30);
            }
        }

        return false;
    }

    /// <summary>Opens Explorer with <paramref name="path"/> selected.</summary>
    public static void ShowInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Shows a save dialog; returns the chosen path or null.</summary>
    public static string? AskSavePath(string suggestedName, string filter, string? folder, string title = "Export")
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}
