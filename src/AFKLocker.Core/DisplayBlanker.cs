using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AFKLocker.Core
{
    public enum DisplayState
    {
        Off = 0,
        On = 1,
        Dimmed = 2
    }

    public enum BlankAction
    {
        Nothing,
        TurnOff,
        TurnOn,
        Stop
    }

    /// <summary>
    /// The AFK display state machine. Lid-open is a temporary visible state,
    /// not the end of AFK mode: opening wakes the displays, and closing again
    /// darkens every attached display without requiring a second launch.
    /// </summary>
    public sealed class DisplayBlankPolicy
    {
        private uint _lastInput;
        private bool _lidOpen;
        private bool _hasInitialLidState;
        private bool _stopped;

        public DisplayBlankPolicy(uint inputAtStart)
        {
            _lastInput = inputAtStart;
        }

        public bool LidOpen
        {
            get { return _lidOpen; }
        }

        public bool Stopped
        {
            get { return _stopped; }
        }

        public string StopReason { get; private set; }

        public BlankAction Begin()
        {
            return _stopped ? BlankAction.Nothing : BlankAction.TurnOff;
        }

        public BlankAction HandleDisplayState(DisplayState state, bool sessionLocked)
        {
            if (_stopped) return BlankAction.Nothing;
            if (!sessionLocked) return StopBecause("the session was unlocked");
            return state == DisplayState.On && !_lidOpen
                ? BlankAction.TurnOff
                : BlankAction.Nothing;
        }

        /// <summary>
        /// A changed GetLastInputInfo tick is all AFKLocker learns about input.
        /// It never receives a key, button, position or character. Input is
        /// ignored during the short arming window so the mouse-up belonging to
        /// the activating double-click cannot immediately cancel AFK mode.
        /// </summary>
        public BlankAction HandleInput(uint lastInputTick, bool exitArmed)
        {
            if (_stopped) return BlankAction.Nothing;
            if (lastInputTick == _lastInput) return BlankAction.Nothing;

            _lastInput = lastInputTick;
            return exitArmed
                ? StopBecause("keyboard or mouse input was detected")
                : BlankAction.Nothing;
        }

        public BlankAction HandleLid(LidState state)
        {
            if (_stopped) return BlankAction.Nothing;

            // Windows immediately reports the current value when lid
            // notifications are registered. It is synchronization, not a
            // transition. In particular, an initial "open" must not undo the
            // display-off requested by Begin().
            if (!_hasInitialLidState)
            {
                _hasInitialLidState = true;
                return BlankAction.Nothing;
            }

            _lidOpen = state == LidState.Opened;
            return _lidOpen ? BlankAction.TurnOn : BlankAction.TurnOff;
        }

        public BlankAction HandleUnlocked()
        {
            return _stopped ? BlankAction.Nothing : StopBecause("the session was unlocked");
        }

        private BlankAction StopBecause(string reason)
        {
            _stopped = true;
            StopReason = reason;
            return BlankAction.Stop;
        }
    }

    public interface IUserInputMonitor
    {
        uint LastInputTick { get; }
    }

    public sealed class WindowsUserInputMonitor : IUserInputMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        public uint LastInputTick
        {
            get
            {
                var info = new LASTINPUTINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                return GetLastInputInfo(ref info) ? info.dwTime : 0;
            }
        }
    }

    /// <summary>
    /// Keeps all displays dark while AFK mode is armed and the lid is closed.
    /// A display-on notification is answered immediately. Lid close also starts
    /// a five-second burst of requests to cover the topology changes Windows
    /// produces when an external monitor is attached. Opening the lid wakes the
    /// displays; real keyboard or mouse input ends the mode.
    /// </summary>
    public sealed class DisplayBlanker : IDisposable
    {
        private static readonly TimeSpan InputArmDelay = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan LidCloseBurstInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan LidCloseBurstDuration = TimeSpan.FromSeconds(5);

        private static readonly Guid GuidConsoleDisplayState =
            new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        private static readonly Guid GuidLidSwitchStateChange =
            new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DataLengthOffset = 16;
        private const int DataOffset = 20;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient,
            ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        private readonly IDisplayController _displays;
        private readonly IUserInputMonitor _input;

        private DisplayBlankPolicy _policy;
        private NotificationWindow _window;
        private Timer _poll;
        private Timer _burst;
        private SessionSwitchEventHandler _sessionHandler;
        private DateTime _startedUtc;
        private DateTime _burstUntilUtc;
        private DisplayState _lastState = DisplayState.On;
        private bool _stateKnown;
        private bool _sessionLocked = true;
        private bool _running;
        private bool _finished;
        private bool _disposed;

        public DisplayBlanker(IDisplayController displays, IUserInputMonitor input)
        {
            if (displays == null) throw new ArgumentNullException("displays");
            if (input == null) throw new ArgumentNullException("input");
            _displays = displays;
            _input = input;
        }

        public event EventHandler Finished;

        public bool HasFinished
        {
            get { return _finished; }
        }

        public string StopReason
        {
            get { return _policy == null ? null : _policy.StopReason; }
        }

        public bool Start()
        {
            if (_running) return true;

            _policy = new DisplayBlankPolicy(SafeLastInput());
            _startedUtc = DateTime.UtcNow;
            _running = true;
            _finished = false;
            _stateKnown = false;

            _window = new NotificationWindow(this);
            if (!_window.Start())
            {
                Stop();
                return false;
            }

            _sessionHandler = delegate(object sender, SessionSwitchEventArgs e)
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock ||
                    e.Reason == SessionSwitchReason.SessionLogoff)
                {
                    _sessionLocked = false;
                    Apply(_policy.HandleUnlocked());
                }
                else if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _sessionLocked = true;
                }
            };
            SystemEvents.SessionSwitch += _sessionHandler;

            _poll = new Timer();
            _poll.Interval = (int)RetryInterval.TotalMilliseconds;
            _poll.Tick += delegate
            {
                if (!_running) return;
                if (ObserveInput()) return;
                if (!_policy.LidOpen && (!_stateKnown || _lastState == DisplayState.On))
                    RequestOff();
            };
            _poll.Start();

            _burst = new Timer();
            _burst.Interval = (int)LidCloseBurstInterval.TotalMilliseconds;
            _burst.Tick += delegate
            {
                if (!_running || _policy.LidOpen || DateTime.UtcNow >= _burstUntilUtc)
                {
                    _burst.Stop();
                    return;
                }

                if (ObserveInput()) return;
                RequestOff();
            };

            Apply(_policy.Begin());
            return true;
        }

        private bool ObserveInput()
        {
            bool armed = DateTime.UtcNow - _startedUtc >= InputArmDelay;
            BlankAction action = _policy.HandleInput(SafeLastInput(), armed);
            Apply(action);
            return action == BlankAction.Stop;
        }

        private uint SafeLastInput()
        {
            try
            {
                return _input.LastInputTick;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void OnDisplayState(DisplayState state)
        {
            if (!_running) return;
            _lastState = state;
            _stateKnown = true;
            if (state == DisplayState.On && ObserveInput()) return;
            Apply(_policy.HandleDisplayState(state, _sessionLocked));
        }

        private void OnLid(LidState state)
        {
            if (!_running) return;
            Apply(_policy.HandleLid(state));
        }

        private void Apply(BlankAction action)
        {
            switch (action)
            {
                case BlankAction.TurnOff:
                    RequestOff();
                    StartCloseBurst();
                    break;

                case BlankAction.TurnOn:
                    if (_burst != null) _burst.Stop();
                    try
                    {
                        _displays.TurnOn();
                    }
                    catch (Exception)
                    {
                    }
                    break;

                case BlankAction.Stop:
                    Stop();
                    break;
            }
        }

        private void StartCloseBurst()
        {
            if (_burst == null || _policy.LidOpen) return;
            _burstUntilUtc = DateTime.UtcNow + LidCloseBurstDuration;
            _burst.Stop();
            _burst.Start();
        }

        private void RequestOff()
        {
            if (!_running || _policy.LidOpen) return;
            try
            {
                _displays.TurnOff();
            }
            catch (Exception)
            {
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _finished = true;
            DisposeTimer(ref _poll);
            DisposeTimer(ref _burst);

            if (_sessionHandler != null)
            {
                SystemEvents.SessionSwitch -= _sessionHandler;
                _sessionHandler = null;
            }

            EventHandler handler = Finished;
            if (handler != null) handler(this, EventArgs.Empty);
            ScheduleTeardown();
        }

        private static void DisposeTimer(ref Timer timer)
        {
            if (timer == null) return;
            try
            {
                timer.Stop();
                timer.Dispose();
            }
            catch (Exception)
            {
            }
            timer = null;
        }

        private void ScheduleTeardown()
        {
            if (_window == null) return;
            var teardown = new Timer();
            teardown.Interval = 1;
            teardown.Tick += delegate
            {
                teardown.Stop();
                teardown.Dispose();
                TearDownWindow();
            };
            teardown.Start();
        }

        private void TearDownWindow()
        {
            NotificationWindow window = _window;
            _window = null;
            if (window == null) return;
            try
            {
                window.Stop();
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            TearDownWindow();
        }

        private sealed class NotificationWindow : NativeWindow
        {
            private readonly DisplayBlanker _owner;
            private IntPtr _displayRegistration = IntPtr.Zero;
            private IntPtr _lidRegistration = IntPtr.Zero;

            public NotificationWindow(DisplayBlanker owner)
            {
                _owner = owner;
            }

            public bool Start()
            {
                if (Handle == IntPtr.Zero)
                {
                    var parameters = new CreateParams
                    {
                        Caption = "AFKLocker Display",
                        X = 0,
                        Y = 0,
                        Height = 0,
                        Width = 0,
                        Style = 0,
                        ExStyle = WS_EX_TOOLWINDOW
                    };
                    CreateHandle(parameters);
                }

                Guid display = GuidConsoleDisplayState;
                _displayRegistration = RegisterPowerSettingNotification(Handle, ref display,
                    DEVICE_NOTIFY_WINDOW_HANDLE);
                Guid lid = GuidLidSwitchStateChange;
                _lidRegistration = RegisterPowerSettingNotification(Handle, ref lid,
                    DEVICE_NOTIFY_WINDOW_HANDLE);
                return _displayRegistration != IntPtr.Zero && _lidRegistration != IntPtr.Zero;
            }

            public void Stop()
            {
                if (_displayRegistration != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_displayRegistration);
                    _displayRegistration = IntPtr.Zero;
                }
                if (_lidRegistration != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_lidRegistration);
                    _lidRegistration = IntPtr.Zero;
                }
                if (Handle != IntPtr.Zero) DestroyHandle();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_POWERBROADCAST)
                {
                    if (m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE)
                        Dispatch(m.LParam);
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }

            private void Dispatch(IntPtr lParam)
            {
                if (lParam == IntPtr.Zero) return;
                var setting = (Guid)Marshal.PtrToStructure(lParam, typeof(Guid));
                int dataLength = Marshal.ReadInt32(lParam, DataLengthOffset);
                if (dataLength < sizeof(int)) return;
                int value = Marshal.ReadInt32(lParam, DataOffset);

                if (setting == GuidConsoleDisplayState)
                {
                    if (value == (int)DisplayState.Off || value == (int)DisplayState.On ||
                        value == (int)DisplayState.Dimmed)
                        _owner.OnDisplayState((DisplayState)value);
                }
                else if (setting == GuidLidSwitchStateChange)
                {
                    if (value == (int)LidState.Closed || value == (int)LidState.Opened)
                        _owner.OnLid((LidState)value);
                }
            }
        }
    }
}
