using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AFKLocker.Core
{
    /// <summary>
    /// Reads and writes Windows power plan settings.
    ///
    /// This is an interface so the readiness, backup and restore logic can be
    /// unit tested without touching the machine's real power configuration.
    /// </summary>
    public interface IPowerConfiguration
    {
        /// <summary>GUID of the power scheme currently in effect.</summary>
        Guid GetActiveScheme();

        /// <summary>
        /// Human readable name of a scheme ("Balanced", "High performance", ...).
        /// Returns null when the name cannot be read.
        /// </summary>
        string GetSchemeName(Guid scheme);

        /// <summary>
        /// Reads one setting value.
        /// </summary>
        /// <exception cref="PowerSettingNotFoundException">The setting does not exist on this machine.</exception>
        /// <exception cref="PowerConfigurationException">The read failed for another reason.</exception>
        uint ReadValue(Guid scheme, Guid subgroup, Guid setting, PowerSource source);

        /// <summary>
        /// Writes one setting value. The change is not in effect until
        /// <see cref="ApplyScheme"/> is called for the same scheme.
        /// </summary>
        /// <exception cref="PowerSettingNotFoundException">The setting does not exist on this machine.</exception>
        /// <exception cref="PowerConfigurationException">The write failed, e.g. access denied.</exception>
        void WriteValue(Guid scheme, Guid subgroup, Guid setting, PowerSource source, uint value);

        /// <summary>Makes a scheme active, committing any values written to it.</summary>
        void ApplyScheme(Guid scheme);
    }

    /// <summary>
    /// <see cref="IPowerConfiguration"/> backed by the documented powrprof.dll
    /// power scheme API.
    ///
    /// Deliberately not implemented by shelling out to powercfg.exe: that tool
    /// prints localized text, so parsing it breaks on any non-English Windows.
    /// The API returns raw DWORDs and is language independent.
    /// </summary>
    public sealed class WindowsPowerConfiguration : IPowerConfiguration
    {
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_MORE_DATA = 234;

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint value);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint value);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid powerSettingGuid, uint value);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid powerSettingGuid, uint value);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
            IntPtr subGroupGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public Guid GetActiveScheme()
        {
            IntPtr pGuid = IntPtr.Zero;
            uint result = PowerGetActiveScheme(IntPtr.Zero, out pGuid);
            if (result != ERROR_SUCCESS)
                throw new PowerConfigurationException("Could not read the active power scheme.", (int)result);

            try
            {
                return (Guid)Marshal.PtrToStructure(pGuid, typeof(Guid));
            }
            finally
            {
                if (pGuid != IntPtr.Zero)
                    LocalFree(pGuid);
            }
        }

        public string GetSchemeName(Guid scheme)
        {
            uint size = 0;
            uint result = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (result != ERROR_SUCCESS && result != ERROR_MORE_DATA)
                return null;
            if (size == 0)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                result = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
                if (result != ERROR_SUCCESS)
                    return null;
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public uint ReadValue(Guid scheme, Guid subgroup, Guid setting, PowerSource source)
        {
            uint value;
            uint result = source == PowerSource.AC
                ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value)
                : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);

            if (result == ERROR_SUCCESS)
                return value;

            if (result == ERROR_FILE_NOT_FOUND)
                throw new PowerSettingNotFoundException(
                    string.Format("Power setting {0} is not present on this machine.", setting));

            throw new PowerConfigurationException(
                string.Format("Could not read power setting {0} ({1}): {2}",
                    setting, source, new Win32Exception((int)result).Message), (int)result);
        }

        public void WriteValue(Guid scheme, Guid subgroup, Guid setting, PowerSource source, uint value)
        {
            uint result = source == PowerSource.AC
                ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
                : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);

            if (result == ERROR_SUCCESS)
                return;

            if (result == ERROR_FILE_NOT_FOUND)
                throw new PowerSettingNotFoundException(
                    string.Format("Power setting {0} is not present on this machine.", setting));

            throw new PowerConfigurationException(
                string.Format("Could not write power setting {0} ({1}): {2}",
                    setting, source, new Win32Exception((int)result).Message), (int)result);
        }

        public void ApplyScheme(Guid scheme)
        {
            uint result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (result != ERROR_SUCCESS)
                throw new PowerConfigurationException(
                    string.Format("Could not activate power scheme {0}: {1}",
                        scheme, new Win32Exception((int)result).Message), (int)result);
        }
    }
}
