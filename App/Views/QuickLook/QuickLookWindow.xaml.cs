using System.IO;
using System.Windows;
using System.Windows.Interop;
using SwiftList.PluginSdk.Services;

using SwiftList.App.Services.Plugin;
using SwiftList.App.Services.ShellIcons;
using SwiftList.App.Services.Theme;
using SwiftList.App.Helpers.Visuals;
using SwiftList.App.Views.Controls.Results;
using SwiftList.PluginSdk.Abstractions.Plugins.Preview;
namespace SwiftList.App.Views.QuickLook;

public partial class QuickLookWindow : Window
{
    // Must match QuickLookWindow.xaml's WindowBorder Margin -- the invisible gap between the window's
    // outer (transparent, drop-shadow) bounds and the actual visible card, on every side.
    public const double ContentMargin = 12;

    private string? _currentFilePath;
    private readonly PreviewOverlay _overlay;
    private HwndHost? _pendingHost;
    private UIElement? _currentPreview;
    private IFilePreviewProvider? _currentProvider;

    // Read by QuickLookManager right after SetTarget resolves the winning provider, so it can hide this
    // window instead of showing it -- the provider's real preview surface is a separate window it manages
    // itself, and CreatePreview's returned content (already set as ContentArea.Content/_currentPreview by
    // this point) is never actually meant to be seen.
    public bool IsShowingExternalPreview => _currentProvider?.RendersExternally == true;

    // Called by QuickLookManager once it's computed where this window's own panel would have gone, so
    // the winning provider (if it wants to know) can dock its externally-managed window there instead.
    public void NotifyExternalPreviewBounds(int left, int top, int width, int height) =>
        (_currentProvider as IReceivesPreviewPanelBounds)?.OnPreviewPanelBoundsAvailable(left, top, width, height);

    // Called by QuickLookManager.Hide() unconditionally, not just relied on to happen implicitly via
    // IsVisibleChanged: when the current provider is RendersExternally, this window was already Hide()'d
    // the moment that provider started showing (so it never displays an empty panel), so a later Hide()
    // call is a no-op transition-wise and IsVisibleChanged never fires again -- without this, ending the
    // preview session (closing the search window, toggling preview off) would leave the current
    // provider's EndPreviewSession() (e.g. closing an externally-docked window) never called. Idempotent:
    // harmless if the normal in-panel path already released everything via IsVisibleChanged.
    public void ReleaseCurrentPreview() => ReleasePreview();

    public QuickLookWindow()
    {
        InitializeComponent();
        SystemMenuBlocker.Attach(this);
        ThemedWindowIconHelper.Apply(this);
        _overlay = new PreviewOverlay(this, ContentArea);

        ResultsDragDropHelper.RegisterPathDragSource(HeaderDragHandle, () => _currentFilePath);
        HeaderDragHandle.ToolTip = TranslationService.Get("QuickLook_DragHint");

        FooterGrid.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        IsVisibleChanged += (s, e) =>
        {
            if (!IsVisible)
            {
                // Release a hosted native preview (HwndHost -> IPreviewHandler + its prevhost surrogate
                // and file lock) whenever the window hides; the next show rebuilds it.
                ReleasePreview();
                _currentFilePath = null;
            }
            else if (_pendingHost != null)
            {
                // Overlay needs the window shown before it can be Owner-ed; attach it now.
                var host = _pendingHost;
                _pendingHost = null;
                _overlay.Show(host);
            }
        };

        Loaded += (s, e) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        };
    }

    private const int WM_EXITSIZEMOVE = 0x0232;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_EXITSIZEMOVE)
        {
            BeginAnimation(LeftProperty, null);
            Services.QuickLookManager.Instance.SetUserResizedDimensions(Width, Height);
            Services.QuickLookManager.Instance.SetUserMovedPosition(Left, Top);
        }
        return IntPtr.Zero;
    }

    private void ReleasePreview()
    {
        // The provider being released owns a real external window, not just an in-process resource --
        // unlike ShellPreviewHandlerProvider's pooled prevhost handlers (which deliberately survive
        // hide/show cycles within one owner session, see IPreviewSessionAware's own doc comment), there's
        // nothing to keep alive here, so it's told to end its session on every release, not just when the
        // whole preview session ends. Otherwise its window would linger on screen with nothing left
        // pointing at it whenever this panel just hides (toggling preview off, the search window closing)
        // rather than the owner window itself closing.
        if (_currentProvider?.RendersExternally == true)
            (_currentProvider as IPreviewSessionAware)?.EndPreviewSession();

        _overlay.Clear();
        (_pendingHost as IDisposable)?.Dispose();
        _pendingHost = null;
        (ContentArea.Content as IDisposable)?.Dispose();
        ContentArea.Content = null;
        _currentPreview = null;
        _currentProvider = null;
    }

    public void SetTarget(string path)
    {
        if (_currentFilePath == path) return;
        _currentFilePath = path;

        if (string.IsNullOrEmpty(path))
        {
            ReleasePreview();
            TxtFileName.Text = TranslationService.Get("QuickLook_NoSelection");
            TxtFilePath.Text = string.Empty;
            ImgFileIcon.Source = null;
            TxtFooterSize.Text = string.Empty;
            TxtFooterDate.Text = string.Empty;
            return;
        }

        try
        {
            var isDir = Directory.Exists(path);
            UpdateHeader(path, isDir);

            // Priority-based selection stays authoritative: pick the winning provider first.
            var provider = PluginManager.Instance.FilePreviewProviders
                .FirstOrDefault(p => p.CanPreview(path, isDir));

            // Only reuse in place when the SAME provider wins again and its control can re-point itself.
            // This keeps the pool from bypassing a higher-priority (or third-party) provider that should
            // own the new file, while still avoiding overlay/prevhost churn on same-type navigation.
            if (ReferenceEquals(provider, _currentProvider)
                && _currentPreview is IReusablePreview reusable && reusable.TrySetTarget(path, isDir))
                return;

            ReleasePreview();

            if (provider != null)
            {
                var content = provider.CreatePreview(path, isDir);
                _currentProvider = provider;
                _currentPreview = content;
                if (content is HwndHost host)
                {
                    // Native (HwndHost) previews can't render in this layered window — host them in a
                    // separate non-layered overlay laid over the content area. Owner requires the window
                    // to be shown, so defer until it is visible.
                    if (IsVisible) _overlay.Show(host);
                    else _pendingHost = host;
                }
                else
                {
                    ContentArea.Content = content;
                }
            }
        }
        catch (Exception ex)
        {
            ReleasePreview();
            var errTxt = new System.Windows.Controls.TextBlock
            {
                Text = $"{TranslationService.Get("QuickLook_Error")}: {ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8)
            };
            errTxt.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
            ContentArea.Content = errTxt;
        }
    }

    private void UpdateHeader(string path, bool isDir)
    {
        TxtFileName.Text = Path.GetFileName(path);
        if (string.IsNullOrEmpty(TxtFileName.Text) && isDir) TxtFileName.Text = path;

        TxtFilePath.Text = path;

        // Cache-only fast path first (matches AppSearchResult.Icon's pattern) -- a network-drive video
        // file's real thumbnail requires the shell to actually read/decode frame data over the network,
        // which can take seconds; calling GetIconForPath directly here blocked this whole window (and the
        // owning search window, since QuickLook rides its message loop) until that finished. A cached hit
        // or generic placeholder shows instantly; needsLoad only fires the slow fetch in the background.
        var icon = ShellIconHelper.GetIconFromCacheOnly(path, isDir, out var needsLoad);
        ImgFileIcon.Source = icon;
        if (needsLoad)
        {
            Task.Run(() => ShellIconHelper.GetIconForPath(path, isDir)).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result == null) return;
                // The user may have already navigated to a different file by the time this resolves --
                // _currentFilePath is updated synchronously at the top of SetTarget before this method
                // even runs, so comparing against it here is the same staleness check, just applied late.
                if (_currentFilePath == path) ImgFileIcon.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        if (isDir)
        {
            var dirInfo = new DirectoryInfo(path);
            TxtFooterSize.Text = TranslationService.Get("QuickLook_Folder");
            TxtFooterDate.Text = $"{TranslationService.Get("QuickLook_Modified")}: {dirInfo.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        else if (File.Exists(path))
        {
            var fileInfo = new FileInfo(path);
            TxtFooterSize.Text = FormatFileSize(fileInfo.Length);
            TxtFooterDate.Text = $"{TranslationService.Get("QuickLook_Modified")}: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double val = bytes;
        var i = 0;
        while (val >= 1024 && i < suffixes.Length - 1)
        {
            val /= 1024;
            i++;
        }
        return $"{val:0.##} {suffixes[i]}";
    }
}
