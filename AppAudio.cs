using System.Runtime.InteropServices;

namespace RepeatDesktop;

/// <summary>Levels for the mini window's music bars (Monkey picked "C1" on
/// 2026-10-08: the Stream Deck Spotify knob's bars). Ported from that plugin
/// (streamdeck/spotifyviz, SpotifyAudio + Wasapi): Windows' per-process audio
/// loopback, here pointed at this app's own process tree - WebView2's audio
/// runs in child processes of RepeatDesktop.exe - so it hears YouTube in this
/// window and nothing else on the PC. The page can't measure the sound itself:
/// it plays inside YouTube's cross-origin frame.
///
/// Same feel as the knob: 16 log-spaced bands (50 Hz - 14 kHz), auto-gain
/// against a slowly falling reference so quiet songs still fill the bars, and
/// bars that rise fast and fall slowly.</summary>
sealed class AppAudio
{
    public const int Bars = 16;
    const int Rate = 48000, N = 2048;
    readonly float[] ring = new float[N * 2];
    readonly float[] shown = new float[Bars];
    readonly Lock gate = new();
    int pos;
    long lastAudio;                   // tick of the last non-silent packet
    float refDb = -48f;
    Thread? thread;
    volatile bool running;
    /// <summary>"capturing", "starting" or the last error - sent to the page with the
    /// levels, so a capture that fails is visible instead of just flat bars.</summary>
    public volatile string Status = "off";
    long packets; float peak;
    /// <summary>Status plus what the capture has actually received.</summary>
    public string Diag => $"{Status} packets={Interlocked.Read(ref packets)} peak={peak:0.0000}";

    /// <summary>Starts capturing (idempotent). Runs only while the mini window
    /// shows the bars; Stop() ends it.</summary>
    int targetPid;      // WebView2's browser process: its tree plays the sound (our own pid only captured silence)
    public void Start(int pid)
    {
        targetPid = pid;
        running = true;
        // a Stop() moments ago leaves its thread finishing its last 100 ms wait:
        // it sees running again and simply carries on, so never start a second one
        if (thread is { IsAlive: true }) return;
        thread = new Thread(Loop) { IsBackground = true, Name = "audio-capture" };
        thread.Start();
    }

    public void Stop() => running = false;

    void Loop()
    {
        while (running)
        {
            try { Capture(targetPid); }
            catch (Exception e) { Status = $"error: {e.GetType().Name}: {e.Message}"; Thread.Sleep(2000); }   // e.g. no audio device yet: try again
        }
    }

    void Capture(int pid)
    {
        var client = Wasapi.ActivateProcessLoopback(pid);
        var fmt = Wasapi.Pcm16Stereo(Rate);
        try
        {
            const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000;
            Wasapi.Check(client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM, 200_000, 0, fmt, IntPtr.Zero), "Initialize");
            using var ev = new AutoResetEvent(false);
            Wasapi.Check(client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Wasapi.Check(client.GetService(typeof(IAudioCaptureClient).GUID, out var svc), "GetService");
            var cap = (IAudioCaptureClient)svc;
            Wasapi.Check(client.Start(), "Start");
            Status = "capturing";
            var buf = new short[0];
            while (running)
            {
                ev.WaitOne(100);
                while (cap.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    if (cap.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                    if (buf.Length < frames * 2) buf = new short[frames * 2];
                    Marshal.Copy(data, buf, 0, (int)frames * 2);
                    Interlocked.Increment(ref packets);
                    Push(buf, (int)frames, silent: (flags & 0x2) != 0);
                    cap.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        finally { Marshal.FreeHGlobal(fmt); Marshal.ReleaseComObject(client); }
    }

    void Push(short[] s, int frames, bool silent)
    {
        var any = false;
        lock (gate)
        {
            for (var i = 0; i < frames; i++)
            {
                var v = silent ? 0f : (s[2 * i] + s[2 * i + 1]) / 65536f;
                if (MathF.Abs(v) > peak) peak = MathF.Abs(v);
                if (MathF.Abs(v) > 0.0002f) any = true;   // ignore dither / LSB noise (~ -74 dBFS)
                ring[pos] = v; pos = (pos + 1) % ring.Length;
            }
            if (any) lastAudio = Environment.TickCount64;
        }
    }

    /// <summary>The next frame of bar heights, 0..1, already smoothed. All
    /// falling to 0 when nothing plays (paused, muted, between loops).</summary>
    public float[] Next()
    {
        var db = BandsDb();
        var target = new float[Bars];
        if (db is not null)
        {
            var mx = db.Max();
            refDb = Math.Max(Math.Max(mx, refDb - 0.25f), -48f);
            for (var i = 0; i < Bars; i++) target[i] = Math.Clamp((db[i] - (refDb - 42f)) / 42f, 0f, 1f);
        }
        for (var i = 0; i < Bars; i++)
            shown[i] = target[i] > shown[i] ? shown[i] + (target[i] - shown[i]) * 0.7f : shown[i] - (shown[i] - target[i]) * 0.25f;
        return (float[])shown.Clone();
    }

    /// <summary>dB per log-spaced band (50 Hz .. 14 kHz), or null when silent.</summary>
    float[]? BandsDb()
    {
        var re = new float[N]; var im = new float[N];
        lock (gate)
        {
            if (Environment.TickCount64 - lastAudio > 250) return null;
            var start = (pos - N + ring.Length) % ring.Length;
            for (var i = 0; i < N; i++) re[i] = ring[(start + i) % ring.Length];
        }
        for (var i = 0; i < N; i++) re[i] *= 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (N - 1));
        Fft(re, im);
        var outDb = new float[Bars];
        double lo = 50, hi = 14000, binHz = (double)Rate / N;
        for (var b = 0; b < Bars; b++)
        {
            double f0 = lo * Math.Pow(hi / lo, (double)b / Bars), f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / Bars);
            int k0 = Math.Max(1, (int)(f0 / binHz)), k1 = Math.Max(k0 + 1, (int)Math.Ceiling(f1 / binHz));
            float mag = 0;
            for (var k = k0; k < k1 && k < N / 2; k++) mag = Math.Max(mag, MathF.Sqrt(re[k] * re[k] + im[k] * im[k]));
            outDb[b] = 20f * MathF.Log10(mag / (N / 4f) + 1e-9f);   // 0 dB = full-scale sine
        }
        return outDb;
    }

    static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            float ang = -2 * MathF.PI / len, wr = MathF.Cos(ang), wi = MathF.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    var nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }
}

static class Wasapi
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    public static void Check(int hr, string what) { if (hr < 0) throw new COMException(what + " failed", hr); }

    public static IAudioClient ActivateProcessLoopback(int pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = 1 /* PROCESS_LOOPBACK */, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 /* include tree */ };
        var size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr pParams = Marshal.AllocHGlobal(size), pVar = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            for (var i = 0; i < 24; i += 4) Marshal.WriteInt32(pVar, i, 0);
            Marshal.WriteInt16(pVar, 0, 65);            // PROPVARIANT.vt = VT_BLOB
            Marshal.WriteInt32(pVar, 8, size);          // blob.cbSize
            Marshal.WriteIntPtr(pVar, 16, pParams);     // blob.pBlobData
            var done = new Completion();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pVar, done, out var op);
            if (!done.Signal.Wait(5000)) throw new TimeoutException("process loopback activation timed out");
            op.GetActivateResult(out var hr, out var iface);
            Check(hr, "ActivateAudioInterfaceAsync");
            return (IAudioClient)iface;
        }
        finally { Marshal.FreeHGlobal(pParams); Marshal.FreeHGlobal(pVar); }
    }

    public static IntPtr Pcm16Stereo(int rate)
    {
        var f = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(f, 0, 1); Marshal.WriteInt16(f, 2, 2); Marshal.WriteInt32(f, 4, rate);
        Marshal.WriteInt32(f, 8, rate * 4); Marshal.WriteInt16(f, 12, 4); Marshal.WriteInt16(f, 14, 16); Marshal.WriteInt16(f, 16, 0);
        return f;
    }

    sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Signal = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Signal.Set();
    }
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface); }

[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject { }

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long def, out long min);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
