using System;

namespace AFKLocker.Core
{
    /// <summary>Which power source a setting applies to.</summary>
    public enum PowerSource
    {
        /// <summary>Plugged in.</summary>
        AC,
        /// <summary>On battery.</summary>
        DC
    }

    /// <summary>
    /// Values of the "Lid close action" power setting, as defined by Windows.
    /// </summary>
    public enum LidAction : uint
    {
        DoNothing = 0,
        Sleep = 1,
        Hibernate = 2,
        Shutdown = 3
    }

    /// <summary>
    /// Well-known Windows power setting GUIDs.
    ///
    /// These are the documented subgroup/setting identifiers used with the
    /// powrprof.dll API. Using the API with these GUIDs avoids parsing the
    /// localized text output of powercfg.exe, which differs per Windows
    /// display language.
    /// </summary>
    public static class PowerSettingIds
    {
        /// <summary>GUID_SYSTEM_BUTTON_SUBGROUP</summary>
        public static readonly Guid ButtonsSubgroup = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");

        /// <summary>GUID_LIDCLOSE_ACTION</summary>
        public static readonly Guid LidCloseAction = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");

        /// <summary>GUID_SLEEP_SUBGROUP</summary>
        public static readonly Guid SleepSubgroup = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");

        /// <summary>GUID_STANDBY_TIMEOUT (seconds; 0 = never)</summary>
        public static readonly Guid StandbyTimeout = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

        /// <summary>GUID_HIBERNATE_TIMEOUT (seconds; 0 = never)</summary>
        public static readonly Guid HibernateTimeout = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");

        /// <summary>GUID_VIDEO_SUBGROUP</summary>
        public static readonly Guid VideoSubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");

        /// <summary>GUID_VIDEO_POWERDOWN_TIMEOUT (seconds; 0 = never)</summary>
        public static readonly Guid VideoTimeout = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    }

    /// <summary>Raised when a power configuration operation fails.</summary>
    public class PowerConfigurationException : Exception
    {
        public int ErrorCode { get; private set; }

        public PowerConfigurationException(string message, int errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public PowerConfigurationException(string message)
            : this(message, 0)
        {
        }

        /// <summary>True when Windows refused the operation for lack of privilege.</summary>
        public bool IsAccessDenied
        {
            get { return ErrorCode == 5; }
        }
    }

    /// <summary>
    /// Raised when a power setting is not present on this machine. Some settings
    /// are hidden or absent depending on hardware and OEM policy - for example a
    /// desktop PC has no lid, and hibernate settings disappear when hibernation
    /// is disabled.
    /// </summary>
    public class PowerSettingNotFoundException : PowerConfigurationException
    {
        public PowerSettingNotFoundException(string message)
            : base(message, 2)
        {
        }
    }
}
