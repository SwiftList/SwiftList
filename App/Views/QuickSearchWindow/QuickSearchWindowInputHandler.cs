using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using SwiftList.App.Services;
using SwiftList.PluginSdk;
using SwiftList.App.Helpers;
using SwiftList.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
namespace SwiftList.App.Views.QuickSearchWindow
{
    public class QuickSearchWindowInputHandler
    {
        private readonly SwiftList.App.QuickSearchWindow _window;

        public QuickSearchWindowInputHandler(SwiftList.App.QuickSearchWindow window)
        {
            _window = window;
        }

        public void HandleWindowPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_window.LstResults.SelectedItem is AppSearchResult result && !result.IsSearchSectionHeader && !result.IsEmptyResult && !result.IsApplication)
                {
                    if (result.ResultKind == "File" || result.ResultKind == "Folder" || File.Exists(result.FullPath) || Directory.Exists(result.FullPath))
                    {
                        try
                        {
                            var fileList = new System.Collections.Specialized.StringCollection { result.FullPath };
                            System.Windows.Clipboard.SetFileDropList(fileList);

                            try
                            {
                                string tempPath = Path.Combine(Path.GetTempPath(), "swiftlist_copied.tmp");
                                File.WriteAllText(tempPath, result.FullPath);
                            }
                            catch { }

                            _window.HideWindow();
                            e.Handled = true;
                            return;
                        }
                        catch { }
                    }
                }
            }

            var quickLookModifier = WpfUiHelper.GetWpfModifier(UserSettings.Load().SelectIndexModifier);
            var checkKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if ((checkKey == Key.P && Keyboard.Modifiers == quickLookModifier) ||
                (checkKey == Key.Space && Keyboard.Modifiers == quickLookModifier))
            {
                if (_window.LstResults.SelectedItem is AppSearchResult result && !result.IsSearchSectionHeader && !result.IsEmptyResult && !result.IsApplication && result.FullPath != "__SHOW_MORE__")
                {
                    QuickLookManager.Instance.Toggle(_window, result.FullPath);
                    e.Handled = true;
                    return;
                }
            }

            var menuPresenter = _window.MenuPresenter;
            if (menuPresenter != null && menuPresenter.IsInActionsMode)
            {
                if (e.Key == Key.Escape)
                {
                    menuPresenter.ExitActionsMode();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Left || e.Key == Key.Back)
                {
                    menuPresenter.GoBackMenuOrExit();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Right)
                {
                    menuPresenter.EnterSubMenu();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Down)
                {
                    menuPresenter.NavigateActionsList(1);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Up)
                {
                    menuPresenter.NavigateActionsList(-1);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter)
                {
                    menuPresenter.ExecuteSelectedAction();
                    e.Handled = true;
                    return;
                }

                if (e.Key != Key.System && e.Key != Key.LeftAlt && e.Key != Key.RightAlt)
                {
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Escape)
            {
                _window.HideWindow();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab)
            {
                CompleteSearchFromSelection();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Right && IsSearchCaretAtEnd())
            {
                if (_window.LstResults.SelectedItem is AppSearchResult result)
                {
                    if (result.IsSearchSectionHeader)
                    {
                        e.Handled = true;
                        return;
                    }

                    menuPresenter?.EnterActionsMode(result);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter)
            {
                bool asAdmin = Keyboard.Modifiers == WpfUiHelper.GetWpfModifier(UserSettings.Load().SelectIndexModifier);
                if (_window.LstResults.SelectedItem is AppSearchResult result)
                {
                    ExecuteResult(result, asAdmin);
                }
                else if (_window.LstResults.Items.Count > 0)
                {
                    _window.LstResults.SelectedIndex = 0;
                    if (_window.LstResults.SelectedItem is AppSearchResult firstResult)
                    {
                        ExecuteResult(firstResult, asAdmin);
                    }
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down)
            {
                MoveResultSelection(1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                MoveResultSelection(-1);
                e.Handled = true;
                return;
            }

            var selectIndexMod = UserSettings.Load().SelectIndexModifier;
            if (Keyboard.Modifiers == WpfUiHelper.GetWpfModifier(selectIndexMod))
            {
                int num = -1;
                if (e.Key >= Key.D1 && e.Key <= Key.D9)
                    num = e.Key - Key.D1;
                else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
                    num = e.Key - Key.NumPad1;
                if (num >= 0)
                {
                    var scrollViewer = WpfUiHelper.GetScrollViewer(_window.LstResults);
                    int firstVisible = scrollViewer != null ? (int)Math.Round(scrollViewer.VerticalOffset) : 0;
                    int shortcutIndex = 0;
                    for (int i = firstVisible; i < _window.LstResults.Items.Count; i++)
                    {
                        var item = _window.LstResults.Items[i] as AppSearchResult;
                        if (item != null && !item.IsEmptyResult && !item.IsSearchSectionHeader)
                        {
                            if (shortcutIndex == num)
                            {
                                ExecuteResult(item, asAdmin: true);
                                e.Handled = true;
                                break;
                            }

                            shortcutIndex++;
                        }
                    }
                }
            }
        }

        private void CompleteSearchFromSelection()
        {
            AppSearchResult? result = _window.LstResults.SelectedItem as AppSearchResult;
            if (result == null && _window.LstResults.Items.Count > 0)
            {
                result = _window.LstResults.Items[0] as AppSearchResult;
            }

            if (result == null || result.IsEmptyResult || result.FullPath == "__SHOW_MORE__" || string.IsNullOrWhiteSpace(result.Name))
            {
                return;
            }

            string completion = GetCompletionText(result);
            if (string.Equals(_window.TxtSearch.Text, completion, StringComparison.Ordinal))
            {
                return;
            }

            _window.TxtSearch.Text = completion;
            _window.TxtSearch.CaretIndex = _window.TxtSearch.Text.Length;
            _window.TxtSearch.Focus();
        }

        private void ExecuteResult(AppSearchResult result, bool asAdmin = false)
        {
            if (result.IsSearchSectionHeader)
                return;
            if (!result.IsPluginSearchAction && !result.IsInstantResult)
            {
                SearchHistoryStore.Record(result.FullPath);
            }

            if (result.IsPluginSearchAction)
            {
                _window.HideWindow();
                if (PluginManager.Instance.TryExecuteSearchAction(result, _window))
                {
                }

                return;
            }

            if (PluginManager.Instance.TryExecuteSearchAction(result, _window))
            {
                _window.HideWindow();
                return;
            }

            string currentQuery = _window.TxtSearch.Text;
            if (result.FullPath == "__SHOW_MORE__")
            {
                _window.HideWindowNoRestore();
                FileExecutor.OpenFileOrFolder(result.FullPath, currentQuery, _window.HideWindowNoRestore);
            }

            else
            {
                _window.HideWindow();
                if (asAdmin)
                    FileExecutor.OpenFileOrFolderAsAdmin(result.FullPath, currentQuery, _window.HideWindow);
                else
                    FileExecutor.OpenFileOrFolder(result.FullPath, currentQuery, _window.HideWindow);
            }
        }

        private void MoveResultSelection(int direction)
        {
            int count = _window.LstResults.Items.Count;
            if (count == 0) return;
            int index = _window.LstResults.SelectedIndex;
            for (int i = 0; i < count; i++)
            {
                index += direction;
                if (index < 0 || index >= count)
                    break;
                if (_window.LstResults.Items[index] is AppSearchResult item && !item.IsEmptyResult && !item.IsSearchSectionHeader)
                {
                    _window.LstResults.SelectedIndex = index;
                    _window.LstResults.ScrollIntoView(_window.LstResults.SelectedItem);
                    break;
                }
            }
        }

        private static string GetCompletionText(AppSearchResult result)
        {
            if (result.IsInstantResult)
            {
                if (!string.IsNullOrWhiteSpace(result.TabCompletion))
                    return result.TabCompletion;
                return result.InstantResultActionArgument;
            }

            if (result.IsApplication)
            {
                return result.Name;
            }

            string path = result.FullPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return result.Name;
            }

            return path;
        }

        private bool IsSearchCaretAtEnd()
        {
            return _window.TxtSearch.IsKeyboardFocusWithin

                   && _window.TxtSearch.SelectionLength == 0

                   && _window.TxtSearch.CaretIndex >= _window.TxtSearch.Text.Length;
        }
    }
}
