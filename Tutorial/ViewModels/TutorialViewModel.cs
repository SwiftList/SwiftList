using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SwiftList.Tutorial.Services;
using SwiftList.Tutorial.Models;
using SwiftList.Tutorial.Helpers;

namespace SwiftList.Tutorial.ViewModels
{
    public class TutorialViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherTimer _trackerTimer;
        private int _currentStep;

        public int CurrentStep
        {
            get => _currentStep;
            set
            {
                if (_currentStep != value)
                {
                    _currentStep = value;
                    OnPropertyChanged();
                    UpdateStepData();
                    OnPropertyChanged(nameof(IsStartButtonVisible));
                    OnPropertyChanged(nameof(IsNextButtonVisible));
                    OnPropertyChanged(nameof(IsFinishButtonVisible));
                    OnPropertyChanged(nameof(IsTodoListVisible));
                    OnPropertyChanged(nameof(ActionButtonText));
                }
            }
        }

        public string ActionButtonText
        {
            get
            {
                if (CurrentStep == 0) return TutorialTranslationService.Get("Tutorial_StartBtn");
                if (CurrentStep == 6) return TutorialTranslationService.Get("Tutorial_DoneBtn");
                return TutorialTranslationService.Get("Tutorial_NextBtn");
            }
        }

        public string Title => TutorialTranslationService.Get("Tutorial_Title");
        public string Subtitle => TutorialTranslationService.Get("Tutorial_Subtitle");

        private string _stepTitle = "";
        public string StepTitle
        {
            get => _stepTitle;
            set { _stepTitle = value; OnPropertyChanged(); }
        }

        private string _stepDesc = "";
        public string StepDesc
        {
            get => _stepDesc;
            set { _stepDesc = value; OnPropertyChanged(); }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        private string _stepCountText = "";
        public string StepCountText
        {
            get => _stepCountText;
            set { _stepCountText = value; OnPropertyChanged(); }
        }

        public bool IsStartButtonVisible => CurrentStep == 0;
        public bool IsNextButtonVisible => CurrentStep >= 1 && CurrentStep <= 5;
        public bool IsFinishButtonVisible => CurrentStep == 6;
        public bool IsTodoListVisible => CurrentStep >= 1 && CurrentStep <= 5;

        public bool IsNextButtonEnabled
        {
            get
            {
                if (CurrentStep == 0 || CurrentStep == 6) return true;
                foreach (var item in TodoItems)
                {
                    if (!item.IsCompleted) return false;
                }
                return true;
            }
        }

        public ObservableCollection<TodoItem> TodoItems { get; } = new();

        public TutorialViewModel()
        {
            CurrentStep = 0; // Starts with Welcome screen

            _trackerTimer = new DispatcherTimer();
            _trackerTimer.Interval = TimeSpan.FromMilliseconds(100);
            _trackerTimer.Tick += TrackerTimer_Tick;
            _trackerTimer.Start();
        }

        private void TrackerTimer_Tick(object? sender, EventArgs e)
        {
            if (CurrentStep < 1 || CurrentStep > 5) return;
            try
            {
                ProgressTracker.Track(CurrentStep, TodoItems);
                OnPropertyChanged(nameof(IsNextButtonEnabled));
            }
            catch { }
        }

        private void UpdateStepData()
        {
            TodoItems.Clear();
            if (CurrentStep == 0)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Welcome");
                StepDesc = TutorialTranslationService.Get("Tutorial_WelcomeDesc");
                ProgressValue = 0;
                StepCountText = "";
            }
            else if (CurrentStep == 1)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Step1_Title");
                StepDesc = TutorialTranslationService.Get("Tutorial_Step1_Desc");
                ProgressValue = 20;
                StepCountText = TutorialTranslationService.Format("Tutorial_StepCount", 1, 5);

                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step1_Todo1"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step1_Todo2"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step1_Todo3"), false));
            }
            else if (CurrentStep == 2)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Step2_Title");
                StepDesc = TutorialTranslationService.Get("Tutorial_Step2_Desc");
                ProgressValue = 40;
                StepCountText = TutorialTranslationService.Format("Tutorial_StepCount", 2, 5);

                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step2_Todo1"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step2_Todo2"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step2_Todo3"), false));
            }
            else if (CurrentStep == 3)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Step3_Title");
                StepDesc = TutorialTranslationService.Get("Tutorial_Step3_Desc");
                ProgressValue = 60;
                StepCountText = TutorialTranslationService.Format("Tutorial_StepCount", 3, 5);

                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step3_Todo1"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step3_Todo2"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step3_Todo3"), false));
            }
            else if (CurrentStep == 4)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Step4_Title");
                StepDesc = TutorialTranslationService.Get("Tutorial_Step4_Desc");
                ProgressValue = 80;
                StepCountText = TutorialTranslationService.Format("Tutorial_StepCount", 4, 5);

                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step4_Todo1"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step4_Todo2"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step4_Todo3"), false));
            }
            else if (CurrentStep == 5)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Step5_Title");
                StepDesc = TutorialTranslationService.Get("Tutorial_Step5_Desc");
                ProgressValue = 100;
                StepCountText = TutorialTranslationService.Format("Tutorial_StepCount", 5, 5);

                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step5_Todo1"), false));
                TodoItems.Add(new TodoItem(TutorialTranslationService.Get("Tutorial_Step5_Todo2"), false));
            }
            else if (CurrentStep == 6)
            {
                StepTitle = TutorialTranslationService.Get("Tutorial_Congrats");
                StepDesc = TutorialTranslationService.Get("Tutorial_CongratsDesc");
                ProgressValue = 100;
                StepCountText = "";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
