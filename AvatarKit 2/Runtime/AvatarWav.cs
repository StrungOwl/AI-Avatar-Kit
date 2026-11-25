using System;
using System.Text;
using UnityEngine;

/// <summary>
/// WAV in, WAV out.
///
/// Unity records the mic into an AudioClip and plays from an AudioClip, but the
/// API speaks WAV bytes both directions. These two functions are the bridge.
/// Nothing clever -- but a wrong header is a classic way to lose an hour to
/// "why is my transcript empty".
/// </summary>
public static class AvatarWav
{
    // ---------------------------------------------- AudioClip -> WAV bytes

    public static byte[] FromAudioClip(AudioClip clip, int trimToSamples = -1)
    {
        if (clip == null) return null;
        int frames = clip.samples;
        if (trimToSamples > 0 && trimToSamples < frames) frames = trimToSamples;

        var samples = new float[frames * clip.channels];
        clip.GetData(samples, 0);

        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            pcm[i] = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);

        int bytes = pcm.Length * 2;
        var wav = new byte[44 + bytes];
        Ascii(wav, 0, "RIFF");
        I32(wav, 4, 36 + bytes);
        Ascii(wav, 8, "WAVE");
        Ascii(wav, 12, "fmt ");
        I32(wav, 16, 16);
        I16(wav, 20, 1);                                  // PCM
        I16(wav, 22, (short)clip.channels);
        I32(wav, 24, clip.frequency);
        I32(wav, 28, clip.frequency * clip.channels * 2); // byte rate
        I16(wav, 32, (short)(clip.channels * 2));         // block align
        I16(wav, 34, 16);                                 // bits
        Ascii(wav, 36, "data");
        I32(wav, 40, bytes);
        Buffer.BlockCopy(pcm, 0, wav, 44, bytes);
        return wav;
    }

    // ---------------------------------------------- WAV bytes -> AudioClip

    public static AudioClip ToAudioClip(byte[] wav, string name)
    {
        if (wav == null || wav.Length < 44) return null;
        if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return null;

        int channels = BitConverter.ToInt16(wav, 22);
        int rate = BitConverter.ToInt32(wav, 24);
        int bits = BitConverter.ToInt16(wav, 34);

        // Chunk order is not guaranteed: some encoders slip LIST or fact
        // chunks in before "data". Assuming data starts at byte 44 works
        // right up until it doesn't, so walk the chunks.
        int pos = 12, dataAt = -1, dataLen = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data") { dataAt = pos + 8; dataLen = size; break; }
            pos += 8 + size + (size % 2);
        }
        if (dataAt < 0) return null;
        if (dataLen <= 0 || dataAt + dataLen > wav.Length) dataLen = wav.Length - dataAt;

        float[] samples;
        if (bits == 16)
        {
            samples = new float[dataLen / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(wav, dataAt + i * 2) / 32768f;
        }
        else if (bits == 8)
        {
            samples = new float[dataLen];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (wav[dataAt + i] - 128) / 128f;
        }
        else return null;

        if (channels < 1) channels = 1;
        var clip = AudioClip.Create(name, samples.Length / channels, channels, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    static void Ascii(byte[] b, int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
    static void I32(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static void I16(byte[] b, int o, short v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
}
