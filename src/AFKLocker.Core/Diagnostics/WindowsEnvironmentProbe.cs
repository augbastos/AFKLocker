using System;
using System.Security.Principal;
using Microsoft.Win32;

namespace AFKLocker.Core.Diagnostics
{
    /// <summary>
    /// The real machine facts, read from Windows.
    ///
    /// Scope is kept deliberately narrow. This reads the OS version, the
    /// architecture, the CLR version and the elevation state - and, only when
    /// the user opts in, the manufacturer and model. It does not enumerate
    /// hardware, network adapters, installed software or processes, and it does
    /// not read the registry outside the two well-known version keys.
    /// </summary>
    public sealed class WindowsEnvironmentProbe : IEnvironmentProbe
    {
        private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

        public string OsDescription
        {
            get
            {
                string product = ReadCurrentVersion("ProductName");
                if (string.IsNullOrEmpty(product)) return Environment.OSVersion.VersionString;

                // Windows 11 still reports "Windows 10" in ProductName; the build
                // number is what actually distinguishes them.
                int build;
                if (int.TryParse(ReadCurrentVersion("CurrentBuildNumber"), out build) && build >= 22000)
                    product = product.Replace("Windows 10", "Windows 11");

                string display = ReadCurrentVersion("DisplayVersion");
                return string.IsNullOrEmpty(display) ? product : product + " " + display;
            }
        }

        public string OsBuild
        {
            get
            {
                string build = ReadCurrentVersion("CurrentBuildNumber");
                string revision = ReadCurrentVersion("UBR");
                if (string.IsNullOrEmpty(build)) return Environment.OSVersion.Version.ToString();
                return string.IsNullOrEmpty(revision) ? build : build + "." + revision;
            }
        }

        public bool Is64BitOperatingSystem
        {
            get { return Environment.Is64BitOperatingSystem; }
        }

        public bool Is64BitProcess
        {
            get { return Environment.Is64BitProcess; }
        }

        public string ClrVersion
        {
            get { return ".NET Framework CLR " + Environment.Version; }
        }

        public bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string InstallLocation
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>
        /// Manufacturer and model, read only when the caller asks for it.
        ///
        /// Read from the BIOS description key rather than WMI to keep this to a
        /// single narrow registry read. Serial numbers and UUIDs are never read:
        /// the point is which laptops work, not which laptop this is.
        /// </summary>
        public string DeviceModel
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(BiosKey, false))
                    {
                        if (key == null) return null;
                        var manufacturer = key.GetValue("SystemManufacturer") as string;
                        var product = key.GetValue("SystemProductName") as string;
                        if (string.IsNullOrEmpty(manufacturer) && string.IsNullOrEmpty(product))
                            return null;
                        return (manufacturer + " " + product).Trim();
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        private static string ReadCurrentVersion(string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey, false))
                {
                    if (key == null) return null;
                    object value = key.GetValue(valueName);
                    return value == null ? null : value.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
