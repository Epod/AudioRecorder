using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using Microsoft.Win32;
using System.Collections.Generic;

namespace AudioRecorder
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<AudioDevice> _audioDevices;
        private ObservableCollection<AudioDevice> _filteredAudioDevices;
        private SynchronizedAudioRecorder _synchronizedRecorder;
        private ObservableCollection<IWaveIn> _activeRecorders; // For separate recording mode
        private ObservableCollection<WaveFileWriter> _waveWriters; // For separate recording mode
        private DispatcherTimer _recordingTimer;
        private DateTime _recordingStartTime;
        private bool _isRecording = false;
        private bool _isPaused = false;
        private string _currentOutputDirectory;
        private MMDeviceEnumerator _deviceEnumerator;
        private bool _useSynchronizedRecording = false; // Default to separate recording

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                InitializeApplication();
                Loaded += MainWindow_Loaded;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing main window: {ex.Message}", "Initialization Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Load devices after UI is fully initialized
                RefreshAudioSources();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading audio devices: {ex.Message}", "Loading Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                if (StatusTextBlock != null)
                {
                    StatusTextBlock.Text = "Error loading audio devices";
                }
            }
        }

        private void InitializeApplication()
        {
            try
            {
                _audioDevices = new ObservableCollection<AudioDevice>();
                _filteredAudioDevices = new ObservableCollection<AudioDevice>();
                _activeRecorders = new ObservableCollection<IWaveIn>(); // For separate recording mode
                _waveWriters = new ObservableCollection<WaveFileWriter>(); // For separate recording mode
                _deviceEnumerator = new MMDeviceEnumerator();

                // Null check for UI elements before accessing them
                if (AudioSourcesList != null)
                {
                    AudioSourcesList.ItemsSource = _filteredAudioDevices;
                }

                // Set default output directory to user's Documents folder
                _currentOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioRecordings");
                
                if (OutputDirectoryTextBox != null)
                {
                    OutputDirectoryTextBox.Text = _currentOutputDirectory;
                }

                // Initialize timer for recording duration
                _recordingTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _recordingTimer.Tick += RecordingTimer_Tick;

                // Set default ComboBox selections if they exist
                if (FileFormatComboBox?.Items.Count > 0)
                {
                    FileFormatComboBox.SelectedIndex = 0; // Default to WAV
                }

                if (QualityComboBox?.Items.Count > 0)
                {
                    QualityComboBox.SelectedIndex = 0; // Default to 44.1 kHz
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing application: {ex.Message}", "Initialization Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshAudioSources()
        {
            _audioDevices.Clear();

            try
            {
                int deviceIndex = 0;

                // Get input devices using MMDevice API (more reliable)
                var inputDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var mmDevice in inputDevices)
                {
                    var device = new AudioDevice(
                        deviceIndex++,
                        mmDevice.FriendlyName,
                        2, // Default to stereo
                        44100, // Default sample rate
                        AudioDeviceType.Input
                    );
                    _audioDevices.Add(device);
                }

                // Get output devices for loopback recording
                var outputDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var mmDevice in outputDevices)
                {
                    var device = new AudioDevice(
                        deviceIndex++,
                        mmDevice.FriendlyName + " (Loopback)",
                        2, // Default to stereo
                        44100, // Default sample rate
                        AudioDeviceType.Output
                    );
                    device.DeviceIndex = deviceIndex - 1; // Store the MMDevice index
                    _audioDevices.Add(device);
                }

                // Apply filters
                ApplyDeviceFilters();

                int inputCount = _audioDevices.Count(d => d.DeviceType == AudioDeviceType.Input);
                int outputCount = _audioDevices.Count(d => d.DeviceType == AudioDeviceType.Output);
                
                if (StatusTextBlock != null)
                {
                    StatusTextBlock.Text = $"Found {inputCount} input devices, {outputCount} output devices";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing audio sources: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                
                if (StatusTextBlock != null)
                {
                    StatusTextBlock.Text = "Error loading audio devices";
                }
            }
        }

        private void ApplyDeviceFilters()
        {
            _filteredAudioDevices.Clear();

            // Default to showing both if checkboxes are not initialized yet
            bool showInput = ShowInputDevicesCheckBox?.IsChecked ?? true;
            bool showOutput = ShowOutputDevicesCheckBox?.IsChecked ?? true;

            foreach (var device in _audioDevices)
            {
                if ((device.DeviceType == AudioDeviceType.Input && showInput) ||
                    (device.DeviceType == AudioDeviceType.Output && showOutput))
                {
                    _filteredAudioDevices.Add(device);
                }
            }
        }

        private void DeviceFilter_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyDeviceFilters();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying device filters: {ex.Message}");
                if (StatusTextBlock != null)
                {
                    StatusTextBlock.Text = "Error applying device filters";
                }
            }
        }

        private void RefreshSources_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshAudioSources();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing audio sources: {ex.Message}", "Refresh Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Select output directory for recordings",
                    FileName = "Select Folder",
                    Filter = "Folder Selection|*.folder",
                    CheckFileExists = false,
                    CheckPathExists = true,
                    InitialDirectory = _currentOutputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (dialog.ShowDialog() == true)
                {
                    _currentOutputDirectory = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (OutputDirectoryTextBox != null)
                    {
                        OutputDirectoryTextBox.Text = _currentOutputDirectory;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error selecting directory: {ex.Message}", "Directory Selection Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void StartRecording_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AudioSourcesList?.SelectedItems?.Count == 0)
                {
                    MessageBox.Show("Please select at least one audio source to record.", "No Audio Source Selected",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_currentOutputDirectory))
                {
                    MessageBox.Show("Please select an output directory.", "No Output Directory",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create output directory if it doesn't exist
                Directory.CreateDirectory(_currentOutputDirectory);

                await StartRecording();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting recording: {ex.Message}", "Recording Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StartRecording()
        {
            _isRecording = true;
            _isPaused = false;
            _recordingStartTime = DateTime.Now;

            // Update UI with null checks
            if (StartRecordingButton != null) StartRecordingButton.IsEnabled = false;
            if (StopRecordingButton != null) StopRecordingButton.IsEnabled = true;
            if (PauseResumeButton != null)
            {
                PauseResumeButton.IsEnabled = true;
                PauseResumeButton.Content = "Pause";
            }
            if (RecordingProgressBar != null) RecordingProgressBar.Visibility = Visibility.Visible;
            if (RecordingIndicator != null) RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
            if (StatusTextBlock != null) 
            {
                string mode = _useSynchronizedRecording ? "synchronized" : "separate";
                StatusTextBlock.Text = $"Recording in {mode} mode from {AudioSourcesList?.SelectedItems?.Count ?? 0} device(s)...";
            }

            // Get selected sample rate
            int sampleRate = GetSelectedSampleRate();

            // Null check for AudioSourcesList and SelectedItems
            if (AudioSourcesList?.SelectedItems == null)
            {
                MessageBox.Show("No audio sources selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // Check if synchronized recording is enabled
                bool useSynchronizedRecording = SynchronizedRecordingCheckBox?.IsChecked ?? true;

                if (useSynchronizedRecording)
                {
                    await StartSynchronizedRecording(sampleRate);
                }
                else
                {
                    await StartSeparateRecording(sampleRate);
                }

                _recordingTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start recording: {ex.Message}", "Recording Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                
                // Reset UI state
                ResetUIAfterRecordingError();
            }
        }

        private async Task StartSynchronizedRecording(int sampleRate)
        {
            // Convert selected devices to list
            var selectedDevices = new List<AudioDevice>();
            foreach (AudioDevice device in AudioSourcesList.SelectedItems)
            {
                if (device != null)
                {
                    selectedDevices.Add(device);
                    device.Status = "Recording";
                }
            }

            if (selectedDevices.Count == 0)
            {
                throw new InvalidOperationException("No valid audio devices selected.");
            }

            // Create synchronized recorder
            string fileFormat = GetSelectedFileFormat();
            _synchronizedRecorder = new SynchronizedAudioRecorder(selectedDevices, sampleRate, 2, fileFormat);
            _synchronizedRecorder.RecordingError += OnRecordingError;

            // Generate output file path
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"MultiDevice_Recording_{timestamp}";
            string outputPath;
            
            if (fileFormat == "WAV")
            {
                outputPath = Path.Combine(_currentOutputDirectory, $"{fileName}.wav");
            }
            else // MP3
            {
                outputPath = Path.Combine(_currentOutputDirectory, $"{fileName}.mp3");
            }

            // Start synchronized recording
            await _synchronizedRecorder.StartRecording(outputPath);
        }

        private async Task StartSeparateRecording(int sampleRate)
        {
            // Start recording from each selected device separately (original logic)
            foreach (AudioDevice device in AudioSourcesList.SelectedItems)
            {
                if (device == null) continue;

                IWaveIn waveIn;
                
                if (device.DeviceType == AudioDeviceType.Input)
                {
                    // Use WasapiCapture for input devices
                    var inputDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                    if (device.DeviceIndex >= 0 && device.DeviceIndex < inputDevices.Count)
                    {
                        var mmDevice = inputDevices[device.DeviceIndex];
                        waveIn = new WasapiCapture(mmDevice);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Input device index {device.DeviceIndex} not found (available: {inputDevices.Count})");
                    }
                }
                else // Output device
                {
                    // Use WasapiLoopbackCapture for output devices (system playback)
                    var outputDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                    var inputDeviceCount = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count();
                    var deviceIndexAdjusted = device.DeviceIndex - inputDeviceCount;
                    
                    if (deviceIndexAdjusted >= 0 && deviceIndexAdjusted < outputDevices.Count)
                    {
                        var mmDevice = outputDevices[deviceIndexAdjusted];
                        waveIn = new WasapiLoopbackCapture(mmDevice);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Output device index {deviceIndexAdjusted} not found (available: {outputDevices.Count})");
                    }
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string safeName = device.DeviceName?.Replace(":", "").Replace(" ", "_").Replace("(", "").Replace(")", "") ?? "UnknownDevice";
                string fileName = $"{safeName}_{timestamp}";
                
                string outputPath;
                if (GetSelectedFileFormat() == "WAV")
                {
                    outputPath = Path.Combine(_currentOutputDirectory, $"{fileName}.wav");
                    var waveWriter = new WaveFileWriter(outputPath, waveIn.WaveFormat);
                    _waveWriters.Add(waveWriter);

                    waveIn.DataAvailable += (s, e) =>
                    {
                        if (!_isPaused)
                        {
                            waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                    };
                }
                else // MP3
                {
                    outputPath = Path.Combine(_currentOutputDirectory, $"{fileName}.mp3");
                    // Note: MP3 encoding would require additional setup with LAME encoder
                    // For now, we'll record as WAV and note this limitation
                    outputPath = Path.Combine(_currentOutputDirectory, $"{fileName}.wav");
                    var waveWriter = new WaveFileWriter(outputPath, waveIn.WaveFormat);
                    _waveWriters.Add(waveWriter);

                    waveIn.DataAvailable += (s, e) =>
                    {
                        if (!_isPaused)
                        {
                            waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                    };
                }

                waveIn.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Recording error on {device.DeviceName}: {e.Exception.Message}",
                                "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                };

                _activeRecorders.Add(waveIn);
                waveIn.StartRecording();

                device.Status = "Recording";
            }
        }

        private void ResetUIAfterRecordingError()
        {
            _isRecording = false;
            if (StartRecordingButton != null) StartRecordingButton.IsEnabled = true;
            if (StopRecordingButton != null) StopRecordingButton.IsEnabled = false;
            if (PauseResumeButton != null) PauseResumeButton.IsEnabled = false;
            if (RecordingProgressBar != null) RecordingProgressBar.Visibility = Visibility.Collapsed;
            if (RecordingIndicator != null) RecordingIndicator.Fill = new SolidColorBrush(Colors.Gray);
            if (StatusTextBlock != null) StatusTextBlock.Text = "Recording failed";

            // Reset device status
            foreach (AudioDevice device in _audioDevices)
            {
                if (device != null)
                {
                    device.Status = "Available";
                }
            }
        }

        private void StopRecording_Click(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        private void StopRecording()
        {
            _isRecording = false;
            _recordingTimer?.Stop();

            // Check if we're using synchronized recording
            bool useSynchronizedRecording = SynchronizedRecordingCheckBox?.IsChecked ?? true;

            if (useSynchronizedRecording)
            {
                // Stop synchronized recorder
                try
                {
                    _synchronizedRecorder?.StopRecording();
                    _synchronizedRecorder?.Dispose();
                    _synchronizedRecorder = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error stopping synchronized recorder: {ex.Message}");
                    MessageBox.Show($"Error stopping recording: {ex.Message}", "Recording Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                // Stop separate recordings
                foreach (var recorder in _activeRecorders)
                {
                    try
                    {
                        recorder?.StopRecording();
                        recorder?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue stopping other recorders
                        System.Diagnostics.Debug.WriteLine($"Error stopping recorder: {ex.Message}");
                    }
                }

                // Close all wave writers
                foreach (var writer in _waveWriters)
                {
                    try
                    {
                        writer?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error disposing writer: {ex.Message}");
                    }
                }

                _activeRecorders.Clear();
                _waveWriters.Clear();
            }

            // Update device status
            foreach (AudioDevice device in _audioDevices)
            {
                if (device != null)
                {
                    device.Status = "Available";
                }
            }

            // Update UI with null checks
            if (StartRecordingButton != null) StartRecordingButton.IsEnabled = true;
            if (StopRecordingButton != null) StopRecordingButton.IsEnabled = false;
            if (PauseResumeButton != null)
            {
                PauseResumeButton.IsEnabled = false;
                PauseResumeButton.Content = "Pause";
            }
            if (RecordingProgressBar != null) RecordingProgressBar.Visibility = Visibility.Collapsed;
            if (RecordingIndicator != null) RecordingIndicator.Fill = new SolidColorBrush(Colors.Gray);
            if (StatusTextBlock != null) 
            {
                string mode = useSynchronizedRecording ? "synchronized" : "separate";
                string fileType = GetSelectedFileFormat();
                string message = useSynchronizedRecording ? 
                    $"Recording completed. {fileType} file saved to: {_currentOutputDirectory}" :
                    $"Recording completed. {fileType} files saved to: {_currentOutputDirectory}";
                StatusTextBlock.Text = message;
            }
            if (RecordingTimeTextBlock != null) RecordingTimeTextBlock.Text = "00:00:00";
        }

        private void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            bool useSynchronizedRecording = SynchronizedRecordingCheckBox?.IsChecked ?? true;

            if (_isPaused)
            {
                // Resume
                _isPaused = false;
                if (useSynchronizedRecording)
                {
                    _synchronizedRecorder?.ResumeRecording();
                }
                if (PauseResumeButton != null) PauseResumeButton.Content = "Pause";
                if (RecordingIndicator != null) RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
                if (StatusTextBlock != null) StatusTextBlock.Text = "Recording...";
                _recordingTimer?.Start();
            }
            else
            {
                // Pause
                _isPaused = true;
                if (useSynchronizedRecording)
                {
                    _synchronizedRecorder?.PauseRecording();
                }
                if (PauseResumeButton != null) PauseResumeButton.Content = "Resume";
                if (RecordingIndicator != null) RecordingIndicator.Fill = new SolidColorBrush(Colors.Orange);
                if (StatusTextBlock != null) StatusTextBlock.Text = "Recording paused";
                _recordingTimer?.Stop();
            }
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            if (_isRecording && !_isPaused && RecordingTimeTextBlock != null)
            {
                var elapsed = DateTime.Now - _recordingStartTime;
                RecordingTimeTextBlock.Text = elapsed.ToString(@"hh\:mm\:ss");
            }
        }

        private void OnRecordingError(object sender, Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"Recording error: {ex.Message}", "Recording Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                
                // Stop recording on error
                if (_isRecording)
                {
                    StopRecording();
                }
            });
        }

        private int GetSelectedSampleRate()
        {
            try
            {
                if (QualityComboBox?.SelectedItem is ComboBoxItem selected && selected.Content != null)
                {
                    return selected.Content.ToString() switch
                    {
                        "44.1 kHz" => 44100,
                        "48 kHz" => 48000,
                        "96 kHz" => 96000,
                        _ => 44100
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting selected sample rate: {ex.Message}");
            }
            return 44100; // Default fallback
        }

        private string GetSelectedFileFormat()
        {
            try
            {
                if (FileFormatComboBox?.SelectedItem is ComboBoxItem selected && selected.Content != null)
                {
                    return selected.Content.ToString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting selected file format: {ex.Message}");
            }
            return "WAV"; // Default fallback
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (_isRecording)
                {
                    var result = MessageBox.Show("Recording is in progress. Do you want to stop recording and exit?",
                        "Recording in Progress", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        StopRecording();
                    }
                    else
                    {
                        e.Cancel = true;
                        return;
                    }
                }

                // Clean up resources
                try
                {
                    _synchronizedRecorder?.Dispose();
                    _deviceEnumerator?.Dispose();
                    _recordingTimer?.Stop();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error during cleanup: {ex.Message}");
                }

                base.OnClosing(e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during window closing: {ex.Message}");
                base.OnClosing(e);
            }
        }
    }
}
