using System.ComponentModel;

namespace AudioRecorder
{
    public enum AudioDeviceType
    {
        Input,
        Output
    }

    public class AudioDevice : INotifyPropertyChanged
    {
        private string _deviceName;
        private int _channels;
        private int _sampleRate;
        private string _status;
        private int _deviceIndex;
        private AudioDeviceType _deviceType;
        private string _deviceTypeDisplay;

        public string DeviceName
        {
            get => _deviceName;
            set
            {
                _deviceName = value;
                OnPropertyChanged(nameof(DeviceName));
            }
        }

        public int Channels
        {
            get => _channels;
            set
            {
                _channels = value;
                OnPropertyChanged(nameof(Channels));
            }
        }

        public int SampleRate
        {
            get => _sampleRate;
            set
            {
                _sampleRate = value;
                OnPropertyChanged(nameof(SampleRate));
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }

        public int DeviceIndex
        {
            get => _deviceIndex;
            set
            {
                _deviceIndex = value;
                OnPropertyChanged(nameof(DeviceIndex));
            }
        }

        public AudioDeviceType DeviceType
        {
            get => _deviceType;
            set
            {
                _deviceType = value;
                _deviceTypeDisplay = value == AudioDeviceType.Input ? "Input" : "Output";
                OnPropertyChanged(nameof(DeviceType));
                OnPropertyChanged(nameof(DeviceTypeDisplay));
            }
        }

        public string DeviceTypeDisplay
        {
            get => _deviceTypeDisplay;
        }

        public AudioDevice(int deviceIndex, string deviceName, int channels, int sampleRate, AudioDeviceType deviceType)
        {
            DeviceIndex = deviceIndex;
            DeviceName = deviceName ?? "Unknown Device";
            Channels = channels > 0 ? channels : 2; // Default to stereo
            SampleRate = sampleRate > 0 ? sampleRate : 44100; // Default sample rate
            DeviceType = deviceType;
            Status = "Available";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString()
        {
            return $"{DeviceName ?? "Unknown"} ({Channels} channels, {SampleRate} Hz)";
        }
    }
}
