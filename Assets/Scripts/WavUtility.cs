using System;
using System.IO;
using System.Text;
using UnityEngine;

public static class WavUtility
{
    // 1. Converts Unity Audio to WAV byte array (For Uploading)
    public static byte[] FromAudioClip(AudioClip clip, int sampleCount)
    {
        using (var stream = new MemoryStream())
        {
            var writer = new BinaryWriter(stream);

            float[] samples = new float[sampleCount * clip.channels];
            clip.GetData(samples, 0);

            // Write directly to byte array — no need for intermediate Int16 array
            byte[] bytesData = new byte[samples.Length * 2];
            const int rescaleFactor = 32767;

            for (int i = 0; i < samples.Length; i++)
            {
                short val = (short)(samples[i] * rescaleFactor);
                bytesData[i * 2] = (byte)(val & 0xFF);
                bytesData[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + bytesData.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)clip.channels);
            writer.Write(clip.frequency);
            writer.Write(clip.frequency * clip.channels * 2);
            writer.Write((ushort)(clip.channels * 2));
            writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(bytesData.Length);
            writer.Write(bytesData);

            return stream.ToArray();
        }
    }

    // 2. Converts WAV byte array back to Unity AudioClip (For AI Response)
    public static AudioClip ToAudioClip(byte[] wavFile)
    {
        if (wavFile == null || wavFile.Length < 44) return null;

        try
        {
            int channels = BitConverter.ToInt16(wavFile, 22);
            int frequency = BitConverter.ToInt32(wavFile, 24);
            int pos = 12;

            // Robust scan for the 'data' chunk with bounds check
            while (pos + 8 <= wavFile.Length)
            {
                string chunkId = Encoding.ASCII.GetString(wavFile, pos, 4);
                if (chunkId == "data") break;

                int chunkSize = BitConverter.ToInt32(wavFile, pos + 4);
                if (chunkSize < 0) return null; // Malformed
                pos += 8 + chunkSize;
            }

            if (pos + 8 > wavFile.Length) return null; // 'data' chunk not found

            pos += 4;
            int subChunk2Size = BitConverter.ToInt32(wavFile, pos);
            pos += 4;

            // Clamp to actual remaining bytes to prevent out-of-bounds
            int availableBytes = wavFile.Length - pos;
            if (subChunk2Size > availableBytes) subChunk2Size = availableBytes;

            int sampleCount = subChunk2Size / 2;
            float[] floatData = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short bit16 = BitConverter.ToInt16(wavFile, pos + i * 2);
                floatData[i] = bit16 / 32768f;
            }

            AudioClip clip = AudioClip.Create("AI_Response", sampleCount / channels, channels, frequency, false);
            clip.SetData(floatData, 0);
            return clip;
        }
        catch (Exception e)
        {
            Debug.LogError($"[VR] WavUtility Error: {e.Message}");
            return null;
        }
    }
}