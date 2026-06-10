using System;
using System.Text;
using System.Runtime.InteropServices;
using SwiftList.Core;
using SwiftList.PluginSdk;

namespace SwiftList.Core.Hook
{
    public class ExplorerTracker : IDisposable
    {
        private ExplorerNativeHooks.WinEventDelegate? _foregroundHookDelegate;
        private ExplorerNativeHooks.WinEventDelegate? _nameChangeHookDelegate;
        private ExplorerNativeHooks.WinEventDelegate? _locationChangeHookDelegate;
        private IntPtr _hForegroundHook = IntPtr.Zero;
        private IntPtr _hNameChangeHook = IntPtr.Zero;
        private IntPtr _hLocationChangeHook = IntPtr.Zero;
        private bool _isRunning;

        private readonly FileDialogNavigationTracker _dialogTracker = new();
        private readonly ExplorerWindowClassifier _classifier;

        // Internal state exposed to ExplorerWindowClassifier
        public string? LastPath { get; set; }
        public IntPtr LastActiveHwnd { get; set; }

        public string? LastActiveExplorerPath => _dialogTracker.LastActiveExplorerPath;
        public string? LastActiveExplorerClassName { get; set; }
        public bool IsExplorerOrDesktopActive { get; set; }
        public bool IsDesktop { get; set; }

        private bool _isActiveWindowDialog;
        public bool IsActiveWindowDialog { get => _isActiveWindowDialog; set => _isActiveWindowDialog = value; }
        public bool IsActiveWindowExplorer { get; set; }

        public IFileDialogAdapter? ActiveAdapter { get; private set; }
        public IInlineSearchAdapter? ActiveInlineAdapter { get; private set; }

        private IntPtr _activeHwnd;
        public IntPtr ActiveHwnd
        {
            get => _activeHwnd;
            set
            {
                _activeHwnd = value;
                if (_activeHwnd != IntPtr.Zero)
                {
                    var sbClass = new StringBuilder(256);
                    ExplorerNativeHooks.GetClassName(_activeHwnd, sbClass, sbClass.Capacity);
                    string className = sbClass.ToString();
                    string processName = GetProcessName(_activeHwnd);
                    ActiveAdapter = FileDialogAdapterRegistry.GetMatchingAdapter(_activeHwnd, className, processName);
                    _isActiveWindowDialog = ActiveAdapter != null;
                    ActiveInlineAdapter = InlineSearchAdapterRegistry.GetMatchingAdapter(_activeHwnd, className, processName);
                }
                else
                {
                    ActiveAdapter = null;
                    _isActiveWindowDialog = false;
                    ActiveInlineAdapter = null;
                }
            }
        }
        public void SetActiveInlineAdapterDirectly(IInlineSearchAdapter? adapter, IntPtr hwnd)
        {
            ActiveInlineAdapter = adapter;
            _activeHwnd = hwnd;
            IsExplorerOrDesktopActive = adapter != null;

            if (adapter != null && hwnd != IntPtr.Zero)
            {
                var windowTitle = new StringBuilder(256);
                ExplorerNativeHooks.GetWindowText(hwnd, windowTitle, windowTitle.Capacity);

                var sbClass = new StringBuilder(256);
                ExplorerNativeHooks.GetClassName(hwnd, sbClass, sbClass.Capacity);

                RaiseExplorerActivated(hwnd, windowTitle.ToString(), sbClass.ToString(), false);
            }
        }

        public string? ActivePath => LastPath;
        public uint AppProcessId { get; set; }

        public event Action<IntPtr, string, string, bool>? OnExplorerActivated;
        public event Action? OnExplorerDeactivated;
        public event Action<string, bool>? OnPathCaptured;
        public event Action? OnActiveWindowMoved;
        public event Action<string>? OnError;

        private string GetProcessName(IntPtr hwnd)
        {
            try
            {
                ExplorerNativeHooks.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0)
                {
                    using (var proc = System.Diagnostics.Process.GetProcessById((int)pid))
                    {
                        return proc.ProcessName;
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        public void UpdateActiveWindow(IntPtr hwnd, string title, string className, bool isDesktop)
        {
            ActiveHwnd = hwnd;
            IsExplorerOrDesktopActive = true;
            IsDesktop = isDesktop;
            IsActiveWindowExplorer = className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase);
            if (!IsActiveWindowDialog)
            {
                LastActiveExplorerClassName = className;
            }
            RaiseExplorerActivated(hwnd, title, className, isDesktop);
        }

        public void DeactivateWindow()
        {
            Deactivate();
        }

        public void UpdatePath(string path, bool isDesktop)
        {
            LastPath = path;
            if (!IsActiveWindowDialog)
            {
                _dialogTracker.SetLastActiveExplorerPath(path);
            }
            RaisePathCaptured(path, isDesktop);
        }

        public void MoveActiveWindow()
        {
            OnActiveWindowMoved?.Invoke();
        }

        public void RaiseErrorExternal(string msg)
        {
            RaiseError(msg);
        }

        internal void RaiseExplorerActivated(IntPtr hwnd, string title, string cls, bool isDesktop)
            => OnExplorerActivated?.Invoke(hwnd, title, cls, isDesktop);
        internal void RaisePathCaptured(string path, bool isDesktop)
            => OnPathCaptured?.Invoke(path, isDesktop);
        internal void RaiseError(string msg)
            => OnError?.Invoke(msg);

        public ExplorerTracker()
        {
            _classifier = new ExplorerWindowClassifier(this, _dialogTracker);
        }

        public void Start()
        {
            if (_isRunning) return;

            _foregroundHookDelegate = new ExplorerNativeHooks.WinEventDelegate(WinEventProc);
            _nameChangeHookDelegate = new ExplorerNativeHooks.WinEventDelegate(WinEventProc);
            _locationChangeHookDelegate = new ExplorerNativeHooks.WinEventDelegate(WinEventProc);

            _hForegroundHook = ExplorerNativeHooks.SetWinEventHook(
                ExplorerNativeHooks.EVENT_SYSTEM_FOREGROUND, ExplorerNativeHooks.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundHookDelegate, 0, 0, ExplorerNativeHooks.WINEVENT_OUTOFCONTEXT);

            _hNameChangeHook = ExplorerNativeHooks.SetWinEventHook(
                ExplorerNativeHooks.EVENT_OBJECT_NAMECHANGE, ExplorerNativeHooks.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _nameChangeHookDelegate, 0, 0, ExplorerNativeHooks.WINEVENT_OUTOFCONTEXT);

            _hLocationChangeHook = ExplorerNativeHooks.SetWinEventHook(
                ExplorerNativeHooks.EVENT_OBJECT_LOCATIONCHANGE, ExplorerNativeHooks.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _locationChangeHookDelegate, 0, 0, ExplorerNativeHooks.WINEVENT_OUTOFCONTEXT);

            if (_hForegroundHook == IntPtr.Zero || _hNameChangeHook == IntPtr.Zero || _hLocationChangeHook == IntPtr.Zero)
            {
                Stop();
                Logger.Log("[ExplorerTracker] Failed to register WinEvent hooks!", SwiftList.Core.LogLevel.Error);
                return;
            }

            _isRunning = true;
            Logger.Log("[ExplorerTracker] Started.");
            _classifier.CheckActiveWindow(ExplorerNativeHooks.GetForegroundWindow());
        }

        public void Stop()
        {
            if (_hForegroundHook != IntPtr.Zero)
            {
                ExplorerNativeHooks.UnhookWinEvent(_hForegroundHook);
                _hForegroundHook = IntPtr.Zero;
            }
            if (_hNameChangeHook != IntPtr.Zero)
            {
                ExplorerNativeHooks.UnhookWinEvent(_hNameChangeHook);
                _hNameChangeHook = IntPtr.Zero;
            }
            if (_hLocationChangeHook != IntPtr.Zero)
            {
                ExplorerNativeHooks.UnhookWinEvent(_hLocationChangeHook);
                _hLocationChangeHook = IntPtr.Zero;
            }

            _foregroundHookDelegate = null;
            _nameChangeHookDelegate = null;
            _locationChangeHookDelegate = null;
            _isRunning = false;
            LastPath = null;
            LastActiveHwnd = IntPtr.Zero;
            IsExplorerOrDesktopActive = false;
            IsDesktop = false;
            ActiveHwnd = IntPtr.Zero;
            _dialogTracker.Clear();
            Logger.Log("[ExplorerTracker] Stopped.");
        }

        public bool TryGetActiveWindowRect(out RECT rect)
        {
            rect = default;
            if (ActiveHwnd == IntPtr.Zero) return false;

            if (ActiveAdapter != null)
            {
                if (ActiveAdapter.GetDockBounds(ActiveHwnd, out var adapterRect))
                {
                    rect = new RECT { Left = adapterRect.Left, Top = adapterRect.Top, Right = adapterRect.Right, Bottom = adapterRect.Bottom };
                    return true;
                }
                return false;
            }

            if (ActiveInlineAdapter != null)
            {
                if (ActiveInlineAdapter.GetDockBounds(ActiveHwnd, out var adapterRect))
                {
                    rect = new RECT { Left = adapterRect.Left, Top = adapterRect.Top, Right = adapterRect.Right, Bottom = adapterRect.Bottom };
                    return true;
                }
                return false;
            }

            var nativeRect = new ExplorerNativeHooks.RECT();
            int result = ExplorerNativeHooks.DwmGetWindowAttribute(ActiveHwnd, ExplorerNativeHooks.DWMWA_EXTENDED_FRAME_BOUNDS, out nativeRect, Marshal.SizeOf<ExplorerNativeHooks.RECT>());
            if (result == 0)
            {
                rect = new RECT { Left = nativeRect.Left, Top = nativeRect.Top, Right = nativeRect.Right, Bottom = nativeRect.Bottom };
                return true;
            }

            if (ExplorerNativeHooks.GetWindowRect(ActiveHwnd, out nativeRect))
            {
                rect = new RECT { Left = nativeRect.Left, Top = nativeRect.Top, Right = nativeRect.Right, Bottom = nativeRect.Bottom };
                return true;
            }

            return false;
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (!_isRunning || hwnd == IntPtr.Zero) return;
            if (idObject != 0) return;

            if (eventType == ExplorerNativeHooks.EVENT_SYSTEM_FOREGROUND)
            {
                _classifier.CheckActiveWindow(hwnd);
            }
            else if (eventType == ExplorerNativeHooks.EVENT_OBJECT_NAMECHANGE)
            {
                if (hwnd == ExplorerNativeHooks.GetForegroundWindow())
                    _classifier.CheckActiveWindow(hwnd);
            }
            else if (eventType == ExplorerNativeHooks.EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (hwnd == ActiveHwnd && IsActiveWindowDialog)
                    OnActiveWindowMoved?.Invoke();
            }

            IntPtr currentFg = ExplorerNativeHooks.GetForegroundWindow();
            if (currentFg != IntPtr.Zero && currentFg != ActiveHwnd)
            {
                var sbClass = new StringBuilder(256);
                ExplorerNativeHooks.GetClassName(currentFg, sbClass, sbClass.Capacity);
                string className = sbClass.ToString();
                string processName = GetProcessName(currentFg);
                if (FileDialogAdapterRegistry.GetMatchingAdapter(currentFg, className, processName) != null ||
                    InlineSearchAdapterRegistry.GetMatchingAdapter(currentFg, className, processName) != null)
                {
                    _classifier.CheckActiveWindow(currentFg);
                }
            }

            if (IsActiveWindowDialog && ActiveHwnd != IntPtr.Zero && ActiveAdapter != null)
            {
                string? activePath = ActiveAdapter.GetCurrentPath(ActiveHwnd);
                if (!string.IsNullOrEmpty(activePath) && activePath != LastPath)
                {
                    LastPath = activePath;
                    OnPathCaptured?.Invoke(activePath, false);
                }
            }

            if (ActiveInlineAdapter != null && ActiveHwnd != IntPtr.Zero)
            {
                string? activePath = ActiveInlineAdapter.GetSearchScope(ActiveHwnd);
                if (!string.IsNullOrEmpty(activePath) && activePath != LastPath)
                {
                    LastPath = activePath;
                    OnPathCaptured?.Invoke(activePath, false);
                }
            }
        }

        internal void Deactivate()
        {
            bool wasActive = IsExplorerOrDesktopActive;

            IsExplorerOrDesktopActive = false;
            IsDesktop = false;
            IsActiveWindowDialog = false;
            IsActiveWindowExplorer = false;
            ActiveHwnd = IntPtr.Zero;
            LastActiveHwnd = IntPtr.Zero;
            LastPath = null;

            if (wasActive)
                OnExplorerDeactivated?.Invoke();
        }

        public void Dispose() => Stop();

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public static IntPtr FindSubEditBox(IntPtr parent) => ExplorerNativeHooks.FindSubEditBox(parent);
    }
}
