using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using SwiftList.Tutorial.Models;

namespace SwiftList.Tutorial.Helpers
{
    public static class ProgressTracker
    {
        public static void Track(int currentStep, ObservableCollection<TodoItem> todoItems)
        {
            IntPtr fgHWnd = Win32Helper.GetForegroundWindow();
            if (fgHWnd == IntPtr.Zero) return;

            string procName = Win32Helper.GetProcessNameFromWindow(fgHWnd);
            bool isSwiftListActive = procName.Equals("SwiftList.App", StringComparison.OrdinalIgnoreCase);

            if (currentStep == 1)
            {
                if (isSwiftListActive)
                {
                    // Todo 1: SwiftList active
                    if (todoItems.Count > 0 && !todoItems[0].IsCompleted)
                        todoItems[0].IsCompleted = true;

                    // Todo 2: Query text contains "sl_logo"
                    string query = Win32Helper.GetForegroundSearchText(fgHWnd);
                    if (query.Contains("sl_logo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (todoItems.Count > 1 && !todoItems[1].IsCompleted)
                            todoItems[1].IsCompleted = true;
                    }
                }
                else
                {
                    // Todo 3: If 1 & 2 are completed, and SwiftList is closed, mark result opened
                    if (todoItems.Count > 2 && todoItems[0].IsCompleted && todoItems[1].IsCompleted && !todoItems[2].IsCompleted)
                    {
                        todoItems[2].IsCompleted = true;
                    }
                }
            }
            else if (currentStep == 2)
            {
                if (isSwiftListActive)
                {
                    // Todo 1: Query contains "sl_demo"
                    string query = Win32Helper.GetForegroundSearchText(fgHWnd);
                    if (query.Contains("sl_demo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (todoItems.Count > 0 && !todoItems[0].IsCompleted)
                            todoItems[0].IsCompleted = true;
                    }

                    // Todo 3: Actions menu opened
                    if (Win32Helper.IsActionsMenuOpen(fgHWnd))
                    {
                        if (todoItems.Count > 2 && !todoItems[2].IsCompleted)
                            todoItems[2].IsCompleted = true;
                    }
                }

                // Todo 2: Check swiftlist_copied.tmp temp file (very robust, bypasses clipboard COM lock issues)
                if (todoItems.Count > 1 && todoItems[0].IsCompleted && !todoItems[1].IsCompleted)
                {
                    try
                    {
                        string tempPath = Path.Combine(Path.GetTempPath(), "swiftlist_copied.tmp");
                        if (File.Exists(tempPath))
                        {
                            string file = File.ReadAllText(tempPath);
                            if (file != null && file.Contains("sl_demo", StringComparison.OrdinalIgnoreCase))
                            {
                                todoItems[1].IsCompleted = true;
                                try { File.Delete(tempPath); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (currentStep == 3)
            {
                if (isSwiftListActive)
                {
                    string query = Win32Helper.GetForegroundSearchText(fgHWnd);

                    // Todo 1: Math expression (e.g. 50 + 50)
                    if (query.Contains("50") && query.Contains("+"))
                    {
                        if (todoItems.Count > 0 && !todoItems[0].IsCompleted)
                            todoItems[0].IsCompleted = true;
                    }

                    // Todo 2: Env var %temp%
                    if (query.Contains("%temp%"))
                    {
                        if (todoItems.Count > 1 && !todoItems[1].IsCompleted)
                            todoItems[1].IsCompleted = true;
                    }

                    // Todo 3: gg SwiftList
                    if (query.Contains("gg ") && query.Contains("SwiftList", StringComparison.OrdinalIgnoreCase))
                    {
                        if (todoItems.Count > 2 && !todoItems[2].IsCompleted)
                            todoItems[2].IsCompleted = true;
                    }
                }
            }
            else if (currentStep == 4)
            {
                bool isExplorerActive = procName.Equals("explorer", StringComparison.OrdinalIgnoreCase);

                // Todo 1: Explorer is active
                if (isExplorerActive)
                {
                    if (todoItems.Count > 0 && !todoItems[0].IsCompleted)
                        todoItems[0].IsCompleted = true;
                }

                // Todo 2: Inline search window exists and is visible
                IntPtr inlineHwnd = Win32Helper.FindWindow(null, "SwiftList Inline");
                if (inlineHwnd != IntPtr.Zero)
                {
                    if (todoItems.Count > 1 && !todoItems[1].IsCompleted)
                        todoItems[1].IsCompleted = true;
                }

                // Todo 3: Redirect completed (inline search closed and Explorer active again)
                if (todoItems.Count > 2 && todoItems[0].IsCompleted && todoItems[1].IsCompleted && !todoItems[2].IsCompleted)
                {
                    if (inlineHwnd == IntPtr.Zero && isExplorerActive)
                    {
                        todoItems[2].IsCompleted = true;
                    }
                }
            }
            else if (currentStep == 5)
            {
                bool isUserAway = !procName.Equals("SwiftList.Tutorial", StringComparison.OrdinalIgnoreCase);
                if (isUserAway)
                {
                    if (todoItems.Count > 0 && !todoItems[0].IsCompleted)
                        todoItems[0].IsCompleted = true;

                    bool ctrlPressed = (Win32Helper.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Win32Helper.GetAsyncKeyState(0x11) & 1) != 0;
                    bool gPressed = (Win32Helper.GetAsyncKeyState(0x47) & 0x8000) != 0 || (Win32Helper.GetAsyncKeyState(0x47) & 1) != 0;
                    if (ctrlPressed && gPressed)
                    {
                        if (todoItems.Count > 1 && !todoItems[1].IsCompleted)
                            todoItems[1].IsCompleted = true;
                    }
                }
                else
                {
                    // Fallback: If they focused back on the Tutorial window, and they had already switched away (Todo 1 completed),
                    // we can automatically mark Todo 2 completed as a fail-safe.
                    if (todoItems.Count > 1 && todoItems[0].IsCompleted && !todoItems[1].IsCompleted)
                    {
                        todoItems[1].IsCompleted = true;
                    }
                }
            }
        }
    }
}
