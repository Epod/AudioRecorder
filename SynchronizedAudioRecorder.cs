using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using NAudio.Lame;
using System.Threading;

namespace AudioRecorder
{
    /// <summary>
    /// Sample provider that adds silence at the beginning to synchronize with other streams
    /// </summary>
    public class TimeSynchronizedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _silenceSamples;
        private int _samplesRead = 0;

        public TimeSynchronizedSampleProvider(ISampleProvider source, TimeSpan delay, int sampleRate, int channels)
        {
            _source = source;
            _silenceSamples = (int)(delay.TotalSeconds * sampleRate * channels);
            WaveFormat = source.WaveFormat; // Use the source's wave format
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesWritten = 0;

            // First, write silence if needed
            if (_samplesRead < _silenceSamples)
            {
                int silenceToWrite = Math.Min(count, _silenceSamples - _samplesRead);
                for (int i = 0; i < silenceToWrite; i++)
                {
                    buffer[offset + i] = 0f;
                }
                _samplesRead += silenceToWrite;
                samplesWritten = silenceToWrite;
                offset += silenceToWrite;
                count -= silenceToWrite;
            }

            // Then read from the actual source
            if (count > 0)
            {
                int sourceRead = _source.Read(buffer, offset, count);
                _samplesRead += sourceRead;
                samplesWritten += sourceRead;
            }

            return samplesWritten;
        }
    }

    public class SynchronizedAudioRecorder : IDisposable
    {
        private readonly List<IWaveIn> _recorders;
        private readonly List<MemoryStream> _recordingStreams;
        private readonly List<WaveFileWriter> _tempWriters;
        private readonly List<AudioDevice> _devices;
        private readonly List<DateTime> _recorderStartTimes; // Track actual start time for each recorder
        private readonly List<bool> _recorderStarted; // Track which recorders have started
        private readonly List<DateTime> _recorderFirstDataTimes; // Track when each recorder first received data (fallback)
        private readonly List<DateTime> _recorderLastDataTimes; // Track when each recorder last received data
        private readonly List<bool> _recorderStoppedUnexpectedly; // Track which recorders stopped unexpectedly
        private readonly List<bool> _recorderRestartAttempted; // Track restart attempts
        private Timer _monitoringTimer; // Timer to monitor recording health
        private WaveFileWriter _finalWriter;
        private readonly object _lockObject = new object();
        private bool _isRecording = false;
        private bool _isPaused = false;
        private DateTime _startTime;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly WaveFormat _targetFormat;
        private Task _mixingTask;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly string _outputFormat;

        public event EventHandler<Exception> RecordingError;

        public SynchronizedAudioRecorder(List<AudioDevice> devices, int sampleRate = 44100, int channels = 2, string outputFormat = "WAV")
        {
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _sampleRate = sampleRate;
            _channels = channels;
            _outputFormat = outputFormat.ToUpper();
            _targetFormat = new WaveFormat(_sampleRate, 16, _channels); // 16-bit PCM
            _recorders = new List<IWaveIn>();
            _recordingStreams = new List<MemoryStream>();
            _tempWriters = new List<WaveFileWriter>();
            _recorderStartTimes = new List<DateTime>();
            _recorderStarted = new List<bool>();
            _recorderFirstDataTimes = new List<DateTime>();
            _recorderLastDataTimes = new List<DateTime>();
            _recorderStoppedUnexpectedly = new List<bool>();
            _recorderRestartAttempted = new List<bool>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task StartRecording(string outputPath)
        {
            if (_isRecording)
                throw new InvalidOperationException("Recording is already in progress");

            try
            {
                // Initialize recorders for each device
                var deviceEnumerator = new MMDeviceEnumerator();
                var inputDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                var outputDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                var inputDeviceCount = inputDevices.Count;

                // Create temporary recording streams for each device
                foreach (var device in _devices)
                {
                    IWaveIn waveIn;
                    
                    if (device.DeviceType == AudioDeviceType.Input)
                    {
                        if (device.DeviceIndex >= 0 && device.DeviceIndex < inputDevices.Count)
                        {
                            var mmDevice = inputDevices[device.DeviceIndex];
                            waveIn = new WasapiCapture(mmDevice);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Input device index {device.DeviceIndex} not found");
                        }
                    }
                    else // Output device
                    {
                        var deviceIndexAdjusted = device.DeviceIndex - inputDeviceCount;
                        if (deviceIndexAdjusted >= 0 && deviceIndexAdjusted < outputDevices.Count)
                        {
                            var mmDevice = outputDevices[deviceIndexAdjusted];
                            System.Diagnostics.Debug.WriteLine($"Setting up WasapiLoopbackCapture for device: {mmDevice.FriendlyName}");
                            System.Diagnostics.Debug.WriteLine($"Device state: {mmDevice.State}");
                            System.Diagnostics.Debug.WriteLine($"Device ID: {mmDevice.ID}");
                            
                            waveIn = new WasapiLoopbackCapture(mmDevice);
                            
                            // Log the wave format being used
                            System.Diagnostics.Debug.WriteLine($"WasapiLoopbackCapture WaveFormat: {waveIn.WaveFormat}");
                            System.Diagnostics.Debug.WriteLine($"Share mode: {((WasapiLoopbackCapture)waveIn).ShareMode}");
                        }
                        else
                        {
                            throw new InvalidOperationException($"Output device index {deviceIndexAdjusted} not found");
                        }
                    }

                    // Create memory stream and wave writer for this device
                    var stream = new MemoryStream();
                    var writer = new WaveFileWriter(stream, waveIn.WaveFormat);

                    // Initialize tracking variables for this recorder
                    var recorderIndex = _recorders.Count;
                    _recorderStartTimes.Add(DateTime.MinValue); // Will be set when recording actually starts
                    _recorderStarted.Add(false);
                    _recorderFirstDataTimes.Add(DateTime.MinValue); // Track first data received
                    _recorderLastDataTimes.Add(DateTime.MinValue); // Track last data received
                    _recorderStoppedUnexpectedly.Add(false); // Track unexpected stops
                    _recorderRestartAttempted.Add(false); // Track restart attempts
                    var firstDataReceived = false;

                    // Handle data from this device
                    waveIn.DataAvailable += (sender, e) =>
                    {
                        if (!_isPaused && _isRecording)
                        {
                            lock (_lockObject)
                            {
                                // Update last data time
                                _recorderLastDataTimes[recorderIndex] = DateTime.Now;
                                
                                // Record first data time as fallback
                                if (!firstDataReceived)
                                {
                                    _recorderFirstDataTimes[recorderIndex] = DateTime.Now;
                                    firstDataReceived = true;
                                    System.Diagnostics.Debug.WriteLine($"Recorder {recorderIndex} ({device.DeviceName}) received first data at: {_recorderFirstDataTimes[recorderIndex]:HH:mm:ss.fff}");
                                }
                                
                                // Record the actual start time for this recorder on first non-silent data
                                if (!_recorderStarted[recorderIndex])
                                {
                                    // Check if this buffer contains actual audio (not just silence)
                                    bool hasAudio = HasAudioContent(e.Buffer, e.BytesRecorded, waveIn.WaveFormat);
                                    
                                    if (hasAudio)
                                    {
                                        _recorderStartTimes[recorderIndex] = DateTime.Now;
                                        _recorderStarted[recorderIndex] = true;
                                        System.Diagnostics.Debug.WriteLine($"Recorder {recorderIndex} ({device.DeviceName}) started with audio at: {_recorderStartTimes[recorderIndex]:HH:mm:ss.fff}");
                                    }
                                }
                                
                                writer.Write(e.Buffer, 0, e.BytesRecorded);
                            }
                        }
                    };

                    waveIn.RecordingStopped += (sender, e) =>
                    {
                        var recorderType = waveIn is WasapiLoopbackCapture ? "WasapiLoopbackCapture" : "WasapiCapture";
                        var stopTime = DateTime.Now;
                        var recordingDuration = stopTime - _startTime;
                        
                        System.Diagnostics.Debug.WriteLine($"RecordingStopped event fired for {recorderType} (Recorder {recorderIndex}, {device.DeviceName})");
                        System.Diagnostics.Debug.WriteLine($"Recording duration: {recordingDuration.TotalSeconds:F3} seconds");
                        
                        // Check if this was an unexpected stop (recording still active but device stopped)
                        if (_isRecording)
                        {
                            _recorderStoppedUnexpectedly[recorderIndex] = true;
                            System.Diagnostics.Debug.WriteLine($"WARNING: {device.DeviceName} stopped unexpectedly while recording is still active!");
                            
                            // For WasapiLoopbackCapture, attempt restart if not already tried
                            if (waveIn is WasapiLoopbackCapture && !_recorderRestartAttempted[recorderIndex])
                            {
                                System.Diagnostics.Debug.WriteLine($"Attempting to restart {device.DeviceName}...");
                                _recorderRestartAttempted[recorderIndex] = true;
                                
                                // Schedule restart attempt on a background thread
                                Task.Run(() => AttemptRecorderRestart(recorderIndex));
                            }
                        }
                        
                        if (e.Exception != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Recording stopped with exception: {e.Exception.Message}");
                            System.Diagnostics.Debug.WriteLine($"Exception details: {e.Exception}");
                            RecordingError?.Invoke(this, e.Exception);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Recording stopped normally for {device.DeviceName}");
                        }
                    };

                    _recorders.Add(waveIn);
                    _recordingStreams.Add(stream);
                    _tempWriters.Add(writer);
                    
                    // Log device setup completion
                    System.Diagnostics.Debug.WriteLine($"Device {recorderIndex} ({device.DeviceName}) setup completed. Type: {(waveIn is WasapiLoopbackCapture ? "WasapiLoopbackCapture" : "WasapiCapture")}");
                }

                // Start recording
                _isRecording = true;
                _startTime = DateTime.Now;
                System.Diagnostics.Debug.WriteLine($"Starting synchronized recording at: {_startTime:HH:mm:ss.fff}");

                // Start all recorders simultaneously
                for (int i = 0; i < _recorders.Count; i++)
                {
                    var recorder = _recorders[i];
                    var deviceName = _devices[i].DeviceName;
                    var recorderType = recorder is WasapiLoopbackCapture ? "WasapiLoopbackCapture" : "WasapiCapture";
                    
                    System.Diagnostics.Debug.WriteLine($"Starting recorder {i}: {deviceName} ({recorderType})");
                    recorder.StartRecording();
                    System.Diagnostics.Debug.WriteLine($"Recorder {i} StartRecording() call completed");
                }

                // Start mixing task that will create the final mixed file
                _mixingTask = Task.Run(() => CreateMixedOutput(outputPath, _cancellationTokenSource.Token));
                
                // Start monitoring timer to check recording health
                _monitoringTimer = new Timer(MonitorRecordingHealth, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Dispose();
                throw new InvalidOperationException($"Failed to start synchronized recording: {ex.Message}", ex);
            }
        }

        private void MonitorRecordingHealth(object state)
        {
            if (!_isRecording)
                return;

            try
            {
                var currentTime = DateTime.Now;
                var totalRecordingTime = currentTime - _startTime;
                
                System.Diagnostics.Debug.WriteLine($"=== Recording Health Check at {totalRecordingTime.TotalSeconds:F1}s ===");
                
                for (int i = 0; i < _recorders.Count && i < _devices.Count; i++)
                {
                    var device = _devices[i];
                    var lastDataTime = _recorderLastDataTimes[i];
                    var stoppedUnexpectedly = _recorderStoppedUnexpectedly[i];
                    
                    if (stoppedUnexpectedly)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Recorder {i} ({device.DeviceName}): STOPPED UNEXPECTEDLY");
                    }
                    else if (lastDataTime != DateTime.MinValue)
                    {
                        var timeSinceLastData = currentTime - lastDataTime;
                        System.Diagnostics.Debug.WriteLine($"  Recorder {i} ({device.DeviceName}): Last data {timeSinceLastData.TotalSeconds:F1}s ago");
                        
                        // Warn if no data for too long (especially concerning for loopback capture)
                        if (timeSinceLastData.TotalSeconds > 5)
                        {
                            var recorderType = _recorders[i] is WasapiLoopbackCapture ? "WasapiLoopbackCapture" : "WasapiCapture";
                            System.Diagnostics.Debug.WriteLine($"    WARNING: {recorderType} hasn't received data for {timeSinceLastData.TotalSeconds:F1}s");
                            
                            if (recorderType == "WasapiLoopbackCapture" && timeSinceLastData.TotalSeconds > 3)
                            {
                                System.Diagnostics.Debug.WriteLine($"    SUGGESTION: Check if audio is playing through {device.DeviceName}");
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  Recorder {i} ({device.DeviceName}): No data received yet");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in MonitorRecordingHealth: {ex.Message}");
            }
        }

        private async void AttemptRecorderRestart(int recorderIndex)
        {
            try
            {
                if (!_isRecording || recorderIndex >= _devices.Count || recorderIndex >= _recorders.Count)
                    return;

                var device = _devices[recorderIndex];
                System.Diagnostics.Debug.WriteLine($"Attempting to restart recorder {recorderIndex} ({device.DeviceName})");

                // Wait a moment before restart attempt
                await Task.Delay(1000);

                if (!_isRecording) // Check again after delay
                    return;

                // Create new recorder for this device
                var deviceEnumerator = new MMDeviceEnumerator();
                var outputDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                var inputDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                var inputDeviceCount = inputDevices.Count;

                IWaveIn newWaveIn = null;
                
                if (device.DeviceType == AudioDeviceType.Output)
                {
                    var deviceIndexAdjusted = device.DeviceIndex - inputDeviceCount;
                    if (deviceIndexAdjusted >= 0 && deviceIndexAdjusted < outputDevices.Count)
                    {
                        var mmDevice = outputDevices[deviceIndexAdjusted];
                        newWaveIn = new WasapiLoopbackCapture(mmDevice);
                        System.Diagnostics.Debug.WriteLine($"Created new WasapiLoopbackCapture for {device.DeviceName}");
                    }
                }

                if (newWaveIn == null)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to create new recorder for {device.DeviceName}");
                    return;
                }

                // Set up event handlers for the new recorder
                newWaveIn.DataAvailable += (sender, e) =>
                {
                    if (!_isPaused && _isRecording)
                    {
                        lock (_lockObject)
                        {
                            _recorderLastDataTimes[recorderIndex] = DateTime.Now;
                            
                            if (recorderIndex < _tempWriters.Count && _tempWriters[recorderIndex] != null)
                            {
                                _tempWriters[recorderIndex].Write(e.Buffer, 0, e.BytesRecorded);
                                System.Diagnostics.Debug.WriteLine($"Restarted recorder {recorderIndex} writing data");
                            }
                        }
                    }
                };

                newWaveIn.RecordingStopped += (sender, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Restarted recorder {recorderIndex} stopped again");
                    if (e.Exception != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Restart failed with exception: {e.Exception.Message}");
                    }
                };

                // Replace the old recorder
                lock (_lockObject)
                {
                    try
                    {
                        _recorders[recorderIndex]?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error disposing old recorder: {ex.Message}");
                    }

                    _recorders[recorderIndex] = newWaveIn;
                    _recorderStoppedUnexpectedly[recorderIndex] = false;
                }

                // Start the new recorder
                newWaveIn.StartRecording();
                System.Diagnostics.Debug.WriteLine($"Restarted recorder {recorderIndex} ({device.DeviceName}) successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error restarting recorder {recorderIndex}: {ex.Message}");
            }
        }

        private async Task CreateMixedOutput(string outputPath, CancellationToken cancellationToken)
        {
            try
            {
                // Wait for recording to finish
                while (_isRecording)
                {
                    // Check for cancellation but don't exit immediately - allow normal stop
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Only return if recording was forcefully cancelled, not normally stopped
                        if (_isRecording)
                        {
                            System.Diagnostics.Debug.WriteLine("Recording was cancelled while still active");
                            return;
                        }
                        break;
                    }
                    
                    await Task.Delay(100, CancellationToken.None); // Don't use cancellation token for delay
                }

                // Recording has finished normally, proceed with mixing
                System.Diagnostics.Debug.WriteLine("Recording finished, starting mixing process...");

                // Close all temp writers to finalize the streams but keep streams open
                lock (_lockObject)
                {
                    foreach (var writer in _tempWriters)
                    {
                        writer?.Flush();
                        // Don't dispose the writer here - we need the underlying stream for mixing
                        // The dispose will happen in the main Dispose method
                    }
                }

                // Now mix all the recorded streams
                await MixRecordedStreams(outputPath);
                
                System.Diagnostics.Debug.WriteLine($"Mixing completed successfully. File saved to: {outputPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in CreateMixedOutput: {ex.Message}");
                RecordingError?.Invoke(this, ex);
            }
        }

        private async Task MixRecordedStreams(string outputPath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Starting MixRecordedStreams with {_recordingStreams.Count} streams");
                
                if (_recordingStreams.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("No recording streams available for mixing");
                    return;
                }

                // Create a temporary WAV file for mixing
                string tempWavPath = outputPath;
                if (_outputFormat == "MP3")
                {
                    tempWavPath = Path.ChangeExtension(outputPath, ".tmp.wav");
                }

                System.Diagnostics.Debug.WriteLine($"Output path: {outputPath}, Temp path: {tempWavPath}");

                // If only one device, just copy the stream
                if (_recordingStreams.Count == 1)
                {
                    System.Diagnostics.Debug.WriteLine("Single stream - copying directly");
                    var singleStream = _recordingStreams[0];
                    if (singleStream != null && singleStream.CanRead)
                    {
                        // Log start time for single stream too
                        if (_recorderStarted[0] && _recorderStartTimes[0] != DateTime.MinValue)
                        {
                            System.Diagnostics.Debug.WriteLine($"Single stream start time: {_recorderStartTimes[0]:HH:mm:ss.fff}");
                        }
                        
                        singleStream.Position = 0;
                        using (var fileStream = File.Create(tempWavPath))
                        {
                            await singleStream.CopyToAsync(fileStream);
                        }
                        System.Diagnostics.Debug.WriteLine($"Single stream copied. File size: {new FileInfo(tempWavPath).Length} bytes");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Single stream is not readable");
                        throw new InvalidOperationException("Recording stream is not available for processing");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Multiple streams - mixing");
                    // For multiple devices, we need to mix them
                    await MixMultipleStreams(tempWavPath);
                }

                // Convert to MP3 if requested
                if (_outputFormat == "MP3")
                {
                    System.Diagnostics.Debug.WriteLine("Converting to MP3");
                    await ConvertWavToMp3(tempWavPath, outputPath);
                    
                    // Clean up temporary WAV file
                    try
                    {
                        File.Delete(tempWavPath);
                        System.Diagnostics.Debug.WriteLine("Temporary WAV file deleted");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Warning: Could not delete temporary file {tempWavPath}: {ex.Message}");
                    }
                }
                
                // Verify final file exists
                if (File.Exists(outputPath))
                {
                    var fileInfo = new FileInfo(outputPath);
                    System.Diagnostics.Debug.WriteLine($"Final file created successfully: {outputPath} ({fileInfo.Length} bytes)");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR: Final file was not created: {outputPath}");
                }

                // Now dispose the temp writers since mixing is complete
                lock (_lockObject)
                {
                    foreach (var writer in _tempWriters)
                    {
                        try
                        {
                            writer?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error disposing temp writer after mixing: {ex.Message}");
                        }
                    }
                    _tempWriters.Clear();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in MixRecordedStreams: {ex.Message}");
                RecordingError?.Invoke(this, ex);
            }
        }

        private async Task MixMultipleStreams(string outputPath)
        {
            try
            {
                if (_recordingStreams.Count == 0)
                    return;

                // Check if any streams are valid before proceeding
                var validStreams = _recordingStreams.Where(s => s != null && s.CanRead).ToList();
                if (validStreams.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("No valid streams available for mixing");
                    return;
                }

                // Calculate synchronization offsets
                // Get all valid start times (either audio start times or first data times as fallback)
                var validStartTimes = new List<DateTime>();
                for (int i = 0; i < _recorderStartTimes.Count; i++)
                {
                    if (_recorderStarted[i] && _recorderStartTimes[i] != DateTime.MinValue)
                    {
                        validStartTimes.Add(_recorderStartTimes[i]);
                    }
                    else if (_recorderFirstDataTimes[i] != DateTime.MinValue)
                    {
                        validStartTimes.Add(_recorderFirstDataTimes[i]);
                    }
                }
                
                if (validStartTimes.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("No valid start times found for synchronization");
                    return;
                }
                
                var earliestStartTime = validStartTimes.Min();
                System.Diagnostics.Debug.WriteLine($"Earliest start time: {earliestStartTime:HH:mm:ss.fff}");

                var waveReaders = new List<WaveFileReader>();
                var sampleProviders = new List<ISampleProvider>();

                try
                {
                    // Create wave readers from memory streams
                    for (int i = 0; i < _recordingStreams.Count; i++)
                    {
                        var stream = _recordingStreams[i];
                        if (stream == null || !stream.CanRead)
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipping invalid or closed stream {i}");
                            continue;
                        }

                        // Use fallback timing if recorder never detected audio
                        if (!_recorderStarted[i] && _recorderFirstDataTimes[i] != DateTime.MinValue)
                        {
                            System.Diagnostics.Debug.WriteLine($"Using fallback timing for recorder {i} - first data time: {_recorderFirstDataTimes[i]:HH:mm:ss.fff}");
                        }

                        stream.Position = 0;
                        var reader = new WaveFileReader(stream);
                        waveReaders.Add(reader);

                        // Convert to sample provider and normalize format
                        var sampleProvider = reader.ToSampleProvider();
                        
                        // Resample if needed
                        if (reader.WaveFormat.SampleRate != _sampleRate)
                        {
                            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, _sampleRate);
                        }
                        
                        // Convert channels if needed
                        if (reader.WaveFormat.Channels == 1 && _channels == 2)
                        {
                            sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                        }
                        else if (reader.WaveFormat.Channels == 2 && _channels == 1)
                        {
                            sampleProvider = sampleProvider.ToMono();
                        }

                        // Calculate time offset for synchronization
                        DateTime recorderTime;
                        if (_recorderStarted[i] && _recorderStartTimes[i] != DateTime.MinValue)
                        {
                            recorderTime = _recorderStartTimes[i];
                            System.Diagnostics.Debug.WriteLine($"Recorder {i} using audio start time: {recorderTime:HH:mm:ss.fff}");
                        }
                        else if (_recorderFirstDataTimes[i] != DateTime.MinValue)
                        {
                            recorderTime = _recorderFirstDataTimes[i];
                            System.Diagnostics.Debug.WriteLine($"Recorder {i} using fallback first data time: {recorderTime:HH:mm:ss.fff}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipping recorder {i} - no timing data available");
                            continue;
                        }
                        
                        var timeOffset = recorderTime - earliestStartTime;
                        System.Diagnostics.Debug.WriteLine($"Recorder {i} time offset: {timeOffset.TotalMilliseconds} ms");

                        // Apply time synchronization if there's an offset
                        if (timeOffset.TotalMilliseconds > 0)
                        {
                            sampleProvider = new TimeSynchronizedSampleProvider(sampleProvider, timeOffset, _sampleRate, _channels);
                        }

                        // Apply volume normalization for better mixing
                        // Reduce volume when mixing multiple sources to prevent clipping
                        float volumeAdjustment = 1.0f / (float)Math.Sqrt(_recordingStreams.Count);
                        var volumeProvider = new VolumeSampleProvider(sampleProvider, volumeAdjustment);

                        sampleProviders.Add(volumeProvider);
                    }

                    if (sampleProviders.Count == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("No valid sample providers available for mixing");
                        return;
                    }

                    // Create mixer
                    var mixer = new MixingSampleProvider(sampleProviders);
                    mixer.ReadFully = false;

                    // Write mixed output
                    using (var writer = new WaveFileWriter(outputPath, _targetFormat))
                    {
                        var buffer = new float[_sampleRate * _channels]; // 1 second buffer
                        int samplesRead;
                        
                        do
                        {
                            samplesRead = mixer.Read(buffer, 0, buffer.Length);
                            if (samplesRead > 0)
                            {
                                writer.WriteSamples(buffer, 0, samplesRead);
                            }
                        } while (samplesRead > 0);
                    }

                    System.Diagnostics.Debug.WriteLine($"Mixed {sampleProviders.Count} synchronized streams successfully");
                }
                finally
                {
                    // Clean up readers
                    foreach (var reader in waveReaders)
                    {
                        reader?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in MixMultipleStreams: {ex.Message}");
                RecordingError?.Invoke(this, ex);
            }
        }

        private async Task ConvertWavToMp3(string wavPath, string mp3Path)
        {
            try
            {
                await Task.Run(() =>
                {
                    using (var reader = new AudioFileReader(wavPath))
                    {
                        // Use LAMEPreset.STANDARD for good quality/size balance
                        // You can change to LAMEPreset.EXTREME for higher quality
                        MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, 128000); // 128 kbps
                    }
                });
            }
            catch (Exception ex)
            {
                // If MP3 encoding fails, log error but don't crash
                System.Diagnostics.Debug.WriteLine($"MP3 encoding failed: {ex.Message}");
                RecordingError?.Invoke(this, new Exception($"MP3 encoding failed: {ex.Message}. WAV file saved instead.", ex));
                
                // Copy the WAV file as fallback
                try
                {
                    File.Copy(wavPath, Path.ChangeExtension(mp3Path, ".wav"), true);
                }
                catch (Exception copyEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to copy WAV as fallback: {copyEx.Message}");
                }
            }
        }

        public void PauseRecording()
        {
            _isPaused = true;
        }

        public void ResumeRecording()
        {
            _isPaused = false;
        }

        public void StopRecording()
        {
            System.Diagnostics.Debug.WriteLine("StopRecording called");
            _isRecording = false;
            
            // Stop monitoring timer
            try
            {
                _monitoringTimer?.Dispose();
                _monitoringTimer = null;
                System.Diagnostics.Debug.WriteLine("Monitoring timer stopped");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping monitoring timer: {ex.Message}");
            }
            
            // Stop all recorders first
            System.Diagnostics.Debug.WriteLine($"Stopping {_recorders.Count} recorders");
            foreach (var recorder in _recorders)
            {
                try
                {
                    recorder?.StopRecording();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error stopping recorder: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("All recorders stopped, waiting for mixing to complete");
            
            // DO NOT cancel the mixing task here - let it complete to save the file
            // The mixing task will detect that _isRecording is false and proceed with mixing
            
            // Wait for mixing task to complete (this is where the file gets saved)
            try
            {
                if (_mixingTask != null)
                {
                    System.Diagnostics.Debug.WriteLine("Waiting for mixing task to complete...");
                    _mixingTask.Wait(10000); // Wait up to 10 seconds for mixing to complete
                    System.Diagnostics.Debug.WriteLine("Mixing task completed");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No mixing task to wait for");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error waiting for mixing task: {ex.Message}");
                // Even if there's an error, don't cancel - the file might still be saved
            }
        }

        public TimeSpan GetRecordingDuration()
        {
            return _isRecording ? DateTime.Now - _startTime : TimeSpan.Zero;
        }

        public bool IsRecording => _isRecording;
        public bool IsPaused => _isPaused;

        public void Dispose()
        {
            // If recording is still active, stop it properly first
            if (_isRecording)
            {
                StopRecording();
            }

            // Only cancel tasks if they're still running after stop
            try
            {
                if (_mixingTask != null && !_mixingTask.IsCompleted)
                {
                    _cancellationTokenSource?.Cancel();
                    _mixingTask?.Wait(2000); // Wait up to 2 seconds
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cancelling tasks: {ex.Message}");
            }

            // Dispose recorders
            foreach (var recorder in _recorders)
            {
                try
                {
                    recorder?.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error disposing recorder: {ex.Message}");
                }
            }
            _recorders.Clear();

            // Dispose temp writers (if not already disposed after mixing)
            foreach (var writer in _tempWriters)
            {
                try
                {
                    writer?.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error disposing temp writer: {ex.Message}");
                }
            }
            _tempWriters.Clear();

            // Dispose recording streams
            foreach (var stream in _recordingStreams)
            {
                try
                {
                    stream?.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error disposing stream: {ex.Message}");
                }
            }
            _recordingStreams.Clear();

            // Clear timing tracking lists
            _recorderStartTimes.Clear();
            _recorderStarted.Clear();
            _recorderFirstDataTimes.Clear();
            _recorderLastDataTimes.Clear();
            _recorderStoppedUnexpectedly.Clear();
            _recorderRestartAttempted.Clear();

            // Dispose monitoring timer
            try
            {
                _monitoringTimer?.Dispose();
                _monitoringTimer = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing monitoring timer: {ex.Message}");
            }

            // Dispose final writer
            try
            {
                _finalWriter?.Dispose();
                _finalWriter = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing final writer: {ex.Message}");
            }

            // Dispose cancellation token source
            try
            {
                _cancellationTokenSource?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing cancellation token source: {ex.Message}");
            }
        }

        private bool HasAudioContent(byte[] buffer, int bytesRecorded, WaveFormat waveFormat)
        {
            // Determine the threshold based on bit depth
            double threshold = 0.001; // Threshold for detecting non-silence (as a fraction of max amplitude)
            
            if (waveFormat.BitsPerSample == 16)
            {
                // 16-bit samples
                int sampleThreshold = (int)(threshold * short.MaxValue);
                for (int i = 0; i < bytesRecorded - 1; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    if (Math.Abs(sample) > sampleThreshold)
                    {
                        return true;
                    }
                }
            }
            else if (waveFormat.BitsPerSample == 32)
            {
                // 32-bit float samples
                float floatThreshold = (float)threshold;
                for (int i = 0; i < bytesRecorded - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    if (Math.Abs(sample) > floatThreshold)
                    {
                        return true;
                    }
                }
            }
            else if (waveFormat.BitsPerSample == 24)
            {
                // 24-bit samples (less common)
                int sampleThreshold = (int)(threshold * (1 << 23)); // 2^23 for 24-bit
                for (int i = 0; i < bytesRecorded - 2; i += 3)
                {
                    // Convert 24-bit to int32
                    int sample = (buffer[i + 2] << 16) | (buffer[i + 1] << 8) | buffer[i];
                    if ((sample & 0x800000) != 0) // Sign extend
                        sample |= unchecked((int)0xFF000000);
                    
                    if (Math.Abs(sample) > sampleThreshold)
                    {
                        return true;
                    }
                }
            }
            
            return false; // No audio content detected
        }
    }
}
