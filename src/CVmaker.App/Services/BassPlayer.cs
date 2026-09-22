using System.IO;
using System.Runtime.InteropServices;
using CVmaker.Core.Audio;
using ManagedBass;

namespace CVmaker.App.Services;

/// <summary>
/// Playback through BASS (the same library osu! uses). Supports a file on disk or an in-memory WAV
/// image (used for previewing the rendered cut), seeking, and an A-B loop implemented with a
/// sample-accurate position sync.
/// </summary>
public sealed class BassPlayer : IDisposable
{
    private int _stream;
    private GCHandle _pinned;
    private int _loopSync;
    private int _endSync;
    private readonly SyncProcedure _loopProc;
    private readonly SyncProcedure _endProc;
    private double _loopStartMs;

    public event Action? PlaybackEnded;

    public BassPlayer()
    {
        BassRuntime.EnsureInit(-1);
        _loopProc = (_, _, _, _) => Seek(_loopStartMs);
        _endProc = (_, _, _, _) => PlaybackEnded?.Invoke();
    }

    public bool HasStream => _stream != 0;
    public double DurationMs { get; private set; }
    public int SampleRate { get; private set; }

    public bool IsPlaying => _stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing;

    public double PositionMs
    {
        get
        {
            if (_stream == 0) return 0;
            var bytes = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
            return Bass.ChannelBytes2Seconds(_stream, bytes) * 1000.0;
        }
    }

    public float Volume
    {
        set { if (_stream != 0) Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, Math.Clamp(value, 0f, 1f)); }
    }

    public void LoadFile(string path)
    {
        Free();
        _stream = Bass.CreateStream(path, 0, 0, BassFlags.Float | BassFlags.Prescan);
        if (_stream == 0) throw new IOException($"BASS could not open '{path}': {Bass.LastError}");
        AfterLoad();
    }

    /// <summary>Load a complete file image (e.g. WAV bytes) kept in memory.</summary>
    public void LoadMemory(byte[] fileImage)
    {
        Free();
        _pinned = GCHandle.Alloc(fileImage, GCHandleType.Pinned);
        _stream = Bass.CreateStream(_pinned.AddrOfPinnedObject(), 0, fileImage.LongLength, BassFlags.Float);
        if (_stream == 0)
        {
            _pinned.Free();
            throw new IOException($"BASS could not open memory stream: {Bass.LastError}");
        }
        AfterLoad();
    }

    public void LoadPcm(PcmAudio pcm) => LoadMemory(WavWriter.ToFloatWav(pcm));

    private void AfterLoad()
    {
        var info = Bass.ChannelGetInfo(_stream);
        SampleRate = info.Frequency;
        var len = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
        DurationMs = Bass.ChannelBytes2Seconds(_stream, len) * 1000.0;
        _endSync = Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endProc);
    }

    public void Play()
    {
        if (_stream == 0) return;
        Bass.ChannelPlay(_stream, false);
    }

    public void Pause()
    {
        if (_stream == 0) return;
        Bass.ChannelPause(_stream);
    }

    public void Stop()
    {
        if (_stream == 0) return;
        Bass.ChannelStop(_stream);
        Seek(0);
    }

    public void Seek(double ms)
    {
        if (_stream == 0) return;
        ms = Math.Clamp(ms, 0, Math.Max(0, DurationMs));
        var bytes = Bass.ChannelSeconds2Bytes(_stream, ms / 1000.0);
        Bass.ChannelSetPosition(_stream, bytes, PositionFlags.Bytes);
    }

    /// <summary>Loop [start, end) until <see cref="ClearLoop"/> is called.</summary>
    public void SetLoop(double startMs, double endMs)
    {
        ClearLoop();
        if (_stream == 0 || endMs <= startMs) return;
        _loopStartMs = startMs;
        var endBytes = Bass.ChannelSeconds2Bytes(_stream, endMs / 1000.0);
        _loopSync = Bass.ChannelSetSync(_stream, SyncFlags.Position | SyncFlags.Mixtime, endBytes, _loopProc);
    }

    public void ClearLoop()
    {
        if (_stream != 0 && _loopSync != 0) Bass.ChannelRemoveSync(_stream, _loopSync);
        _loopSync = 0;
    }

    public void Free()
    {
        if (_stream != 0)
        {
            Bass.ChannelStop(_stream);
            Bass.StreamFree(_stream);
            _stream = 0;
        }
        _loopSync = 0;
        _endSync = 0;
        if (_pinned.IsAllocated) _pinned.Free();
        DurationMs = 0;
    }

    public void Dispose() => Free();
}
