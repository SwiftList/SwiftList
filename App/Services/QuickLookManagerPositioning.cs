using System.Windows;
using System.Windows.Media.Animation;
using SwiftList.App.Services.AppWindow;

namespace SwiftList.App.Services;

// Where the preview window goes, and how that rectangle is worked out. Split out of QuickLookManager.cs
// purely to keep that file nearer the repo's per-file line limit; this is the part of the manager that
// is about geometry rather than about the preview's lifetime.
public partial class QuickLookManager
{
    private void PositionWindow(bool animate = false)
    {
        if (_window == null || _owner == null || !_window.IsVisible) return;

        var computed = TryComputeTargetRect(_owner);
        if (computed == null) return;
        var rect = computed.Value;

        try
        {
            _window.Width = _sessionWidth ?? rect.OuterWidth;
            _window.Height = _sessionHeight ?? rect.OuterHeight;

            // Clear any still-running/held slide-in animation before touching Left directly -- WPF keeps
            // an animated dependency property pinned to the animation's value until the clock is cleared,
            // so a bare assignment here would silently be ignored while one is active.
            _window.BeginAnimation(Window.LeftProperty, null);
            _window.Top = _sessionTop ?? rect.OuterTop;

            if (_sessionLeft.HasValue)
            {
                _window.Left = _sessionLeft.Value;
            }
            else if (animate)
            {
                // Slide out like a drawer: start just short of the resting spot, on the side it docked
                // to, and ease out to it -- rather than just snapping into place.
                const double SlideDistance = 40;
                var startLeft = rect.DockedRight ? rect.OuterLeft - SlideDistance : rect.OuterLeft + SlideDistance;
                _window.Left = startLeft;

                var slideIn = new DoubleAnimation(startLeft, rect.OuterLeft, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                _window.BeginAnimation(Window.LeftProperty, slideIn);
            }
            else
            {
                _window.Left = rect.OuterLeft;
            }
        }
        catch { }
    }

    // A real top-level window (no invisible shadow-margin border like our own _window has) sits a bit
    // further out/wider than where our own panel's outer bounds would land -- these are on top of
    // whatever gap TryComputeTargetRect already used, tuned by eye against an actual docked QuickLook
    // window rather than derived from anything measurable.
    private const double ExternalDockExtraGap = 0;
    private const double ExternalDockExtraWidth = 80;

    // Tells the winning provider (if it wants to know -- see IReceivesPreviewPanelBounds) where our own
    // panel would have gone for this owner, in physical screen pixels: a provider positioning an
    // externally-managed window needs raw pixel SetWindowPos coordinates, not WPF's DIP space, and it has
    // no way to do this DPI/monitor-work-area math itself (that's owner-window state this class already
    // tracks). Best-effort -- silently does nothing if the geometry can't be computed or the window
    // doesn't implement the interface.
    private void NotifyExternalBounds(Window owner)
    {
        if (_window == null) return;
        var computed = TryComputeTargetRect(owner, ExternalDockExtraGap, ExternalDockExtraWidth);
        if (computed == null) return;
        var rect = computed.Value;

        // DIP = physical * dpiScale (see TryComputeTargetRect), so invert it back to physical pixels. No
        // outer/visible distinction here -- an externally-managed window has no invisible margin to
        // compensate for, so it's positioned at the true visible rectangle directly.
        var left = (int)Math.Round(rect.VisibleLeft / rect.DpiScale);
        var top = (int)Math.Round(rect.VisibleTop / rect.DpiScale);
        var width = (int)Math.Round(rect.VisibleWidth / rect.DpiScale);
        var height = (int)Math.Round(rect.VisibleHeight / rect.DpiScale);

        try { _window.NotifyExternalPreviewBounds(left, top, width, height); }
        catch { }
    }

    // "Visible" = the actual rounded-corner card a user sees. Our own _window pads an extra
    // ContentMargin on every side around that for its invisible drop-shadow border, so its outer WPF
    // Window bounds (Outer*) differ from the visible rectangle -- Outer* exists so PositionWindow can
    // recover exactly the same outer bounds the pre-refactor code computed directly, while
    // NotifyExternalBounds uses Visible* as-is, since an external window has no such margin to add back.
    private readonly struct TargetRect
    {
        public double VisibleLeft { get; init; }
        public double VisibleTop { get; init; }
        public double VisibleWidth { get; init; }
        public double VisibleHeight { get; init; }
        public double OuterMargin { get; init; }
        public double DpiScale { get; init; }
        public bool DockedRight { get; init; }

        public double OuterLeft => VisibleLeft - OuterMargin;
        public double OuterTop => VisibleTop - OuterMargin;
        public double OuterWidth => VisibleWidth + 2 * OuterMargin;
        public double OuterHeight => VisibleHeight + 2 * OuterMargin;
    }

    /// <summary>Which side of the owner the preview docks to: the right, unless it does not fit there.</summary>
    /// <remarks>
    /// Room, not roominess. Docking to whichever side has MORE space was tried and is wrong: with a panel
    /// three quarters of the way across a wide screen, the right can hold the preview twice over and the
    /// left is still the wider of the two, so the preview jumped to the left of a panel with the whole
    /// right of the screen empty beside it. Fitting is the only question worth asking; which side has the
    /// larger remainder afterwards is not.
    ///
    /// This is also all a window docked into a corner ever needed. Pushed hard against the screen's right
    /// edge, the right cannot fit the preview, and the flip to the left follows from that on its own.
    /// </remarks>
    internal static bool ChooseRightSide(double roomRight, double needed) => roomRight >= needed;

    // Shared by PositionWindow (moves our own _window there) and NotifyExternalBounds (hands a rectangle
    // to an external-preview provider instead) -- both need the same "where would the preview panel go
    // for this owner" answer, computed once instead of duplicated and risking drift. extraGap/extraWidth
    // default to 0 so the normal (own-window) path is untouched; NotifyExternalBounds passes the
    // ExternalDock* tuning constants above.
    private static TargetRect? TryComputeTargetRect(Window owner, double extraGap = 0, double extraWidth = 0)
    {
        try
        {
            var ownerLeft = owner.Left;
            var ownerTop = owner.Top;
            var ownerWidth = owner.ActualWidth;

            // Use the work area of the monitor the owner is actually on -- not the primary monitor --
            // so the right/left placement flip is correct when the search window sits on a secondary screen.
            var ownerHandle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            var workingArea = Screen.FromHandle(ownerHandle).WorkingArea;
            var dpiScale = 1.0;
            var src = PresentationSource.FromVisual(owner);
            if (src?.CompositionTarget != null) dpiScale = src.CompositionTarget.TransformFromDevice.M11;
            // physical (system-DPI space) -> DIP
            var screenLeft = workingArea.Left * dpiScale;
            var screenTop = workingArea.Top * dpiScale;
            var screenRight = workingArea.Right * dpiScale;
            var screenBottom = workingArea.Bottom * dpiScale;

            var previewInset = Views.QuickLook.QuickLookWindow.ContentMargin;

            // Fixed, user-configurable size (General settings page) rather than mirroring the owner's
            // current ActualHeight -- the owner auto-sizes to however many results are actually showing,
            // so a preview window that copied it would resize unpredictably every time the result count
            // changed instead of staying the same size like a real preview pane. Capped to the current
            // monitor's own work area -- repositioning alone can't keep a configured size fully on screen
            // when that size is bigger than the monitor itself (e.g. the 1200px max preview height on a
            // 768px-tall laptop display).
            var visibleWidth = Math.Min(UiMetrics.PreviewWindowWidth - 2 * previewInset, screenRight - screenLeft) + extraWidth;
            var visibleHeight = Math.Min(UiMetrics.PreviewWindowHeight - 2 * previewInset, screenBottom - screenTop);

            const double DesiredGap = 10;
            var ownerInset = (owner as IHasVisibleContentInset)?.VisibleContentInset ?? new Thickness(0);
            var gap = DesiredGap + extraGap;

            var rightEdge = ownerLeft + ownerWidth - ownerInset.Right;
            var leftEdge = ownerLeft + ownerInset.Left;

            var dockedRight = ChooseRightSide(roomRight: screenRight - (rightEdge + gap), needed: visibleWidth);
            var visibleLeft = dockedRight ? rightEdge + gap : leftEdge - gap - visibleWidth;
            var visibleTop = ownerTop + ownerInset.Top;

            // Neither docking side, nor the owner's own vertical position, guarantees the preview's
            // configured size (user-configurable, up to 900x1200) actually fits next to the owner on
            // this monitor -- clamp against the monitor's work area on every edge so a large preview
            // window always stays fully visible instead of running off-screen.
            var minLeft = screenLeft;
            var maxLeft = screenRight - visibleWidth;
            visibleLeft = Math.Clamp(visibleLeft, minLeft, Math.Max(minLeft, maxLeft));

            var minTop = screenTop;
            var maxTop = screenBottom - visibleHeight;
            visibleTop = Math.Clamp(visibleTop, minTop, Math.Max(minTop, maxTop));

            return new TargetRect
            {
                VisibleLeft = visibleLeft,
                VisibleTop = visibleTop,
                VisibleWidth = visibleWidth,
                VisibleHeight = visibleHeight,
                OuterMargin = previewInset,
                DpiScale = dpiScale,
                DockedRight = dockedRight
            };
        }
        catch
        {
            return null;
        }
    }
}
