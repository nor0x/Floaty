using System.Runtime.InteropServices;
using Floaty.Services;

namespace Floaty.Platforms.Windows;

/// <summary>
/// P/Invoke surface for the transcribe.cpp C API (batch mode only). The DLL lives in the
/// downloaded runtime folder (see <see cref="NativeRuntimeService"/>), not next to the exe, so a
/// resolver loads it by absolute path; its sibling ggml DLLs resolve from the same folder via
/// altered-search-path semantics. transcribe.cpp is pre-1.0 ("ABI MAY break between 0.x minor
/// releases"), so <see cref="Initialize"/> hard-asserts the version and struct sizes against the
/// loaded library before anything else runs.
/// </summary>
internal static class TranscribeNative
{
    private const string Lib = "transcribe";

    public const int Ok = 0;
    public const int ErrBackend = 8;
    public const int ErrInputTooLong = 17;
    public const int ErrOutputTruncated = 18;

    private static IntPtr _handle;
    private static bool _initialized;
    private static readonly object InitLock = new();

    /// <summary>
    /// One-time per process: binds the resolver to the runtime folder, verifies version + ABI,
    /// and registers the ggml backend modules (a missing Vulkan driver is skipped quietly and
    /// inference falls back to CPU). Idempotent; throws if the runtime doesn't match the pin.
    /// </summary>
    public static void Initialize(string installDir)
    {
        lock (InitLock)
        {
            if (_initialized)
                return;

            var libraryPath = Path.Combine(installDir, "transcribe.dll");
            NativeLibrary.SetDllImportResolver(typeof(TranscribeNative).Assembly, (name, _, _) =>
                name == Lib
                    ? (_handle != IntPtr.Zero ? _handle : _handle = NativeLibrary.Load(libraryPath))
                    : IntPtr.Zero);

            var version = Marshal.PtrToStringUTF8(transcribe_version());
            if (version != NativeRuntimeService.Version)
                throw new InvalidOperationException(
                    $"Speech engine version mismatch: expected {NativeRuntimeService.Version}, found {version}.");
            if (transcribe_abi_struct_size(0) != (nuint)Marshal.SizeOf<ModelLoadParams>())
                throw new InvalidOperationException("Speech engine ABI mismatch (model load params).");

            Check(transcribe_init_backends(installDir), "backend initialization");
            _initialized = true;
        }
    }

    public static void Check(int status, string what)
    {
        if (status != Ok)
            throw new InvalidOperationException($"Speech engine {what} failed: {StatusString(status)}");
    }

    public static string StatusString(int status) =>
        Marshal.PtrToStringUTF8(transcribe_status_string(status)) ?? $"status {status}";

    public enum Backend : int
    {
        Auto = 0,
        Cpu = 1,
        Metal = 2,
        Vulkan = 3,
        CpuAccel = 4,
        Cuda = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModelLoadParams
    {
        public ulong StructSize;
        public Backend BackendRequest;
        public int GpuDevice;
    }

    [DllImport(Lib)] public static extern IntPtr transcribe_version();
    [DllImport(Lib)] public static extern int transcribe_init_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string artifactDir);
    [DllImport(Lib)] public static extern nuint transcribe_abi_struct_size(int which);
    [DllImport(Lib)] public static extern IntPtr transcribe_status_string(int status);
    [DllImport(Lib)] public static extern void transcribe_model_load_params_init(ref ModelLoadParams p);
    [DllImport(Lib)] public static extern int transcribe_model_load_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref ModelLoadParams p, out IntPtr model);
    [DllImport(Lib)] public static extern int transcribe_model_load_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr nullParams, out IntPtr model);
    [DllImport(Lib)] public static extern void transcribe_model_free(IntPtr model);
    [DllImport(Lib)] public static extern IntPtr transcribe_model_backend(IntPtr model);
    [DllImport(Lib)] public static extern int transcribe_session_init(IntPtr model, IntPtr nullParams, out IntPtr session);
    [DllImport(Lib)] public static extern void transcribe_session_free(IntPtr session);
    [DllImport(Lib)] public static extern int transcribe_run(IntPtr session, float[] pcm, int nSamples, IntPtr nullParams);
    [DllImport(Lib)] public static extern IntPtr transcribe_full_text(IntPtr session);
}
