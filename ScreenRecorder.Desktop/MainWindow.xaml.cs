using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorder.Desktop.Models;
using ScreenRecorder.Desktop.Services;
using ScreenRecorder.Desktop.Views;

namespace ScreenRecorder.Desktop;

public partial class MainWindow : Window
{
    private readonly RecorderService recorderService = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch uiStopwatch = new();
    private CaptureRegion? selectedRegion;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureUiDefaults();
        HookRecorderEvents();
        timer.Tick += (_, _) => UpdateTimer();
    }

    private void ConfigureUiDefaults()
    {
        OutputPathTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "recording.mp4");

        FpsComboBox.ItemsSource = new[] { 10, 15, 20, 30 };
        FpsComboBox.SelectedItem = 20;

        AudioSourceComboBox.ItemsSource = Enum.GetValues<AudioSourceMode>();
        AudioSourceComboBox.SelectedItem = AudioSourceMode.System;
    }

    private void HookRecorderEvents()
    {
        recorderService.StatusChanged += (_, msg) =>
            Dispatcher.Invoke(() => StatusTextBlock.Text = msg);

        recorderService.ProgressChanged += (_, progress) =>
            Dispatcher.Invoke(() => StatusTextBlock.Text = $"Recording • {progress.FrameCount} frames");

        recorderService.RecordingCompleted += (_, result) =>
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text =
                    $"Saved • {result.FrameCount} frames • {result.AverageFps:F1} fps avg • {result.OutputPath}";
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                TimerTextBlock.Text = "00:00";
                MessageBox.Show(
                    this,
                    $"Recording saved to:\n{result.OutputPath}",
                    "Done",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 video (*.mp4)|*.mp4|AVI video (*.avi)|*.avi|All files (*.*)|*.*",
            FileName = Path.GetFileName(OutputPathTextBox.Text)
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathTextBox.Text = dialog.FileName;
        }
    }

    private void SelectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        var overlay = new RegionSelectorWindow();
        if (overlay.ShowDialog() == true && overlay.SelectedRegion is not null)
        {
            selectedRegion = overlay.SelectedRegion;
            RegionTextBlock.Text = selectedRegion.ToString();
        }
    }

    private void ResetRegionButton_Click(object sender, RoutedEventArgs e)
    {
        selectedRegion = null;
        RegionTextBlock.Text = "Full screen";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (recorderService.IsRecording)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
        {
            MessageBox.Show(this, "Please choose an output file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var options = new RecorderOptions
        {
            OutputPath = OutputPathTextBox.Text.Trim(),
            Fps = (int)(FpsComboBox.SelectedItem ?? 20),
            Region = selectedRegion,
            AudioMode = (AudioSourceMode)(AudioSourceComboBox.SelectedItem ?? AudioSourceMode.System)
        };

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusTextBlock.Text = "Starting...";
        uiStopwatch.Restart();
        timer.Start();

        try
        {
            await recorderService.StartAsync(options);
            StatusTextBlock.Text = "● Recording";
        }
        catch (Exception ex)
        {
            timer.Stop();
            uiStopwatch.Reset();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusTextBlock.Text = "Start failed";
            MessageBox.Show(this, ex.Message, "Start Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!recorderService.IsRecording)
        {
            return;
        }

        StopButton.IsEnabled = false;
        StatusTextBlock.Text = "Stopping...";
        timer.Stop();

        try
        {
            await recorderService.StopAsync();
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            MessageBox.Show(this, ex.Message, "Stop Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTimer()
    {
        var elapsed = uiStopwatch.Elapsed;
        TimerTextBlock.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    protected override async void OnClosed(EventArgs e)
    {
        timer.Stop();
        if (recorderService.IsRecording)
        {
            await recorderService.StopAsync();
        }

        recorderService.Dispose();
        base.OnClosed(e);
    }
}
