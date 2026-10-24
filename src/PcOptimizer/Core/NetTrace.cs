using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace PcOptimizer.Core
{
    // Precise recording for Network Control: Windows reports every connection through ETW
    // (the Microsoft-Windows-Kernel-Network provider, the same source Resource Monitor uses),
    // so short connections and UDP traffic are not missed. Needs administrator rights; installs nothing.
    public sealed class NetTrace : IDisposable
    {
        public const string SessionName = "PcOptimizer-NetRecord";
        static readonly Guid KernelNetwork = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");
        static readonly long ProviderLo = BitConverter.ToInt64(KernelNetwork.ToByteArray(), 0);
        static readonly long ProviderHi = BitConverter.ToInt64(KernelNetwork.ToByteArray(), 8);

        readonly ConcurrentQueue<KeyValuePair<RemoteConn, PidInfo>> _queue = new ConcurrentQueue<KeyValuePair<RemoteConn, PidInfo>>();
        readonly NativeMethods.EventRecordCallback _callback;
        readonly byte[] _buf = new byte[64];
        readonly HashSet<NetEvents.Key> _seen = new HashSet<NetEvents.Key>();
        readonly Dictionary<int, PidInfo> _pids = new Dictionary<int, PidInfo>();
        readonly HashSet<int> _pidsChecked = new HashSet<int>();
        readonly ICollection<IPAddress> _local;
        volatile bool _reset;
        ulong _session, _trace;
        IntPtr _name;
        Thread? _thread;
        long _events;

        public long Events => Interlocked.Read(ref _events);

        static int _running;
        public static bool Running => Volatile.Read(ref _running) > 0;

        NetTrace()
        {
            _callback = OnEvent;
            _local = LocalAddresses();
        }

        public static NetTrace? TryStart(out uint error)
        {
            var t = new NetTrace();
            Interlocked.Increment(ref _running);
            error = t.Start();
            if (error == 0) return t;
            t.Dispose();
            return null;
        }

        // A session survives if this program crashes while recording; stop any left over from before.
        public static void StopLeftover()
        {
            try { Control(0, SessionName); }
            catch (Exception) { }
        }

        uint Start()
        {
            StopLeftover();
            ulong session = 0;
            uint r = WithProps(p => NativeMethods.StartTrace(out session, SessionName, p));
            if (r == NativeMethods.ERROR_ALREADY_EXISTS)
            {
                StopLeftover();
                r = WithProps(p => NativeMethods.StartTrace(out session, SessionName, p));
            }
            _session = session;
            if (r != 0) { _session = 0; return r; }

            var provider = KernelNetwork;
            r = NativeMethods.EnableTraceEx2(_session, ref provider, NativeMethods.EVENT_CONTROL_CODE_ENABLE_PROVIDER, 5, 0x30, 0, 0, IntPtr.Zero);
            if (r != 0) return r;

            _name = Marshal.StringToHGlobalUni(SessionName);
            var log = new NativeMethods.EVENT_TRACE_LOGFILE
            {
                LoggerName = _name,
                ProcessTraceMode = NativeMethods.PROCESS_TRACE_MODE_REAL_TIME | NativeMethods.PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
                LogfileHeader = new NativeMethods.TRACE_LOGFILE_HEADER { TimeZone = new byte[172] },
            };
            _trace = NativeMethods.OpenTrace(ref log);
            if (!ValidTrace(_trace))
            {
                _trace = 0;
                int e = Marshal.GetLastWin32Error();
                return e == 0 ? 1u : (uint)e;
            }
            ulong handle = _trace;
            _thread = new Thread(() =>
            {
                try { NativeMethods.ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception) { }
            }) { IsBackground = true, Name = "NetTrace" };
            _thread.Start();
            return 0;
        }

        static bool ValidTrace(ulong h) => h != 0 && h != ulong.MaxValue && h != 0xFFFFFFFFUL;

        static uint WithProps(Func<IntPtr, uint> use)
        {
            IntPtr props = AllocProps();
            try { return use(props); }
            finally { Marshal.FreeHGlobal(props); }
        }

        static IntPtr AllocProps()
        {
            int head = Marshal.SizeOf(typeof(NativeMethods.EVENT_TRACE_PROPERTIES));
            int size = head + 2 * 1024;
            var p = new NativeMethods.EVENT_TRACE_PROPERTIES
            {
                LogFileMode = NativeMethods.EVENT_TRACE_REAL_TIME_MODE,
                FlushTimer = 1,
                LoggerNameOffset = (uint)head,
            };
            p.Wnode.BufferSize = (uint)size;
            p.Wnode.Flags = NativeMethods.WNODE_FLAG_TRACED_GUID;
            p.Wnode.ClientContext = 1;
            IntPtr mem = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(mem, i, 0);
            Marshal.StructureToPtr(p, mem, false);
            return mem;
        }

        static void Control(ulong session, string? name) =>
            WithProps(p => NativeMethods.ControlTrace(session, session == 0 ? name : null, p, NativeMethods.EVENT_TRACE_CONTROL_STOP));

        public List<KeyValuePair<RemoteConn, PidInfo>> Drain()
        {
            var list = new List<KeyValuePair<RemoteConn, PidInfo>>();
            while (_queue.TryDequeue(out var x)) list.Add(x);
            _reset = true;
            return list;
        }

        void OnEvent(IntPtr record)
        {
            try
            {
                if (Marshal.ReadInt64(record, NativeMethods.EVENT_RECORD_ProviderId) != ProviderLo ||
                    Marshal.ReadInt64(record, NativeMethods.EVENT_RECORD_ProviderId + 8) != ProviderHi) return;
                ushort id = (ushort)Marshal.ReadInt16(record, NativeMethods.EVENT_RECORD_EventId);
                if (NetEvents.AddressLength(id) == 0) return;
                int len = (ushort)Marshal.ReadInt16(record, NativeMethods.EVENT_RECORD_UserDataLength);
                IntPtr data = Marshal.ReadIntPtr(record, NativeMethods.EVENT_RECORD_UserData);
                if (data == IntPtr.Zero) return;
                len = Math.Min(len, _buf.Length);
                Marshal.Copy(data, _buf, 0, len);
                Interlocked.Increment(ref _events);

                if (_reset) { _reset = false; _seen.Clear(); _pidsChecked.Clear(); }
                if (!NetEvents.TryKey(id, _buf, len, out var key) || !_seen.Add(key)) return;
                if (!NetEvents.TryParse(id, _buf, len, _local, out int pid, out string remote)) return;
                _queue.Enqueue(new KeyValuePair<RemoteConn, PidInfo>(new RemoteConn(pid, remote), Info(pid)));
            }
            catch (Exception) { }
        }

        // Process ids are reused, so a cached name is re-checked against the image path once per sample.
        PidInfo Info(int pid)
        {
            if (_pids.TryGetValue(pid, out var info))
            {
                if (!_pidsChecked.Add(pid)) return info;
                string? path = pid > 4 ? MemoryManager.ImagePath(pid) : null;
                if (string.Equals(path ?? "", info.Path, StringComparison.OrdinalIgnoreCase) && info.Path.Length > 0) return info;
            }
            _pidsChecked.Add(pid);
            return _pids[pid] = NetControl.Describe(pid);
        }

        static ICollection<IPAddress> LocalAddresses()
        {
            var set = new HashSet<IPAddress>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in n.GetIPProperties().UnicastAddresses) set.Add(u.Address);
            }
            catch (Exception) { }
            return set;
        }

        bool _disposed;
        static readonly List<NetTrace> Stuck = new List<NetTrace>();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Decrement(ref _running);
            if (_session != 0) { try { Control(_session, null); } catch (Exception) { } _session = 0; }
            if (_trace != 0) { try { NativeMethods.CloseTrace(_trace); } catch (Exception) { } _trace = 0; }
            // The callback must stay alive while ProcessTrace can still call it.
            if (_thread != null && !_thread.Join(2000)) lock (Stuck) Stuck.Add(this);
            _thread = null;
            if (_name != IntPtr.Zero) { Marshal.FreeHGlobal(_name); _name = IntPtr.Zero; }
        }
    }
}
