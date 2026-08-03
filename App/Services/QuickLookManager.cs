using System.Runtime.InteropServices;
using System.Windows;
using SwiftList.PluginSdk.Services;

using SwiftList.App.Services.AppWindow;
using SwiftList.App.Services.Plugin;
using SwiftList.PluginSdk.Abstractions.Plugins.Preview;
namespace SwiftList.App.Services;

public partial class QuickLookManager
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly Lazy<QuickLookManager> _instance = new(() => new QuickLookManager());
    public static QuickLookManager Instance => _instance.Value;

    private Views.QuickLook.QuickLookWindow? _window;
    private Window? _owner;
    private bool _userWantsPreview;
    private double? _sessionWidth;
    private double? _sessionHeight;
    private double? _sessionLeft;
    private double? _sessionTop;

    public void SetUserResizedDimensions(double width, double height)
    {
        _sessionWidth = width;
        _sessionHeight = height;
    }

    public void SetUserMovedPosition(double left, double top)
    {
        _sessionLeft = left;
        _sessionTop = top;
    }
    // Tracked separately (not just "is _owner non-null") since external-preview mode attaches
    // LocationChanged/SizeChanged but deliberately NOT Deactivated -- see ShowOrUpdate's own comment.
    private bool _ownerTrackingAttached;
    private bool _ownerDeactivateAttached;
    // Set while both windows are hidden for a preview handler's own popup dialog (see
    // PreviewDialogSignal) -- distinguishes that from every other reason _window/_owner might be
    // hidden, so DialogClosed only ever re-shows what this specific mechanism hid.
    private bool _hiddenForDialog;

    // Owners whose Closed we've hooked (once each) to end the preview session — release pooled native
    // preview handlers and their prevhost surrogates when the search window that used them goes away.
    private readonly HashSet<Window> _sessionOwners = new();

    private QuickLookManager()
    {
        PreviewActivationSignal.FocusStolen += OnPreviewFocusStolen;
        PreviewDialogSignal.DialogOpened += OnPreviewDialogOpened;
        PreviewDialogSignal.DialogClosed += OnPreviewDialogClosed;
    }

    // A preview handler's own popup (e.g. Word's "Enter password" prompt) just got the OS foreground --
    // see PreviewFocusGuard's own comment. Left floating on top of it, the quick window and its preview
    // window make that dialog unreachable, so both hide for as long as it's up. Runs on whatever thread
    // the plugin's WinEvent hook fires on, not necessarily this app's UI thread, so both handlers marshal
    // onto the owner's Dispatcher before touching either Window.
    private void OnPreviewDialogOpened()
    {
        if (_owner == null || _window == null) return;
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_owner == null || _window == null) return;
            _hiddenForDialog = true;
            _window.Hide();
            _owner.Hide();
        }));
    }

    private void OnPreviewDialogClosed()
    {
        if (!_hiddenForDialog) return;
        var owner = _owner;
        var window = _window;
        if (owner == null || window == null) { _hiddenForDialog = false; return; }
        owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            _hiddenForDialog = false;
            owner.Show();
            window.Show();
        }));
    }

    // A native (HwndHost) preview handler's own out-of-process window just grabbed OS keyboard focus for
    // itself (see PreviewFocusGuard) -- reclaim it back onto the search box the preview is attached to.
    // Window.Deactivated doesn't fire for this: the handler's window is reparented as a child of our own
    // overlay window, so top-level activation never actually changes, only which control has keyboard
    // focus.
    private void OnPreviewFocusStolen()
    {
        if (_owner is ISearchWindow searchWindow && IsVisible)
            _owner.Dispatcher.BeginInvoke(new Action(() => searchWindow.FocusSearch()));
    }

    // IsShowingExternalPreview counts too: that path deliberately Hide()s _window itself (so it never
    // shows an empty panel next to whatever the provider popped up externally) -- without this, Toggle()
    // would read "nothing is showing" while QuickLook is actively docked and take the wrong branch (show
    // again instead of hide) on the next Alt+P.
    public bool IsVisible => _window != null && (_window.IsVisible || _window.IsShowingExternalPreview);

    // Checked by QuickSearchWindow.Window_Deactivated so its own delayed auto-hide-on-deactivate logic
    // doesn't fight this: without it, that handler would see the window we just Hide()'d as "deactivated"
    // and run the FULL HideWindow() (resets the search query, stops the foreground hook, ...) a moment
    // later, undoing the purely-visual, preserve-everything hide this is meant to be.
    public bool IsHiddenForDialog => _hiddenForDialog;

    public void Reset()
    {
        _userWantsPreview = false;
        _sessionWidth = null;
        _sessionHeight = null;
        _sessionLeft = null;
        _sessionTop = null;
        Hide();
    }

    /// <summary>
    /// Whether the user currently wants the preview following the selection. Read when one search window
    /// hands over to another so the replacement can reopen it.
    /// </summary>
    public bool IsPreviewWanted => _userWantsPreview;

    /// <summary>Whether the preview window is the one that currently has the foreground.</summary>
    /// <remarks>
    /// For an owner that dismisses itself on losing focus: clicking into the preview is the user reaching
    /// for something the owner put there, not clicking away from it, and an owner that took the second
    /// reading would close both windows out from under that click. The search windows do not need this --
    /// they hide on deactivate only when the foreground left the process entirely, which the preview
    /// never does -- but the quick panel's dismissal is stricter than that.
    ///
    /// Both readings, because they answer at different moments. IsActive is WPF's own and is set as the
    /// activation is processed; the foreground handle is the OS's and is what a native preview handler's
    /// child window reports through. Asking either alone left a window in which neither had said yes yet.
    ///
    /// Worth stressing: this is only ever true AFTER the click has been processed. Called from inside a
    /// Deactivated handler it answers no, whatever the user actually clicked -- Windows makes the new
    /// window the foreground one after the old one is told it lost it.
    /// </remarks>
    public bool IsPreviewForeground()
    {
        if (_window == null) return false;
        if (_window.IsActive) return true;

        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        return handle != IntPtr.Zero && GetForegroundWindow() == handle;
    }

    /// <summary>Opens the preview and records that the user wants it.</summary>
    public void Open(Window owner, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _userWantsPreview = true;
        ShowOrUpdate(owner, path);
    }

    public void Toggle(Window owner, string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        if (IsVisible)
        {
            _userWantsPreview = false;
            Hide();
        }
        else
        {
            _userWantsPreview = true;
            ShowOrUpdate(owner, path);
        }
    }

    // Only the window that owns the preview may move or close it. A window handing over keeps running its
    // own teardown for a moment: the quick window's hide clears its search query, which re-fires its own
    // selection handler. Whether that lands before or after the full window has opened the preview it
    // inherited is a race -- the search debounce is 150ms for an ordinary query but ZERO for a
    // single-character one, either side of the fade-out this is racing.
    private bool IsSupersededCaller(Window caller) =>
        _owner != null && !ReferenceEquals(_owner, caller) && _owner.IsVisible;

    /// <summary>
    /// Hides the preview on behalf of one window, for a selection with nothing to preview. Ignored once
    /// another window has taken the preview over.
    /// </summary>
    public void HideFrom(Window caller)
    {
        if (IsSupersededCaller(caller)) return;
        Hide();
    }

    public void UpdateOrShow(Window owner, string path)
    {
        if (IsSupersededCaller(owner)) return;

        if (string.IsNullOrEmpty(path))
        {
            Hide();
            return;
        }

        if (_userWantsPreview)
        {
            ShowOrUpdate(owner, path);
        }
    }

    public void Hide()
    {
        if (_window != null)
        {
            _window.Hide();
            // Not redundant with the line above: when the current provider is RendersExternally, _window
            // was already hidden the moment that provider started showing, so this Hide() call is a no-op
            // transition-wise and IsVisibleChanged (which normally does this) never fires again.
            _window.ReleaseCurrentPreview();
            DetachOwner();
        }
    }

    private void ShowOrUpdate(Window owner, string path)
    {
        _owner = owner;

        // Keep the preview-handler pool alive across this owner's hide/show cycles; release it only when
        // the owner window itself closes. Hooked once per owner (self-removes on close).
        if (_sessionOwners.Add(owner))
            owner.Closed += OnSessionOwnerClosed;

        if (_window == null)
        {
            _window = new Views.QuickLook.QuickLookWindow();
            // Detaches the owner as well as dropping the reference: an owner window that closes takes
            // this one with it (it is an owned window), and leaving the owner attached would keep the
            // tracking flags set, so the next owner would silently get no tracking at all.
            _window.Closed += (s, e) => { _window = null; DetachOwner(); };
            _window.Deactivated += OnPreviewDeactivated;
        }

        _window.SetTarget(path);

        // The winning provider's real preview surface is a separate window it manages itself (e.g. an
        // external application) -- CreatePreview's returned content is never actually shown, so our own
        // panel would just be a redundant empty box floating next to whatever that provider popped up.
        // Checked here, right after SetTarget resolves the winning provider, specifically to avoid the
        // isFirstShow/_window.Show() logic below re-showing it: SetTarget runs first and can itself leave
        // _window.IsVisible false, which isFirstShow would otherwise read as "starting fresh" and undo.
        if (_window.IsShowingExternalPreview)
        {
            if (_window.IsVisible) _window.Hide();

            // Still track the owner moving/resizing (so the docked window follows it around), but NOT
            // Deactivated: that handler's Hide() would close the very window the user just clicked into --
            // clicking QuickLook's own docked window (a separate top-level window) deactivates the owner
            // for real, same reasoning as Owner_Deactivated's existing HwndHost comment, but there's no
            // equivalent "still focus in a way we care about" check possible for a foreign process.
            DetachOwnerDeactivateTracking();
            AttachOwnerLocationTracking(owner);

            NotifyExternalBounds(owner);
            return;
        }

        AttachOwnerLocationTracking(owner);
        AttachOwnerDeactivateTracking(owner);

        // Only slide in on the transition to visible -- a preview session starting fresh -- not on every
        // reposition while it's already open (the owner moving/resizing would otherwise re-trigger the
        // slide constantly instead of just tracking along).
        var isFirstShow = !_window.IsVisible;
        if (isFirstShow)
        {
            _window.Owner = owner;
            _window.Show();
        }

        PositionWindow(animate: isFirstShow);
    }

    private void AttachOwnerLocationTracking(Window owner)
    {
        if (_ownerTrackingAttached) return;
        owner.LocationChanged += Owner_LocationChanged;
        owner.SizeChanged += Owner_SizeChanged;
        _ownerTrackingAttached = true;
    }

    private void AttachOwnerDeactivateTracking(Window owner)
    {
        if (_ownerDeactivateAttached) return;
        owner.Deactivated += Owner_Deactivated;
        _ownerDeactivateAttached = true;
    }

    private void DetachOwnerDeactivateTracking()
    {
        if (!_ownerDeactivateAttached || _owner == null) return;
        _owner.Deactivated -= Owner_Deactivated;
        _ownerDeactivateAttached = false;
    }

    private void DetachOwner()
    {
        if (_owner != null)
        {
            if (_ownerTrackingAttached)
            {
                _owner.LocationChanged -= Owner_LocationChanged;
                _owner.SizeChanged -= Owner_SizeChanged;
                _ownerTrackingAttached = false;
            }
            DetachOwnerDeactivateTracking();
            _owner = null;
        }
    }

    // Branches on the current mode: external-dock re-asserts QuickLook's window position, the normal
    // path repositions our own _window -- both are hooked to the same owner LocationChanged/SizeChanged
    // events (see AttachOwnerLocationTracking), just handled differently depending on which is active.
    private void RepositionForCurrentMode()
    {
        if (_window == null || _owner == null) return;
        if (_window.IsShowingExternalPreview) NotifyExternalBounds(_owner);
        else PositionWindow();
    }

    private void Owner_LocationChanged(object? sender, EventArgs e) => RepositionForCurrentMode();
    private void Owner_SizeChanged(object? sender, SizeChangedEventArgs e) => RepositionForCurrentMode();

    private void Owner_Deactivated(object? sender, EventArgs e)
    {
        // A real (HwndHost) preview -- e.g. a native document/media preview handler -- needs actual focus
        // to be interactive (scrolling, playback controls), so clicking into it deactivates the owner for
        // real. Without this check, that click would immediately hide the very preview the user just
        // clicked into. Only hide when something outside this process took the foreground.
        if (IsForegroundWindowInThisProcess())
            return;

        // Dragging the preview's header out to another application makes that application the foreground
        // window, which lands here. Hiding now would pull this window out from under the DoDragDrop still
        // running on its own header -- the same hazard the inline window's own teardown guards against
        // with this flag. The drag's own completion hides the search windows anyway (see
        // ResultsDragDropHelper.HideSearchWindows).
        if (Views.Controls.Results.ResultsDragDropHelper.IsDragActive)
            return;

        Hide();
    }

    /// <summary>Raised when the preview itself loses the foreground to another application.</summary>
    /// <remarks>
    /// For an owner that dismisses itself on losing focus and had to stand down while the preview held it
    /// (see <see cref="IsPreviewForeground"/>): its own Deactivated already fired on the click that
    /// reached the preview and will not fire again, so without this the click that leaves for good
    /// reaches nobody and the owner stays on screen.
    ///
    /// Only an announcement: nothing is hidden here, because the windows that do not dismiss themselves
    /// on focus loss are not meant to start.
    /// </remarks>
    public event Action? PreviewFocusLost;

    private void OnPreviewDeactivated(object? sender, EventArgs e)
    {
        // Clicking back into the owner, or onto any other window of this app's, is not leaving.
        if (IsForegroundWindowInThisProcess()) return;

        // A drag out of the preview's own header makes the drop target's application the foreground, the
        // same hazard Owner_Deactivated guards against for the same reason.
        if (Views.Controls.Results.ResultsDragDropHelper.IsDragActive) return;

        PreviewFocusLost?.Invoke();
    }

    private static bool IsForegroundWindowInThisProcess()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void OnSessionOwnerClosed(object? sender, EventArgs e)
    {
        if (sender is Window w)
        {
            w.Closed -= OnSessionOwnerClosed;
            _sessionOwners.Remove(w);
        }
        // The owner is already deactivated → QuickLook hidden → any visible host parked its handler back
        // in the pool, so releasing now can't blank a live preview.
        foreach (var provider in PluginManager.Instance.FilePreviewProviders)
            (provider as IPreviewSessionAware)?.EndPreviewSession();
    }
}
