using System;
using System.Windows;

namespace SwiftList.App.Services
{
    public class QuickLookManager
    {
        private static readonly Lazy<QuickLookManager> _instance = new(() => new QuickLookManager());
        public static QuickLookManager Instance => _instance.Value;

        private Views.QuickLook.QuickLookWindow? _window;
        private Window? _owner;
        private bool _userWantsPreview;

        private QuickLookManager() { }

        public bool IsVisible => _window != null && _window.IsVisible;

        public void Reset()
        {
            _userWantsPreview = false;
            Hide();
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

        public void UpdateOrShow(Window owner, string path)
        {
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
                DetachOwner();
            }
        }

        private void ShowOrUpdate(Window owner, string path)
        {
            _owner = owner;

            if (_window == null)
            {
                _window = new Views.QuickLook.QuickLookWindow();
                _window.Closed += (s, e) => _window = null;
            }

            _window.SetTarget(path);

            if (!_window.IsVisible)
            {
                _window.Owner = owner;
                _window.Show();

                // Attach window position tracking
                owner.LocationChanged += Owner_LocationChanged;
                owner.SizeChanged += Owner_SizeChanged;
                owner.Deactivated += Owner_Deactivated;
            }

            PositionWindow();
        }

        private void DetachOwner()
        {
            if (_owner != null)
            {
                _owner.LocationChanged -= Owner_LocationChanged;
                _owner.SizeChanged -= Owner_SizeChanged;
                _owner.Deactivated -= Owner_Deactivated;
                _owner = null;
            }
        }

        private void Owner_LocationChanged(object? sender, EventArgs e) => PositionWindow();
        private void Owner_SizeChanged(object? sender, SizeChangedEventArgs e) => PositionWindow();
        private void Owner_Deactivated(object? sender, EventArgs e) => Hide();

        private void PositionWindow()
        {
            if (_window == null || _owner == null || !_window.IsVisible) return;

            try
            {
                double ownerLeft = _owner.Left;
                double ownerTop = _owner.Top;
                double ownerWidth = _owner.ActualWidth;
                double ownerHeight = _owner.ActualHeight;

                _window.Height = ownerHeight;

                double targetLeft = ownerLeft + ownerWidth + 8;
                double screenWidth = SystemParameters.PrimaryScreenWidth;

                if (targetLeft + _window.Width > screenWidth)
                {
                    targetLeft = ownerLeft - _window.Width - 8;
                }

                _window.Left = targetLeft;
                _window.Top = ownerTop;
            }
            catch { }
        }
    }
}
