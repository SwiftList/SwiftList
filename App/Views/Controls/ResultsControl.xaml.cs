using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections;
using System.Collections.Specialized;
using SwiftList.App.Services;

namespace SwiftList.App
{
    public enum ResultsViewMode
    {
        List,
        Grid
    }

    public partial class ResultsControl : System.Windows.Controls.UserControl
    {
        public ResultsControl()
        {
            InitializeComponent();
            InitializeSelectionChangedHandlers();

            // Dynamically load custom GridView columns from ResultColumnProviders
            Loaded += (s, e) =>
            {
                UpdateViewModeVisibility();
                LoadDynamicColumns();
            };
        }

        public System.Windows.Controls.Border LoadingBorder => null!;
        public System.Windows.Controls.Control LoadingProgressBar => null!;
        public System.Windows.Controls.TextBlock LoadingTitleTextBlock => null!;
        public System.Windows.Controls.TextBlock LoadingStatsTextBlock => null!;
        public System.Windows.Controls.Button InstallServiceButton => null!;
        public System.Windows.Controls.ListBox ResultsListBox => LstResults;
        public System.Windows.Controls.Grid SearchResultsGrid => GridSearchResultsContainer;
        public System.Windows.Controls.Grid ActionsGrid => GridActions;
        public System.Windows.Controls.TextBlock ActionsTargetTextBlock => TxtActionsTarget;
        public System.Windows.Controls.ListBox ActionsListBox => LstActions;

        public System.Windows.Controls.ListBox ActiveListBox => ViewMode == ResultsViewMode.Grid ? (System.Windows.Controls.ListBox)LstGridResults : LstResults;

        // ViewMode DependencyProperty
        public static readonly DependencyProperty ViewModeProperty = DependencyProperty.Register(
            nameof(ViewMode), typeof(ResultsViewMode), typeof(ResultsControl),
            new PropertyMetadata(ResultsViewMode.List, OnViewModeChanged));

        public ResultsViewMode ViewMode
        {
            get => (ResultsViewMode)GetValue(ViewModeProperty);
            set => SetValue(ViewModeProperty, value);
        }

        private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResultsControl control)
            {
                control.UpdateViewModeVisibility();
            }
        }

        private void UpdateViewModeVisibility()
        {
            if (GridSearchResults == null || GridSearchResultsGrid == null) return;
            if (ViewMode == ResultsViewMode.Grid)
            {
                GridSearchResults.Visibility = Visibility.Collapsed;
                GridSearchResultsGrid.Visibility = Visibility.Visible;
            }
            else
            {
                GridSearchResults.Visibility = Visibility.Visible;
                GridSearchResultsGrid.Visibility = Visibility.Collapsed;
            }
        }

        // ItemsSource DependencyProperty
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(ResultsControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResultsControl control)
            {
                control.UpdateItemsSource(e.OldValue as IEnumerable, e.NewValue as IEnumerable);
            }
        }

        private void UpdateItemsSource(IEnumerable? oldValue, IEnumerable? newValue)
        {
            if (oldValue is INotifyCollectionChanged oldNotify)
            {
                oldNotify.CollectionChanged -= OnCollectionChanged;
            }

            if (LstResults != null) LstResults.ItemsSource = newValue;
            if (LstGridResults != null) LstGridResults.ItemsSource = newValue;

            if (newValue is INotifyCollectionChanged newNotify)
            {
                newNotify.CollectionChanged += OnCollectionChanged;
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var list = ActiveListBox;
                if (list != null && list.Items.Count > 0)
                {
                    list.SelectedIndex = 0;
                    if (ViewMode == ResultsViewMode.Grid)
                        LstGridResults.ScrollIntoView(LstGridResults.SelectedItem);
                    else
                        LstResults.ScrollIntoView(LstResults.SelectedItem);
                }
                else if (list != null)
                {
                    list.SelectedIndex = -1;
                }
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        // SelectedItem DependencyProperty
        public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
            nameof(SelectedItem), typeof(object), typeof(ResultsControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResultsControl control)
            {
                control.UpdateSelectedItem(e.NewValue);
            }
        }

        private bool _isUpdatingSelection;

        private void UpdateSelectedItem(object value)
        {
            if (_isUpdatingSelection) return;
            _isUpdatingSelection = true;
            try
            {
                if (ViewMode == ResultsViewMode.Grid)
                {
                    if (LstGridResults != null) LstGridResults.SelectedItem = value;
                }
                else
                {
                    if (LstResults != null) LstResults.SelectedItem = value;
                }
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        private void InitializeSelectionChangedHandlers()
        {
            LstResults.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingSelection) return;
                _isUpdatingSelection = true;
                try
                {
                    SelectedItem = LstResults.SelectedItem;
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            };

            LstGridResults.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingSelection) return;
                _isUpdatingSelection = true;
                try
                {
                    SelectedItem = LstGridResults.SelectedItem;
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            };
        }

        private bool _columnsLoaded;
        private void LoadDynamicColumns()
        {
            if (_columnsLoaded || LstGridResults == null) return;
            _columnsLoaded = true;

            var gridView = LstGridResults.View as GridView;
            if (gridView != null)
            {
                foreach (var provider in PluginManager.Instance.ResultColumnProviders)
                {
                    foreach (var colDef in provider.GetColumns())
                    {
                        var gvc = new GridViewColumn
                        {
                            Header = colDef.HeaderText,
                            Width = colDef.Width
                        };

                        var binding = new System.Windows.Data.Binding($"[{colDef.ColumnId}]")
                        {
                            Mode = System.Windows.Data.BindingMode.OneWay
                        };
                        var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
                        textBlockFactory.SetBinding(TextBlock.TextProperty, binding);
                        textBlockFactory.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Microsoft YaHei UI"));
                        textBlockFactory.SetValue(TextBlock.ForegroundProperty, new System.Windows.DynamicResourceExtension("TextSecondary2"));
                        textBlockFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
                        textBlockFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                        gvc.CellTemplate = new DataTemplate { VisualTree = textBlockFactory };
                        gridView.Columns.Add(gvc);
                    }
                }
            }
        }

        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader headerClicked)
            {
                if (headerClicked.Column != null)
                {
                    string headerText = headerClicked.Column.Header as string ?? string.Empty;
                    if (!string.IsNullOrEmpty(headerText))
                    {
                        string cleanHeader = headerText.Replace(" ▲", "").Replace(" ▼", "");
                        if (DataContext != null)
                        {
                            dynamic vm = DataContext;
                            try
                            {
                                vm.SortByColumn(cleanHeader);
                                bool isAsc = vm.IsSortAscending;

                                var gridView = LstGridResults.View as GridView;
                                if (gridView != null)
                                {
                                    foreach (var col in gridView.Columns)
                                    {
                                        if (col.Header is string colHeaderText)
                                        {
                                            string cleanColHeader = colHeaderText.Replace(" ▲", "").Replace(" ▼", "");
                                            if (cleanColHeader == cleanHeader)
                                            {
                                                col.Header = cleanColHeader + (isAsc ? " ▲" : " ▼");
                                            }
                                            else
                                            {
                                                col.Header = cleanColHeader;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
    }
}
