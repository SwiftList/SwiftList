using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.InteropServices;
using SwiftList.Core;
using SwiftList.Core.Services;
using SwiftList.App.ViewModels;
using SwiftList.App.ViewModels.Settings;
using SwiftList.App.Services;
using Application = System.Windows.Application;
using MessageBox = SwiftList.App.Views.Controls.CustomMessageBox;
namespace SwiftList.App
{
    public partial class App : Application
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
        private System.Threading.Mutex? _appMutex;
        public static SwiftList.Core.Hook.HookIpcClient? HookClient { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Initialize logger first so we can log elevation decisions and issues

            Logger.Initialize("app.log", overwrite: true);
            var settings = UserSettings.Load();
            Logger.MinimumLevel = ExperienceSettingsViewModel.ParseLogLevel(settings.LogLevel);
            Logger.Log("=========================================");
            Logger.Log($"Application starting with arguments: {string.Join(" ", e.Args)}");
            Logger.Log($"[App] Running as Administrator: {ElevationManager.IsRunningAsAdmin()}");

            // Single instance check per user session

            // We append the username to guarantee multi-user isolation on the same machine

            string mutexName = $@"Local\SwiftList_App_{Environment.UserName}";
            _appMutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    var current = Process.GetCurrentProcess();
                    foreach (var proc in Process.GetProcessesByName(current.ProcessName))
                    {
                        if (proc.Id != current.Id)
                        {
                            AllowSetForegroundWindow(proc.Id);
                        }
                    }
                }

                catch { }

                // Send activation command to the already running process and then exit immediately

                await AppPipeService.SendActivateSignalAsync();
                Shutdown();
                return;
            }

            string serviceExe = ServiceInstallManager.GetServiceExePath();
            HookClient = new Core.Hook.HookIpcClient(serviceExe, settings.AutoElevateIfAdmin);

            PluginSdk.ListControlIpcBridge.GetListItemsFunc = hwnd =>

                HookClient != null ? Core.Hook.ListIpcCoordinator.GetListItems(hwnd, HookClient.SendMessage) : Array.Empty<string>();

            PluginSdk.ListControlIpcBridge.GetSelectedIndicesFunc = (hwnd, className) =>

                HookClient != null ? Core.Hook.ListIpcCoordinator.GetSelectedIndices(hwnd, className, HookClient.SendMessage) : Array.Empty<int>();

            PluginSdk.ListControlIpcBridge.SelectItemAction = (hwnd, className, index, clearOthers, selectState) =>

                HookClient?.SendMessage(new Core.IpcMessage
                {
                    Id = Core.IpcMessageId.SelectItem,
                    Hwnd = hwnd.ToInt64(),
                    StringVal1 = className,
                    IntVal = index,
                    BoolVal = clearOthers,
                    IsDesktop = selectState

                });

            PluginSdk.ListControlIpcBridge.ClearSelectionAction = (hwnd, className) =>

                HookClient?.SendMessage(new Core.IpcMessage
                {
                    Id = Core.IpcMessageId.ClearSelection,
                    Hwnd = hwnd.ToInt64(),
                    StringVal1 = className

                });

            HookClient.OnActivated += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var quickSearchWindow = Current.MainWindow as QuickSearchWindow;
                    quickSearchWindow?.ToggleVisibility();
                }));
            };
            HookClient.Start();

            // Set up global exception handlers

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogException("AppDomain UnhandledException", args.ExceptionObject as Exception);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                LogException("DispatcherUnhandledException", args.Exception);

                args.Handled = true; // Prevent crash if possible

            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogException("TaskScheduler UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            // Force load all plugins (actions and alias providers) on startup

            _ = PluginManager.Instance;

            // Now that all plugins are loaded, initialize translations.

            // This must happen after PluginManager to avoid a Lazy<T> circular initialization crash.

            try
            {
                // Register TranslationService delegate for decoupled plugins

                PluginSdk.TranslationService.LookupFunc = key => TranslationManager.Instance[key];

                // Register IconService delegate for decoupled plugins
                PluginSdk.IconService.GetIconFunc = (path, isDir) => ShellIconHelper.GetIconForPath(path, isDir);

                // Register Logger delegate for decoupled plugins

                PluginSdk.Logger.LogAction = (msg, lvl) =>
                {
                    Logger.Log(msg, (LogLevel)(int)lvl);
                };
                SwiftList.App.Services.TranslationManager.Instance.ReloadTranslations();
                Logger.Log("[App] TranslationManager initialized.");

                // Initialize ThemeManager with the saved theme setting

                ThemeManager.Instance.Initialize(settings.Theme);
                Logger.Log($"[App] ThemeManager initialized with theme: {settings.Theme}");
            }

            catch (Exception ex)
            {
                Logger.Log($"[App] Failed to initialize TranslationManager or ThemeManager: {ex.Message}", LogLevel.Error);
            }

            // Start the activation named pipe server to listen to subsequent launches

            _ = AppPipeService.StartPipeServerAsync();
            Logger.Log("Starting normal WPF GUI client mode.");
            base.OnStartup(e);

            // After QuickSearchWindow is created (via StartupUri), start InlineSearchManager
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var quickSearchWindow = Current.MainWindow as QuickSearchWindow;
                if (quickSearchWindow != null)
                {
                    SwiftList.App.Services.InlineSearchManager.Instance.Start();
                    Logger.Log("[App] InlineSearchManager started.");
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            // Background update check on startup

            _ = Task.Run(async () =>
            {
                try
                {
                    // Delay slightly to ensure app is fully initialized and main window is up

                    await Task.Delay(3000);
                    var settings = UserSettings.Load();
                    if (!settings.AutoCheckUpdates)
                    {
                        return;
                    }

                    var release = await UpdateService.Instance.CheckForUpdatesAsync();
                    if (release != null)
                    {
                        var currentVersion = typeof(App).Assembly.GetName().Version;
                        var cleanTag = release.TagName.TrimStart('v', 'V');
                        if (Version.TryParse(cleanTag, out var latestVersion) && latestVersion > currentVersion)
                        {
                            // If auto silent update is enabled and user is admin, prompt user and execute silent update

                            if (settings.AutoSilentUpdate && UpdateService.Instance.IsUserAdmin())
                            {
                                var zipAsset = Array.Find(release.Assets, a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                                if (zipAsset != null)
                                {
                                    _ = Dispatcher.BeginInvoke(new Action(async () =>
                                    {
                                        string promptFormat = TranslationManager.Instance["About_SilentUpdatePrompt"];
                                        string prompt = string.Format(promptFormat, release.TagName);
                                        string title = TranslationManager.Instance["About_CheckUpdate"];
                                        MessageBox.Show(prompt, title, MessageBoxButton.OK, MessageBoxImage.Information);
                                        bool success = await UpdateService.Instance.StartSilentUpdateAsync(zipAsset.BrowserDownloadUrl);
                                        if (success)
                                        {
                                            TrayCleanExitHelper.CleanExit();
                                        }

                                    }));
                                    return;
                                }
                            }

                            _ = Dispatcher.BeginInvoke(new Action(() =>
                            {
                                string promptFormat = TranslationManager.Instance["About_NewVersionAvailablePrompt"];
                                string prompt = string.Format(promptFormat, release.TagName);
                                string title = TranslationManager.Instance["About_CheckUpdate"];
                                MessageBox.Show(prompt, title, MessageBoxButton.OK, MessageBoxImage.Information);
                                ShowSettingsWindow("About");
                            }));
                        }
                    }
                }

                catch (Exception ex)
                {
                    Logger.Log($"[App] Background startup update check failed: {ex.Message}", LogLevel.Warn);
                }

            });
        }

        public static void HideInlineSearch()
        {
            InlineSearchManager.Instance.CloseInlineSearch();
        }

        private static void LogException(string source, Exception? ex)
        {
            string details = ex != null ? ex.ToString() : "Null exception object";
            Logger.Log($"CRITICAL CRASH ({source}):\n{details}", LogLevel.Error);

            // Show message box to alert user

            MessageBox.Show(string.Format(TranslationManager.Instance["Crash_Message"], source, ex?.Message, Logger.LogDir), TranslationManager.Instance["Crash_Title"], MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public static void ShowSettingsWindow(string? targetSection = null)

            => AppWindowManager.ShowSettingsWindow(targetSection);

        public static void ShowSearchWindow()

            => AppWindowManager.ShowSearchWindow();

        public static void CloseAllManagedWindows()

            => AppWindowManager.CloseAllManagedWindows();

        protected override void OnExit(ExitEventArgs e)
        {
            HookClient?.Stop();
            HookClient?.Dispose();
            HookClient = null;
            AppPipeService.StopServer();
            InlineSearchManager.Instance.Dispose();
            CloseAllManagedWindows();
            if (_appMutex != null)
            {
                try
                {
                    _appMutex.ReleaseMutex();
                }

                catch { }

                _appMutex.Dispose();
            }

            base.OnExit(e);
        }
    }
}
