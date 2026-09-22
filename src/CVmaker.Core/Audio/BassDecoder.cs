using System.Runtime.InteropServices;
using ManagedBass;

namespace CVmaker.Core.Audio;

/// <summary>
/// Owns BASS initialisation. osu! stable plays audio through this very library (bass.dll), so
/// decoding with it gives us the exact sample timeline the game uses: MP3 encoder delay/padding is
/// removed when a LAME header is present and 529 decoder-delay samples otherwise (BASS >= 2.4.12).
/// </summary>
public static class BassRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>Initialise BASS once. Device -1 = default output, 0 = no sound (decode only).</summary>
    public static void EnsureInit(int device = -1)
    {
        lock (Gate)
        {
            if (_initialized) return;
            if (Bass.Init(device, 44100, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero))
            {
                _initialized = true;
                return;
            }
            var err = Bass.LastError;
            if (err == Errors.Already) { _initialized = true; return; }
            if (device != 0 && Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero))
            {
                _initialized = true;
                return;
            }
            throw new InvalidOperationException($"BASS initialisation failed: {err}");
        }
    }

    public static string Version
    {
        get
        {
            var v = Bass.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
    }
}

public static class BassDecoder
{
    /// <summary>Decode a whole audio file (mp3/ogg/wav/...) to float PCM.</summary>
    public static PcmAudio DecodeFile(string path)
    {
        BassRuntime.EnsureInit();
        int h = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
        if (h == 0) throw new IOException($"BASS could not open '{path}': {Bass.LastError}");
        try { return ReadAll(h); }
        finally { Bass.StreamFree(h); }
    }

    /// <summary>Decode an in-memory file image (e.g. a WAV produced by <see cref="WavWriter"/>).</summary>
    public static PcmAudio DecodeMemory(byte[] fileImage)
    {
        BassRuntime.EnsureInit();
        var handle = GCHandle.Alloc(fileImage, GCHandleType.Pinned);
        try
        {
            int h = Bass.CreateStream(handle.AddrOfPinnedObject(), 0, fileImage.LongLength, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (h == 0) throw new IOException($"BASS could not open memory stream: {Bass.LastError}");
            try { return ReadAll(h); }
            finally { Bass.StreamFree(h); }
        }
        finally { handle.Free(); }
    }

    private static PcmAudio ReadAll(int h)
    {
        var info = Bass.ChannelGetInfo(h);
        int channels = info.Channels;
        int sampleRate = info.Frequency;
        long lengthBytes = Bass.ChannelGetLength(h, PositionFlags.Bytes);
        var result = new List<float>(lengthBytes > 0 ? (int)Math.Min(int.MaxValue / 2, lengthBytes / 4 + 1024) : 1 << 20);
        var chunk = new float[sampleRate * channels]; // one second
        while (true)
        {
            int got = Bass.ChannelGetData(h, chunk, chunk.Length * sizeof(float));
            if (got <= 0) break;
            int floats = got / sizeof(float);
            for (int i = 0; i < floats; i++) result.Add(chunk[i]);
        }
        var arr = result.ToArray();
        // make sure the frame count is whole
        int rem = arr.Length % channels;
        if (rem != 0) Array.Resize(ref arr, arr.Length - rem);
        return new PcmAudio(arr, sampleRate, channels);
    }

    /// <summary>Duration in ms as BASS reports it, without decoding everything.</summary>
    public static double? GetDurationMs(string path)
    {
        BassRuntime.EnsureInit();
        int h = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Prescan);
        if (h == 0) return null;
        try
        {
            var bytes = Bass.ChannelGetLength(h, PositionFlags.Bytes);
            return Bass.ChannelBytes2Seconds(h, bytes) * 1000.0;
        }
        finally { Bass.StreamFree(h); }
    }
}
