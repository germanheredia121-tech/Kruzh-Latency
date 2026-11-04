using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KruzhLatency.Infra.Benchmark;

/// <summary>Qué carga genera interrupciones de cada tipo de dispositivo (spec 003 / G6).</summary>
public enum LoadKind { Gpu, Network, Storage, Audio, UsbGuided }

/// <summary>
/// Cargas sintéticas que hacen trabajar a un dispositivo mientras se mide su driver con ETW. Ninguna toca juegos.
/// USB no se puede generar por software: el usuario mueve el mouse (lo indica la UI).
/// </summary>
public static partial class InterruptLoad
{
    /// <summary>Arranca la carga; se detiene al hacer Dispose.</summary>
    public static IDisposable Start(LoadKind kind, string toolsDir) => kind switch
    {
        LoadKind.Gpu => new GpuLoad(toolsDir),
        LoadKind.Network => new LoopLoad(PingGateway),
        LoadKind.Storage => new StorageLoad(),
        LoadKind.Audio => new AudioLoad(),
        _ => new NoLoad(),
    };

    private sealed class NoLoad : IDisposable { public void Dispose() { } }

    private sealed class GpuLoad : IDisposable
    {
        private readonly Process? _p;
        public GpuLoad(string toolsDir)
        {
            string exe = SyntheticBenchmark.LavaTriangle(toolsDir);
            if (File.Exists(exe))
            {
                _p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
            }
        }
        public void Dispose()
        {
            try { if (_p is { HasExited: false }) { _p.Kill(); _p.WaitForExit(5000); } } catch (InvalidOperationException) { }
            _p?.Dispose();
        }
    }

    /// <summary>Corre una acción en bucle en un hilo aparte hasta Dispose.</summary>
    private sealed class LoopLoad : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        public LoopLoad(Action<CancellationToken> body) => _task = Task.Run(() => body(_cts.Token));
        public void Dispose()
        {
            _cts.Cancel();
            try { _task.Wait(5000); } catch (AggregateException) { }
            _cts.Dispose();
        }
    }

    /// <summary>Ráfaga de ICMP al gateway: cada respuesta es una interrupción real de RX en la placa.</summary>
    private static void PingGateway(CancellationToken cancel)
    {
        var gateway = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (gateway is null) return;

        using var ping = new Ping();
        var payload = new byte[512];
        while (!cancel.IsCancellationRequested)
        {
            try { ping.Send(gateway, 200, payload); } catch (PingException) { Thread.Sleep(50); }
        }
    }

    /// <summary>Lecturas aleatorias SIN caché de Windows (FILE_FLAG_NO_BUFFERING): cada una llega al NVMe.</summary>
    private sealed class StorageLoad : IDisposable
    {
        private const int Block = 64 * 1024;
        private const long FileSize = 256L * 1024 * 1024;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "kruzh-io-load.bin");
        private readonly LoopLoad _loop;

        public StorageLoad()
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length < FileSize)
            {
                using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
                var chunk = new byte[1024 * 1024];
                new Random(1).NextBytes(chunk);
                for (long w = 0; w < FileSize; w += chunk.Length) fs.Write(chunk, 0, chunk.Length);
            }
            _loop = new LoopLoad(ReadLoop);
        }

        private unsafe void ReadLoop(CancellationToken cancel)
        {
            const FileOptions NoBuffering = (FileOptions)0x20000000;
            using SafeFileHandle h = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);
            void* buffer = NativeMemory.AlignedAlloc(Block, 4096); // NO_BUFFERING exige memoria alineada al sector
            try
            {
                var span = new Span<byte>(buffer, Block);
                var rnd = new Random();
                long blocks = FileSize / Block;
                while (!cancel.IsCancellationRequested) RandomAccess.Read(h, span, rnd.NextInt64(blocks) * Block);
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }

        public void Dispose()
        {
            _loop.Dispose();
            try { File.Delete(_path); } catch (IOException) { }
        }
    }

    /// <summary>Reproduce silencio en bucle: mantiene un stream de audio abierto (interrupciones del controlador HD Audio).</summary>
    private sealed partial class AudioLoad : IDisposable
    {
        private const uint SND_ASYNC = 0x1, SND_MEMORY = 0x4, SND_LOOP = 0x8, SND_NODEFAULT = 0x2;
        private readonly GCHandle _pin;

        [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        public AudioLoad()
        {
            _pin = GCHandle.Alloc(SilentWav(48000, 2), GCHandleType.Pinned);
            PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_LOOP | SND_NODEFAULT);
        }

        public void Dispose()
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            _pin.Free();
        }

        private static byte[] SilentWav(int rate, int seconds)
        {
            int data = rate * 2 * 2 * seconds; // 16 bits, estéreo
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
            return ms.ToArray();
        }
    }
}

/// <summary>Archivo .sys de un servicio de driver (para buscar su p99 en la medición ETW).</summary>
public static class DriverFiles
{
    public static string? ImageFileName(string? service)
    {
        if (string.IsNullOrEmpty(service)) return null;
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        string? image = k?.GetValue("ImagePath") as string;
        return string.IsNullOrEmpty(image) ? service + ".sys" : Path.GetFileName(image.Trim('"'));
    }
}
