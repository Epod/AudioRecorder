using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioRecorder
{
    /// <summary>
    /// Volume-adjustable sample provider for better audio mixing control
    /// </summary>
    public class VolumeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _volume;

        public VolumeSampleProvider(ISampleProvider source, float volume = 1.0f)
        {
            _source = source;
            _volume = volume;
            WaveFormat = source.WaveFormat;
        }

        public float Volume
        {
            get => _volume;
            set => _volume = Math.Max(0, Math.Min(2.0f, value)); // Clamp between 0 and 2.0
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            if (_volume != 1.0f)
            {
                for (int i = offset; i < offset + samplesRead; i++)
                {
                    buffer[i] *= _volume;
                }
            }

            return samplesRead;
        }
    }
}
