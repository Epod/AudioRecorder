# Multi-Source Audio Recorder

A Windows desktop application that can simultaneously record from multiple audio sources including both input devices (microphones, headsets, line-in) and output devices (speakers, headphones via loopback recording). The application compiles to a single executable file with all dependencies bundled, requiring no admin rights to run.

## Features

- **Multiple Audio Source Recording**: Record from multiple audio input AND output devices simultaneously
- **Synchronized Recording Mode**: Combine multiple device recordings into a single mixed file with perfect timing
- **Separate Recording Mode**: Save individual files for each device with original quality
- **Real-time Device Detection**: Automatically detects and lists available audio input and output devices
- **Flexible Output Options**: Choose output directory and file format (WAV/MP3)
- **MP3 Encoding Support**: High-quality MP3 output with configurable bitrate
- **Advanced Audio Mixing**: Automatic volume normalization and format conversion for optimal quality
- **Recording Controls**: Start, stop, pause, and resume recordings
- **Live Recording Timer**: Shows current recording duration
- **No Admin Rights Required**: Runs on systems without administrator privileges
- **Single Executable**: All dependencies bundled into one .exe file

## System Requirements

- Windows 10 or later (64-bit)
- Audio input devices (microphones, headsets, etc.)
- No additional software installation required

## Getting the Application

### Option 1: Download Pre-built Release (Recommended)
1. Go to the [Releases page](../../releases)
2. Download the latest `AudioRecorder.exe` or `AudioRecorder-Windows-x64.zip`
3. Run the executable directly - no installation required!

### Option 2: Build from Source

#### Prerequisites
- .NET 6 SDK or later
- Windows development environment

#### Local Build Instructions

1. **Clone or download** this repository to your local machine

2. **Open command prompt** in the project directory

3. **Run the build script**:
   ```batch
   build.bat
   ```
   
   Alternatively, you can build manually using:
   ```batch
   dotnet publish --configuration Release --runtime win-x64 --self-contained true --output "./dist" /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishReadyToRun=true
   ```

4. **Find the executable** in the `dist` folder: `AudioRecorder.exe`

## Usage

### Starting the Application
1. Double-click `AudioRecorder.exe` to launch
2. The application will automatically detect available audio input devices

### Recording Audio
1. **Select Audio Sources**: 
   - Available audio devices appear in the main list
   - **Input devices**: Microphones, line-in, headsets (direct recording)
   - **Output devices**: Speakers, headphones (loopback recording of system audio)
   - Use checkboxes to filter device types (Input/Output)
   - Select one or more devices by clicking (hold Ctrl for multiple selections)
   - Use "Refresh Devices" to update the device list

2. **Configure Output Settings**:
   - Set output directory using the "Browse" button
   - Choose file format (WAV recommended for highest quality)
   - Select sample rate/quality (44.1 kHz is standard)

3. **Start Recording**:
   - Click "Start Recording" to begin
   - **Synchronized Mode** (default): Creates a single mixed file from all selected devices
   - **Separate Mode**: Creates individual files for each device (uncheck "Synchronized Recording")
   - Recording indicator shows red when active

4. **Control Recording**:
   - **Pause/Resume**: Temporarily stop/continue recording
   - **Stop**: End recording and save files
   - Recording timer shows elapsed time

### Output Files
- **Synchronized Mode**: Single mixed file with all audio sources combined
  - File naming: `MultiDevice_Recording_YYYYMMDD_HHMMSS.wav` or `.mp3`
- **Separate Mode**: Individual files for each device
  - File naming: `DeviceName_YYYYMMDD_HHMMSS.wav` or `.mp3`
- All files are saved to the specified output directory
- Default location: `Documents/AudioRecordings`

## Technical Details

### Architecture
- **Framework**: .NET 6 WPF (Windows Presentation Foundation)
- **Audio Library**: NAudio for Windows audio recording
- **Deployment**: Self-contained single-file executable

### Audio Specifications
- **Supported Formats**: WAV (uncompressed), MP3 (future enhancement)
- **Sample Rates**: 44.1 kHz, 48 kHz, 96 kHz
- **Channels**: Supports mono and stereo devices
- **Bit Depth**: 16-bit standard

## Troubleshooting

### Common Issues

**No audio devices detected:**
- Ensure audio devices are properly connected
- Check Windows sound settings
- Try clicking "Refresh Sources"

**Recording fails to start:**
- Verify output directory exists and is writable
- Check that audio devices aren't being used by other applications
- Ensure sufficient disk space

**Poor audio quality:**
- Try different sample rates
- Check device input levels in Windows
- Ensure devices are functioning properly

### Audio Device Compatibility
The application works with any Windows-compatible audio device:

**Input Devices (Direct Recording)**:
- USB microphones
- Built-in laptop microphones
- Professional audio interfaces
- Headset microphones
- Line-in connections
- XLR microphones (via audio interface)

**Output Devices (Loopback Recording)**:
- System speakers
- Headphones/earbuds
- USB audio devices
- Professional audio interfaces
- Virtual audio cables
- Any device that plays system audio