using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Interop;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// P/Invoke surface for the native demuxer, plus a resolver that pre-loads the FFmpeg shared libraries
    /// (so the wrapper's transitive deps resolve under their SONAME on every platform). Adapted from the
    /// FFmpegAudioReader template.
    /// </summary>
    internal static class NativeWrapper
    {
        internal const string LibName = "TqkLibrary.StreamRelay.Demux.FFmpeg.Native";

        /// <summary>File name of the out-of-process worker executable (sits beside the native lib).</summary>
        internal const string WorkerName = "TqkLibrary.StreamRelay.DemuxWorker";

        static IntPtr _wrapperHandle = IntPtr.Zero;
        static readonly object _loadLock = new object();

        static NativeWrapper()
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeWrapper).Assembly, Resolve);
        }

        static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, LibName, StringComparison.Ordinal))
                return IntPtr.Zero;

            if (_wrapperHandle != IntPtr.Zero)
                return _wrapperHandle;

            lock (_loadLock)
            {
                if (_wrapperHandle != IntPtr.Zero)
                    return _wrapperHandle;

                string[] dirs = GetNativeSearchDirectories();
                foreach (string name in new[] { "avutil", "swresample", "swscale", "avcodec", "avformat" })
                    TryPreload(name, dirs);

                string wrapperFile = WrapperFileName();
                foreach (string dir in dirs)
                {
                    string candidate = Path.Combine(dir, wrapperFile);
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _wrapperHandle))
                        return _wrapperHandle;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Resolve the full path of the worker executable for the current RID, or null if absent.</summary>
        internal static string? FindWorkerExecutable()
        {
            string file = OperatingSystem.IsWindows() ? WorkerName + ".exe" : WorkerName;
            foreach (string dir in GetNativeSearchDirectories())
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>The native search directories (used to preload FFmpeg next to the worker too).</summary>
        internal static string[] NativeDirectories => GetNativeSearchDirectories();

        static string WrapperFileName()
        {
            if (OperatingSystem.IsWindows())
                return LibName + ".dll";
            if (OperatingSystem.IsMacOS())
                return "lib" + LibName + ".dylib";
            return "lib" + LibName + ".so";
        }

        static void TryPreload(string name, string[] dirs)
        {
            string pattern = OperatingSystem.IsWindows()
                ? name + "-*.dll"
                : OperatingSystem.IsMacOS() ? "lib" + name + ".*dylib" : "lib" + name + ".so*";
            foreach (string dir in dirs)
            {
                string? file = Directory.EnumerateFiles(dir, pattern).FirstOrDefault();
                if (file != null && NativeLibrary.TryLoad(file, out _))
                    return;
            }
        }

        static string[] GetNativeSearchDirectories()
        {
            string rid = GetRuntimeIdentifier();
            string relNative = Path.Combine("runtimes", rid, "native");

            string?[] bases =
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(NativeWrapper).Assembly.Location),
            };

            return bases
                .Where(b => !string.IsNullOrEmpty(b))
                .SelectMany(b => new[] { b!, Path.Combine(b!, relNative) })
                .Where(Directory.Exists)
                .Distinct()
                .ToArray();
        }

        static string GetRuntimeIdentifier()
        {
            string os =
                OperatingSystem.IsWindows() ? "win" :
                OperatingSystem.IsLinux() ? "linux" :
                OperatingSystem.IsMacOS() ? "osx" : "unknown";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }

        // ---- C ABI -----------------------------------------------------------------------------------

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern IntPtr Demux_Alloc(byte[]? formatNameUtf8);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Demux_PushBytes(IntPtr demuxer, IntPtr data, int len);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Demux_SignalEof(IntPtr demuxer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Demux_Open(IntPtr demuxer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Demux_GetInit(IntPtr demuxer, out MediaInitOut init);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Demux_ReadPacket(IntPtr demuxer, out PacketOut packet);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Demux_Free(ref IntPtr ppDemuxer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Demux_GetLastError();
    }
}
