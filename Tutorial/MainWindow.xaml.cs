using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwiftList.Tutorial.ViewModels;

namespace SwiftList.Tutorial
{
    public partial class MainWindow : Window
    {
        private readonly TutorialViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new TutorialViewModel();
            this.DataContext = _viewModel;

            // Allow window dragging
            this.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    this.DragMove();
                }
            };
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.CurrentStep == 0)
            {
                _viewModel.CurrentStep = 1;
            }
            else if (_viewModel.CurrentStep >= 1 && _viewModel.CurrentStep <= 5)
            {
                _viewModel.CurrentStep++;
            }
            else if (_viewModel.CurrentStep == 6)
            {
                this.Close();
            }
        }
    }
}
