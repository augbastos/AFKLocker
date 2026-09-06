using System;
using System.Runtime.InteropServices;

namespace AFKLocker.Core
{
    /// <summary>
    /// What this machine can do with sleep, hibernation and lids. Read from the
    /// documented CallNtPowerInformation(SystemPowerCapabilities) API rather
    /// than from the localized text of "powercfg /a".
    /// </summary>
    public sealed class SystemCapabilities
    {
        public bool LidPresent { get; set; }
        public bool SupportsStandbyS3 { get; set; }
        public bool HibernateFilePresent { get; set; }

        /// <summary>
        /// True on "Modern Standby" (S0 low power idle) machines. These behave
        /// differently: the system can keep entering a low power state even when
        /// classic sleep timeouts are set to Never.
        /// </summary>
        public bool ModernStandby { get; set; }

        /// <summary>True when the machine reports at least one battery.</summary>
        public bool BatteryPresent { get; set; }

        public static SystemCapabilities Read()
        {
            return Read(new WindowsPowerInformation());
        }

        public static SystemCapabilities Read(IPowerInformation powerInformation)
        {
            if (powerInformation == null) throw new ArgumentNullException("powerInformation");
            return powerInformation.GetCapabilities();
        }
    }

    /// <summary>Abstraction over the system power capability query, for testing.</summary>
    public interface IPowerInformation
    {
        SystemCapabilities GetCapabilities();
    }

    /// <summary>Reads system power capabilities through powrprof.dll.</summary>
    public sealed class WindowsPowerInformation : IPowerInformation
    {
        private const int SystemPowerCapabilities = 4;
        private const int STATUS_SUCCESS = 0;

        [DllImport("powrprof.dll")]
        private static extern int CallNtPowerInformation(int informationLevel, IntPtr inputBuffer,
            uint inputBufferLength, IntPtr outputBuffer, uint outputBufferLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct BatteryReportingScale
        {
            public uint Granularity;
            public uint Capacity;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerCapabilitiesStruct
        {
            [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent;
            [MarshalAs(UnmanagedType.U1)] public bool SleepButtonPresent;
            [MarshalAs(UnmanagedType.U1)] public bool LidPresent;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS1;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS2;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS3;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS4;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS5;
            [MarshalAs(UnmanagedType.U1)] public bool HiberFilePresent;
            [MarshalAs(UnmanagedType.U1)] public bool FullWake;
            [MarshalAs(UnmanagedType.U1)] public bool VideoDimPresent;
            [MarshalAs(UnmanagedType.U1)] public bool ApmPresent;
            [MarshalAs(UnmanagedType.U1)] public bool UpsPresent;
            [MarshalAs(UnmanagedType.U1)] public bool ThermalControl;
            [MarshalAs(UnmanagedType.U1)] public bool ProcessorThrottle;
            public byte ProcessorMinThrottle;
            public byte ProcessorMaxThrottle;
            [MarshalAs(UnmanagedType.U1)] public bool FastSystemS4;
            [MarshalAs(UnmanagedType.U1)] public bool Hiberboot;
            [MarshalAs(UnmanagedType.U1)] public bool WakeAlarmPresent;
            [MarshalAs(UnmanagedType.U1)] public bool AoAc;
            [MarshalAs(UnmanagedType.U1)] public bool DiskSpinDown;
            public byte HiberFileType;
            [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
            [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent;
            [MarshalAs(UnmanagedType.U1)] public bool BatteriesAreShortTerm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public BatteryReportingScale[] BatteryScale;
            public int AcOnLineWake;
            public int SoftLidWake;
            public int RtcWake;
            public int MinDeviceWakeState;
            public int DefaultLowLatencyWake;
        }

        public SystemCapabilities GetCapabilities()
        {
            int size = Marshal.SizeOf(typeof(SystemPowerCapabilitiesStruct));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++)
                    Marshal.WriteByte(buffer, i, 0);

                int status = CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, buffer, (uint)size);
                if (status != STATUS_SUCCESS)
                    throw new PowerConfigurationException(
                        string.Format("Could not read system power capabilities (status 0x{0:X8}).", status), status);

                var caps = (SystemPowerCapabilitiesStruct)Marshal.PtrToStructure(
                    buffer, typeof(SystemPowerCapabilitiesStruct));

                return new SystemCapabilities
                {
                    LidPresent = caps.LidPresent,
                    SupportsStandbyS3 = caps.SystemS3,
                    HibernateFilePresent = caps.HiberFilePresent,
                    ModernStandby = caps.AoAc,
                    BatteryPresent = caps.SystemBatteriesPresent
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
