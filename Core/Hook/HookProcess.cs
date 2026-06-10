using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SwiftList.Core.Hook.InlineSearch;
using SwiftList.PluginSdk;

namespace SwiftList.Core.Hook
{
    public sealed class HookProcess : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

        private const uint WM_QUIT = 0x0012;

        private readonly HookIpcServer _ipcServer;
        private readonly HookCommandHandler _commandHandler;
        private ExplorerTracker? _explorerTracker;
        private KeyboardHookService? _keyboardHook;
        private MouseHookService? _mouseHook;

        private int _nativeThreadId;
        private int _trackerThreadId;
        private Thread? _trackerThread;
        private volatile bool _running;
        private uint _appProcessId;
        private bool _isHotkeysDisabledTemporarily;

        internal KeyboardHookService? KeyboardHook => _keyboardHook;
        internal ExplorerTracker? ExplorerTracker => _explorerTracker;
        internal HookIpcServer IpcServer => _ipcServer;

        internal uint AppProcessId
        {
            get => _appProcessId;
            set => _appProcessId = value;
        }

        internal bool IsHotkeysDisabledTemporarily
        {
            get => _isHotkeysDisabledTemporarily;
            set => _isHotkeysDisabledTemporarily = value;
        }

        public HookProcess(HookIpcServer ipcServer)
        {
            _ipcServer = ipcServer;
            _commandHandler = new HookCommandHandler(this);

            _ipcServer.OnStopRequested += () => Stop();
            _ipcServer.OnCommandReceived += _commandHandler.HandleAppCommand;
        }

        public void RunMessageLoop()
        {
            _nativeThreadId = GetCurrentThreadId();
            _running = true;

            using (var trackerStartedEvent = new ManualResetEventSlim(false))
            {
                _trackerThread = new Thread(() =>
                {
                    _trackerThreadId = GetCurrentThreadId();
                    try
                    {
                        _explorerTracker = new ExplorerTracker();
                        _explorerTracker.AppProcessId = _appProcessId;
                        _explorerTracker.OnExplorerActivated += (hwnd, title, className, isDesktop) =>
                        {
                            _ipcServer.SendMessage(new IpcMessage
                            {
                                Id = IpcMessageId.ExplorerActivated,
                                Hwnd = hwnd.ToInt64(),
                                StringVal1 = title,
                                StringVal2 = className,
                                IsDesktop = isDesktop
                            });
                        };
                        _explorerTracker.OnExplorerDeactivated += () =>
                        {
                            _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.ExplorerDeactivated });
                            Task.Run(() =>
                            {
                                try { Win32Api.TrimWorkingSet(); } catch { }
                            });
                        };
                        _explorerTracker.OnPathCaptured += (path, isDesktop) =>
                        {
                            _ipcServer.SendMessage(new IpcMessage
                            {
                                Id = IpcMessageId.PathCaptured,
                                StringVal1 = path,
                                IsDesktop = isDesktop
                            });
                        };
                        _explorerTracker.OnActiveWindowMoved += () =>
                        {
                            _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.ActiveWindowMoved });
                        };
                        _explorerTracker.OnError += (msg) =>
                        {
                            _ipcServer.SendMessage(new IpcMessage
                            {
                                Id = IpcMessageId.Error,
                                StringVal1 = msg
                            });
                        };
                        _explorerTracker.Start();
                        trackerStartedEvent.Set();

                        while (_running)
                        {
                            int result = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
                            if (result <= 0) break;
                            TranslateMessage(ref msg);
                            DispatchMessage(ref msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[HookProcess] TrackerThread error: {ex.Message}", LogLevel.Error);
                        trackerStartedEvent.Set();
                    }
                });
                _trackerThread.SetApartmentState(ApartmentState.STA);
                _trackerThread.IsBackground = true;
                _trackerThread.Start();

                trackerStartedEvent.Wait();
            }

            try
            {
                _keyboardHook = new KeyboardHookService(_explorerTracker!);
                _keyboardHook.AppProcessId = _appProcessId;
                _keyboardHook.IsHotkeysDisabledTemporarily = _isHotkeysDisabledTemporarily;
                _keyboardHook.OnDoubleCtrl += () =>
                {
                    Logger.Log("[HookProcess] Double-Ctrl detected, sending ACTIVATE.", LogLevel.Debug);
                    _ipcServer.SendActivate();
                };
                _keyboardHook.OnCharacterTyped += ch => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyChar, CharVal = ch });
                _keyboardHook.OnBackspacePressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyBackspace });
                _keyboardHook.OnEscapePressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyEscape });
                _keyboardHook.OnEnterPressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyEnter });
                _keyboardHook.OnUpPressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyUp });
                _keyboardHook.OnDownPressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyDown });
                _keyboardHook.OnLeftPressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyLeft });
                _keyboardHook.OnRightPressed += () => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyRight });
                _keyboardHook.OnCtrlNumberPressed += num => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.KeyCtrlNumber, IntVal = num });
                _keyboardHook.Start();

                _mouseHook = new MouseHookService();
                _mouseHook.OnMouseClick += (x, y) => _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.MouseClick, MouseX = x, MouseY = y });
                _mouseHook.Start();

                Logger.Log("[HookProcess] Hooks and ExplorerTracker initialized successfully.", LogLevel.Info);

                while (_running)
                {
                    int result = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
                    if (result <= 0) break;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally
            {
                CleanupHooks();
            }
        }

        private void CleanupHooks()
        {
            _keyboardHook?.Dispose(); _keyboardHook = null;
            _mouseHook?.Dispose(); _mouseHook = null;
            if (_trackerThreadId != 0)
            {
                PostThreadMessage(_trackerThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            if (_trackerThread != null)
            {
                _trackerThread.Join(2000);
                _trackerThread = null;
            }
            _explorerTracker?.Dispose(); _explorerTracker = null;
            Logger.Log("[HookProcess] Hooks and ExplorerTracker stopped/cleaned up.", LogLevel.Info);
            try { Win32Api.TrimWorkingSet(); } catch { }
        }

        public void Stop()
        {
            _running = false;
            if (_nativeThreadId != 0) PostThreadMessage(_nativeThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (_trackerThreadId != 0) PostThreadMessage(_trackerThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose() => Stop();
    }
}
