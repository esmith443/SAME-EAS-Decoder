using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SameEasDecoder.Core;

namespace SameEasDecoder.Audio;

public enum SourceKind { None, Device, File }

public sealed class AudioEngine : IDisposable
{
    private readonly DecoderPipeline _pipeline;
    private readonly object _gate = new();
    private IWaveIn? _capture;
    private MMDevice? _device;
    private WaveOutEvent? _player;
    private WaveStream? _fileStream;
    private BlockingCollection<float[]>? _queue;
    private CancellationTokenSource? _cts;
    private Thread? _worker, _pump;
    private volatile bool _userStopped;

    public AudioEngine(DecoderPipeline pipeline) => _pipeline = pipeline;

    public SourceKind Kind { get; private set; }
    public string SourceName { get; private set; } = "";
    public string FormatDescription { get; private set; } = "";

    public event Action<string>? Failed;

    public event Action? SourceCompleted;

    public TimeSpan Position
    {
        get
        {
            try { return _fileStream?.CurrentTime ?? TimeSpan.Zero; } catch (Exception) { return TimeSpan.Zero; }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            try { return _fileStream?.TotalTime ?? TimeSpan.Zero; } catch (Exception) { return TimeSpan.Zero; }
        }
    }

    public void StartDevice(AudioDevice device)
    {
        Stop();
        lock (_gate)
        {
            using var enumerator = new MMDeviceEnumerator();
            var mm = enumerator.GetDevice(device.Id);
            IWaveIn capture = device.Kind == AudioDeviceKind.Loopback
                ? new WasapiLoopbackCapture(mm)
                : new WasapiCapture(mm, true, 50);

            var format = capture.WaveFormat;
            var queue = NewQueue();
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                try { queue.TryAdd(SampleConverter.ToFloat(e.Buffer, e.BytesRecorded, format)); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null && !_userStopped) Failed?.Invoke(e.Exception.Message);
            };

            _device = mm;
            _capture = capture;
            Kind = SourceKind.Device;
            SourceName = device.Name;
            FormatDescription = Describe(format.SampleRate, format.Channels, format.BitsPerSample, SampleConverter.IsFloat(format));
            _pipeline.Start(format.SampleRate, format.Channels);
            StartWorker(queue, feedSilenceWhenIdle: true, isLive: true);
            capture.StartRecording();
        }
    }

    public void StartFile(string path, bool fast)
    {
        Stop();
        lock (_gate)
        {
            WaveStream stream;
            try { stream = new AudioFileReader(path); }
            catch (Exception) { stream = new MediaFoundationReader(path); }

            ISampleProvider provider = stream as ISampleProvider ?? stream.ToSampleProvider();
            var format = provider.WaveFormat;
            var queue = NewQueue();
            _fileStream = stream;
            Kind = SourceKind.File;
            SourceName = Path.GetFileName(path);
            FormatDescription = Describe(format.SampleRate, format.Channels, stream.WaveFormat.BitsPerSample, SampleConverter.IsFloat(stream.WaveFormat));
            _pipeline.Start(format.SampleRate, format.Channels);
            StartWorker(queue, feedSilenceWhenIdle: false, isLive: false);

            if (!fast && TryStartPlayback(provider, queue)) return;
            StartPump(provider, queue, paced: !fast);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (Kind == SourceKind.None && _worker is null) return;
            _userStopped = true;
            _cts?.Cancel();

            try { _capture?.StopRecording(); } catch (Exception) { }
            try { _player?.Stop(); } catch (Exception) { }
            _queue?.CompleteAdding();
            _pump?.Join(2000);
            _worker?.Join(3000);

            _capture?.Dispose();
            _player?.Dispose();
            _fileStream?.Dispose();
            _device?.Dispose();
            _queue?.Dispose();
            _cts?.Dispose();
            _capture = null; _player = null; _fileStream = null; _device = null; _queue = null; _cts = null;
            _worker = null; _pump = null;

            _pipeline.Stop();
            Kind = SourceKind.None;
            SourceName = "";
            FormatDescription = "";
        }
    }

    private BlockingCollection<float[]> NewQueue()
    {
        _userStopped = false;
        _cts = new CancellationTokenSource();
        _queue = new BlockingCollection<float[]>(new ConcurrentQueue<float[]>(), 2000);
        return _queue;
    }

    private void StartWorker(BlockingCollection<float[]> queue, bool feedSilenceWhenIdle, bool isLive)
    {
        var worker = new Thread(() => WorkerLoop(queue, feedSilenceWhenIdle, isLive))
        {
            IsBackground = true,
            Name = "Decoder",
            Priority = ThreadPriority.AboveNormal,
        };
        _worker = worker;
        worker.Start();
    }

    private void WorkerLoop(BlockingCollection<float[]> queue, bool feedSilenceWhenIdle, bool isLive)
    {
        var idle = Stopwatch.StartNew();
        try
        {
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var block, 100))
                {
                    _pipeline.Process(block);
                    idle.Restart();
                }
                else if (feedSilenceWhenIdle && idle.ElapsedMilliseconds >= 300)
                {
                    _pipeline.ProcessSilence(TimeSpan.FromMilliseconds(idle.ElapsedMilliseconds));
                    idle.Restart();
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }

        if (!isLive && !_userStopped)
        {
            _pipeline.ProcessSilence(TimeSpan.FromSeconds(3));
            _pipeline.Stop();
            SourceCompleted?.Invoke();
        }
    }

    private bool TryStartPlayback(ISampleProvider provider, BlockingCollection<float[]> queue)
    {
        try
        {
            var tap = new TapSampleProvider(provider, block =>
            {
                try { queue.TryAdd(block); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            });
            var player = new WaveOutEvent { DesiredLatency = 150 };
            player.Init(tap);
            player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null && !_userStopped) Failed?.Invoke(e.Exception.Message);
                try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            };
            _player = player;
            player.Play();
            return true;
        }
        catch (Exception)
        {
            _player?.Dispose();
            _player = null;
            return false;
        }
    }

    private void StartPump(ISampleProvider provider, BlockingCollection<float[]> queue, bool paced)
    {
        var token = _cts!.Token;
        var pump = new Thread(() =>
        {
            int ch = provider.WaveFormat.Channels, rate = provider.WaveFormat.SampleRate;
            var buffer = new float[Math.Max(ch, rate / 50 * ch)];
            var clock = Stopwatch.StartNew();
            long frames = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = provider.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    queue.Add(buffer[..n], token);
                    frames += n / ch;
                    if (paced)
                    {
                        double ahead = frames * 1000.0 / rate - clock.Elapsed.TotalMilliseconds;
                        if (ahead > 40) Thread.Sleep((int)(ahead - 20));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        })
        {
            IsBackground = true,
            Name = "Source pump",
        };
        _pump = pump;
        pump.Start();
    }

    private static string Describe(int rate, int channels, int bits, bool isFloat) =>
        $"{rate / 1000.0:0.#} kHz · {(channels == 1 ? "mono" : channels == 2 ? "stereo" : $"{channels} ch")} · {bits}-bit{(isFloat ? " float" : "")}";

    public void Dispose() => Stop();

    private sealed class TapSampleProvider(ISampleProvider source, Action<float[]> tap) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            if (n > 0) tap(buffer.AsSpan(offset, n).ToArray());
            return n;
        }
    }
}
