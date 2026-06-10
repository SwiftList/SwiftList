using System;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using SwiftList.Core;
using SwiftList.PluginSdk;

namespace SwiftList.Core.Hook
{
    /// <summary>
    /// Handles window classification and path tracking for ExplorerTracker,
    /// delegating path collection to registered IActivePathCollector plugins.
    /// </summary>
    internal sealed class ExplorerWindowClassifier
    {
        private readonly ExplorerTracker _tracker;
        private readonly FileDialogNavigationTracker _dialogTracker;

        public ExplorerWindowClassifier(ExplorerTracker tracker, FileDialogNavigationTracker dialogTracker)
        {
            _tracker = tracker;
            _dialogTracker = dialogTracker;
        }

        public void CheckActiveWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                if (_tracker.IsActiveWindowDialog && _tracker.ActiveHwnd != IntPtr.Zero && !ExplorerNativeHooks.IsWindow(_tracker.ActiveHwnd))
                {
                    _tracker.Deactivate();
                }

                if (IsFocusChangeIgnored(hwnd))
                    return;

                IntPtr dialogHwnd = FindMatchingDialogWindow(hwnd, out var adapter);
                if (dialogHwnd != IntPtr.Zero && adapter != null)
                {
                    TrackFileDialogWindow(dialogHwnd);
                    return;
                }

                IntPtr rootHwnd = ExplorerNativeHooks.GetAncestor(hwnd, ExplorerNativeHooks.GA_ROOTOWNER);
                if (rootHwnd == IntPtr.Zero) rootHwnd = hwnd;

                bool isDesktop = ExplorerNativeHooks.IsDesktopWindow(rootHwnd, out string windowClassName);
                Logger.Log($"[ExplorerTracker] Active window: HWND=0x{hwnd:X}, Root=0x{rootHwnd:X}, Class={windowClassName}, isDesktop={isDesktop}", LogLevel.Debug);

                // Resolve the actual focused control handle inside the active window's thread
                IntPtr focusedHwnd = IntPtr.Zero;
                string activeClassName = string.Empty;
                try
                {
                    uint threadId = KeyboardNativeMethods.GetWindowThreadProcessId(rootHwnd, out _);
                    var guiInfo = new KeyboardNativeMethods.GUITHREADINFO();
                    guiInfo.cbSize = Marshal.SizeOf(guiInfo);
                    if (KeyboardNativeMethods.GetGUIThreadInfo(threadId, ref guiInfo) && guiInfo.hwndFocus != IntPtr.Zero)
                    {
                        focusedHwnd = guiInfo.hwndFocus;
                        var sbActiveCls = new StringBuilder(256);
                        KeyboardNativeMethods.GetClassName(focusedHwnd, sbActiveCls, sbActiveCls.Capacity);
                        activeClassName = sbActiveCls.ToString();
                    }
                }
                catch { }

                if (focusedHwnd == IntPtr.Zero)
                {
                    focusedHwnd = hwnd;
                    var sbActiveCls = new StringBuilder(256);
                    ExplorerNativeHooks.GetClassName(hwnd, sbActiveCls, sbActiveCls.Capacity);
                    activeClassName = sbActiveCls.ToString();
                }

                // Get process name of root window
                string processName = "Unknown";
                try
                {
                    ExplorerNativeHooks.GetWindowThreadProcessId(rootHwnd, out uint pid);
                    if (pid != 0)
                    {
                        using (var proc = System.Diagnostics.Process.GetProcessById((int)pid))
                        {
                            processName = proc.ProcessName;
                        }
                    }
                }
                catch { }

                // Delegate active path collection to registered plugins
                var collectors = SwiftList.PluginSdk.ActivePathCollectorRegistry.GetCollectors();
                bool handledByPlugin = false;

                foreach (var collector in collectors)
                {
                    try
                    {
                        if (collector.CanHandle(windowClassName))
                        {
                            string? activePath = collector.TryGetPath(focusedHwnd, activeClassName, rootHwnd, windowClassName, processName);
                            if (!string.IsNullOrEmpty(activePath))
                            {
                                handledByPlugin = true;
                                _tracker.IsExplorerOrDesktopActive = true;
                                _tracker.IsDesktop = isDesktop;
                                _tracker.IsActiveWindowDialog = false;
                                _tracker.IsActiveWindowExplorer = !isDesktop && windowClassName.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase);
                                _tracker.LastActiveExplorerClassName = windowClassName;
                                _tracker.ActiveHwnd = rootHwnd;

                                if (rootHwnd != _tracker.LastActiveHwnd)
                                {
                                    _tracker.LastActiveHwnd = rootHwnd;
                                    var windowTitle = new StringBuilder(256);
                                    ExplorerNativeHooks.GetWindowText(rootHwnd, windowTitle, windowTitle.Capacity);
                                    _tracker.RaiseExplorerActivated(rootHwnd, windowTitle.ToString(), windowClassName, isDesktop);
                                }

                                if (_dialogTracker.LastActiveExplorerPath != activePath)
                                    _dialogTracker.SetLastActiveExplorerPath(activePath);

                                if (activePath != _tracker.LastPath)
                                {
                                    _tracker.LastPath = activePath;
                                    _tracker.RaisePathCaptured(activePath, isDesktop);
                                }
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ExplorerTracker] Error invoking active path collector '{collector.Name}': {ex.Message}", LogLevel.Error);
                    }
                }

                if (handledByPlugin)
                {
                    return;
                }

                var matchedAdapter = FileDialogAdapterRegistry.GetMatchingAdapter(rootHwnd, windowClassName, processName);
                if (matchedAdapter != null)
                {
                    _tracker.IsExplorerOrDesktopActive = true;
                    _tracker.IsDesktop = false;
                    _tracker.ActiveHwnd = rootHwnd;
                    _tracker.IsActiveWindowExplorer = false;
                }
                else
                {
                    var matchedInlineAdapter = InlineSearchAdapterRegistry.GetMatchingAdapter(rootHwnd, windowClassName, processName);
                    if (matchedInlineAdapter != null)
                    {
                        _tracker.IsExplorerOrDesktopActive = false;
                        _tracker.IsDesktop = false;
                        _tracker.IsActiveWindowExplorer = false;
                        _tracker.ActiveHwnd = rootHwnd;

                        if (rootHwnd != _tracker.LastActiveHwnd)
                        {
                            _tracker.LastActiveHwnd = rootHwnd;
                            var windowTitle = new StringBuilder(256);
                            ExplorerNativeHooks.GetWindowText(rootHwnd, windowTitle, windowTitle.Capacity);
                            _tracker.RaiseExplorerActivated(rootHwnd, windowTitle.ToString(), windowClassName, false);
                        }
                    }
                    else
                    {
                        if (_tracker.IsActiveWindowDialog && _tracker.ActiveHwnd != IntPtr.Zero)
                        {
                            IntPtr fgHwnd = ExplorerNativeHooks.GetForegroundWindow();
                            if (IsDescendantOrOwned(_tracker.ActiveHwnd, fgHwnd) || IsImeWindow(fgHwnd))
                            {
                                return;
                            }
                        }
                        _tracker.Deactivate();
                    }
                }
            }
            catch (Exception ex)
            {
                _tracker.RaiseError(ex.Message);
            }
        }

        private bool IsFocusChangeIgnored(IntPtr hwnd)
        {
            var sbClass = new StringBuilder(256);
            ExplorerNativeHooks.GetClassName(hwnd, sbClass, sbClass.Capacity);
            if (sbClass.ToString().Contains("InputSwitch", StringComparison.OrdinalIgnoreCase))
                return true;

            ExplorerNativeHooks.GetWindowThreadProcessId(hwnd, out uint activePid);
            if (activePid == Environment.ProcessId || (activePid != 0 && activePid == _tracker.AppProcessId))
                return true;

            // If there is an active tracked window, ignore focus changes to other windows in the SAME process.
            // This prevents false deactivation when autocomplete dropdowns, tooltips, or child controls of the active dialog are shown or updated.
            if (_tracker.ActiveHwnd != IntPtr.Zero)
            {
                ExplorerNativeHooks.GetWindowThreadProcessId(_tracker.ActiveHwnd, out uint activeTrackerPid);
                if (activePid != 0 && activePid == activeTrackerPid)
                    return true;
            }

            return false;
        }

        private void TrackFileDialogWindow(IntPtr mainDialog)
        {
            _tracker.IsExplorerOrDesktopActive = true;
            _tracker.IsDesktop = false;
            _tracker.ActiveHwnd = mainDialog;

            _dialogTracker.HandleDialogSeen(mainDialog, _tracker.ActiveAdapter);

            string? activePath = _tracker.ActiveAdapter?.GetCurrentPath(mainDialog);
            _tracker.LastPath = !string.IsNullOrEmpty(activePath) ? activePath : string.Empty;

            var windowTitle = new StringBuilder(256);
            ExplorerNativeHooks.GetWindowText(mainDialog, windowTitle, windowTitle.Capacity);

            StringBuilder sbCls2 = new StringBuilder(256);
            ExplorerNativeHooks.GetClassName(mainDialog, sbCls2, sbCls2.Capacity);

            if (mainDialog != _tracker.LastActiveHwnd)
            {
                _tracker.LastActiveHwnd = mainDialog;
                _tracker.RaiseExplorerActivated(mainDialog, windowTitle.ToString(), sbCls2.ToString(), false);
            }

            _tracker.RaisePathCaptured(_tracker.LastPath, false);
        }

        private IntPtr FindMatchingDialogWindow(IntPtr hwnd, out IFileDialogAdapter? adapter)
        {
            IntPtr current = hwnd;
            while (current != IntPtr.Zero)
            {
                var sbClass = new StringBuilder(256);
                ExplorerNativeHooks.GetClassName(current, sbClass, sbClass.Capacity);
                string className = sbClass.ToString();

                ExplorerNativeHooks.GetWindowThreadProcessId(current, out uint pid);
                string processName = "Unknown";
                if (pid != 0)
                {
                    try
                    {
                        using (var proc = System.Diagnostics.Process.GetProcessById((int)pid))
                        {
                            processName = proc.ProcessName;
                        }
                    }
                    catch { }
                }

                var matched = FileDialogAdapterRegistry.GetMatchingAdapter(current, className, processName);
                if (matched != null)
                {
                    adapter = matched;
                    return current;
                }

                current = ExplorerNativeHooks.GetParent(current);
            }

            adapter = null;
            return IntPtr.Zero;
        }

        private bool IsDescendantOrOwned(IntPtr parent, IntPtr child)
        {
            if (parent == IntPtr.Zero || child == IntPtr.Zero) return false;
            if (parent == child) return true;

            IntPtr current = child;
            while (current != IntPtr.Zero)
            {
                if (current == parent) return true;
                IntPtr temp = ExplorerNativeHooks.GetParent(current);
                if (temp == IntPtr.Zero || temp == current) break;
                current = temp;
            }

            IntPtr rootOwner = ExplorerNativeHooks.GetAncestor(child, ExplorerNativeHooks.GA_ROOTOWNER);
            if (rootOwner == parent) return true;

            return false;
        }

        private bool IsImeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var sbClass = new StringBuilder(256);
            ExplorerNativeHooks.GetClassName(hwnd, sbClass, sbClass.Capacity);
            string fgClass = sbClass.ToString();
            return fgClass.Contains("IME", StringComparison.OrdinalIgnoreCase) ||
                   fgClass.Contains("Candidate", StringComparison.OrdinalIgnoreCase) ||
                   fgClass.Contains("InputTip", StringComparison.OrdinalIgnoreCase) ||
                   fgClass.Contains("InputSwitch", StringComparison.OrdinalIgnoreCase);
        }
    }
}
