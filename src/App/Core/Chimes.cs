using System;
using System.IO;
using System.Media;
using System.Threading;

namespace Murmur.App.Core
{
    public enum Chime
    {
        Start,
        Stop,
        Error
    }

    public static class Chimes
    {
        private static readonly object cacheLock = new object();
        private static byte[] cachedStart;
        private static byte[] cachedStop;
        private static byte[] cachedError;

        public static void Play(Chime chime)
        {
            try
            {
                int frequencyHz;
                int milliseconds;
                if (chime == Chime.Start)
                {
                    frequencyHz = 880;
                    milliseconds = 70;
                }
                else if (chime == Chime.Stop)
                {
                    frequencyHz = 660;
                    milliseconds = 70;
                }
                else
                {
                    frequencyHz = 300;
                    milliseconds = 160;
                }
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try
                    {
                        byte[] wav = GetCached(chime, frequencyHz, milliseconds);
                        if (wav == null || wav.Length <= 44)
                        {
                            return;
                        }
                        using (MemoryStream stream = new MemoryStream(wav, false))
                        {
                            using (SoundPlayer player = new SoundPlayer(stream))
                            {
                                player.PlaySync();
                            }
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }

        private static byte[] GetCached(Chime chime, int frequencyHz, int milliseconds)
        {
            lock (cacheLock)
            {
                if (chime == Chime.Start)
                {
                    if (cachedStart == null)
                    {
                        cachedStart = BuildWav(frequencyHz, milliseconds, 0.25);
                    }
                    return cachedStart;
                }
                if (chime == Chime.Stop)
                {
                    if (cachedStop == null)
                    {
                        cachedStop = BuildWav(frequencyHz, milliseconds, 0.25);
                    }
                    return cachedStop;
                }
                if (cachedError == null)
                {
                    cachedError = BuildWav(frequencyHz, milliseconds, 0.25);
                }
                return cachedError;
            }
        }

        public static byte[] BuildWav(int frequencyHz, int milliseconds, double volume)
        {
            try
            {
                if (frequencyHz < 20)
                {
                    frequencyHz = 20;
                }
                if (frequencyHz > 12000)
                {
                    frequencyHz = 12000;
                }
                if (milliseconds < 1)
                {
                    milliseconds = 1;
                }
                if (milliseconds > 2000)
                {
                    milliseconds = 2000;
                }
                if (volume < 0.0)
                {
                    volume = 0.0;
                }
                if (volume > 1.0)
                {
                    volume = 1.0;
                }
                int sampleRate = 22050;
                int numSamples = sampleRate * milliseconds / 1000;
                if (numSamples < 1)
                {
                    numSamples = 1;
                }
                int fadeSamples = sampleRate * 8 / 1000;
                if (fadeSamples < 1)
                {
                    fadeSamples = 1;
                }
                if (fadeSamples * 2 > numSamples)
                {
                    fadeSamples = numSamples / 2;
                }
                short[] samples = new short[numSamples];
                double twoPiF = 2.0 * Math.PI * (double)frequencyHz;
                for (int i = 0; i < numSamples; i++)
                {
                    double t = (double)i / (double)sampleRate;
                    double sine = Math.Sin(twoPiF * t);
                    double envelope = 1.0;
                    if (fadeSamples > 0 && numSamples > 1)
                    {
                        if (i < fadeSamples)
                        {
                            envelope = (double)i / (double)fadeSamples;
                        }
                        else if (i >= numSamples - fadeSamples)
                        {
                            envelope = (double)(numSamples - 1 - i) / (double)fadeSamples;
                        }
                    }
                    double v = sine * volume * envelope;
                    if (v > 1.0)
                    {
                        v = 1.0;
                    }
                    if (v < -1.0)
                    {
                        v = -1.0;
                    }
                    samples[i] = (short)(v * 32767.0);
                }
                int dataBytes = numSamples * 2;
                byte[] wav = new byte[44 + dataBytes];
                wav[0] = (byte)'R';
                wav[1] = (byte)'I';
                wav[2] = (byte)'F';
                wav[3] = (byte)'F';
                WriteInt32(wav, 4, 36 + dataBytes);
                wav[8] = (byte)'W';
                wav[9] = (byte)'A';
                wav[10] = (byte)'V';
                wav[11] = (byte)'E';
                wav[12] = (byte)'f';
                wav[13] = (byte)'m';
                wav[14] = (byte)'t';
                wav[15] = (byte)' ';
                WriteInt32(wav, 16, 16);
                WriteInt16(wav, 20, 1);
                WriteInt16(wav, 22, 1);
                WriteInt32(wav, 24, sampleRate);
                WriteInt32(wav, 28, sampleRate * 2);
                WriteInt16(wav, 32, 2);
                WriteInt16(wav, 34, 16);
                wav[36] = (byte)'d';
                wav[37] = (byte)'a';
                wav[38] = (byte)'t';
                wav[39] = (byte)'a';
                WriteInt32(wav, 40, dataBytes);
                for (int i = 0; i < numSamples; i++)
                {
                    wav[44 + i * 2] = (byte)(samples[i] & 0xFF);
                    wav[44 + i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
                }
                return wav;
            }
            catch
            {
                return new byte[0];
            }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
