using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AFKLocker.Core
{
    /// <summary>
    /// Console display state, as reported by GUID_CONSOLE_DISPLAY_STATE.
    /// </summary>
    public enum DisplayState
    {
        Off = 0,
        On = 1,
        Dimmed = 2
    }

    /// <summary>What to do about the display state that was just observed.</summary>
    public enum BlankAction
    {
        /// <summary>Leave it alone.</summary>
        Nothing,

        /// <summary>Ask Windows to turn the display off.</summary>
        TurnOff,

        /// <summary>
        /// Somebody is touching the machine right now. Wait longer before
        /// deciding anything, but keep watching: they may be about to sign in,
        /// or they may walk away again without doing so.
        /// </summary>
        Defer,

        /// <summary>
        /// Asking is not working - something on this machine is holding the
        /// display on. Keep asking, but slowly, for as long as the session stays
        /// locked. Whatever is holding it will eventually let go, and when it
        /// does the screen has to go dark rather than stay lit because a counter
        /// ran out an hour earlier.
        /// </summary>
        BackOff,

        /// <summary>
        /// Stop watching entirely. The only thing that earns this is somebody
        /// signing in: the session being unlocked is the one state in which a lit
        /// screen is correct.
        /// </summary>
        Stop
    }

    /// <summary>
    /// The last keyboard or mouse input Windows attributes to this session.
    ///
    /// Behind an interface because the whole point of reading it is to stop
    /// blanking the moment a person is back at the machine, and a test cannot
    /// produce real input.
    /// </summary>
    public interface IUserInputMonitor
    {
        uint LastInputTick { get; }
    }

    /// <inheritdoc cref="IUserInputMonitor"/>
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
    /// Decides whether AFKLocker should ask for display-off again.
    ///
    /// This exists because one request is not enough on a machine with an
    /// external monitor. Closing the lid makes Windows reconfigure the displays,
    /// and that reconfiguration lights the external panel back up - after the
    /// session is already locked, so the lock screen ends up glowing on a desk
    /// nobody is sitting at.
    ///
    /// One request is not enough for a second reason, and it is the one that
    /// took longest to find: <b>Windows ignores a display-off request while
    /// there has been recent user input.</b> It returns success and does
    /// nothing. AFKLocker asks about a second after the click that locked the
    /// machine, so the first request is almost always discarded - measured on a
    /// real machine, the same request took effect in 200ms once that machine had
    /// been idle for 94 seconds. Persistence, not a cleverer call, is the answer.
    ///
    /// So this gives up on exactly one thing: somebody signing in. Everything
    /// else that used to end it - input, the lid opening, a display that would
    /// not go dark - now only changes how long it waits. Ending early is what
    /// left a lock screen glowing all night; waiting costs nothing.
    ///
    /// The two directions are not symmetrical, and the asymmetry decides every
    /// judgement call here. A screen that stays lit too long is a nuisance. A
    /// screen that goes dark under the hands of somebody typing their password
    /// is a malfunction. So anything ambiguous waits rather than acts.
    /// </summary>
    public sealed class DisplayBlankPolicy
    {
        /// <summary>
        /// How many times it will ask before concluding that something else owns
        /// the display. One for the lock itself, one for the lid reconfiguration,
        /// and a little room for a monitor that takes two rounds to settle.
        /// </summary>
        public const int DefaultMaxRequests = 5;

        private readonly int _maxRequests;

        private uint _lastInput;
        private int _requests;
        private bool _stopped;

        public DisplayBlankPolicy(uint inputAtStart)
            : this(inputAtStart, DefaultMaxRequests)
        {
        }

        public DisplayBlankPolicy(uint inputAtStart, int maxRequests)
        {
            _lastInput = inputAtStart;
            _maxRequests = maxRequests < 1 ? 1 : maxRequests;
        }

        /// <summary>True once it has given up, for whatever reason.</summary>
        public bool Stopped
        {
            get { return _stopped; }
        }

        /// <summary>How many display-off requests it has authorised so far.</summary>
        public int Requests
        {
            get { return _requests; }
        }

        /// <summary>Why it stopped, in words a log can carry. Null while running.</summary>
        public string StopReason { get; private set; }

        /// <summary>The first request, made right after the session was locked.</summary>
        public BlankAction Begin()
        {
            if (_stopped) return BlankAction.Nothing;
            _requests++;
            return BlankAction.TurnOff;
        }

        /// <summary>
        /// Something changed the display state. Decide what that means.
        /// </summary>
        public BlankAction HandleDisplayState(DisplayState state, bool sessionLocked, uint lastInputTick)
        {
            if (_stopped) return BlankAction.Nothing;

            // Signing in is the only thing that ends this. Everything else is a
            // reason to wait, because a lock screen nobody signs into is exactly
            // the screen that must not stay lit.
            if (!sessionLocked)
                return StopBecause("the session was unlocked");

            // Input used to stop it outright, which was wrong for anything
            // longer than a few seconds: somebody who wakes the screen, looks at
            // it and walks away without signing in would leave it lit for good.
            // Waiting covers both people - the one about to type their password,
            // and the one who changed their mind.
            if (lastInputTick != _lastInput)
            {
                _lastInput = lastInputTick;
                return BlankAction.Defer;
            }

            // Off or dimmed is the goal, not a problem to solve. Reaching it
            // also clears the budget: the cap is meant to catch a display that
            // refuses to go off, not to ration a long lock during which several
            // separate things wake the screen.
            if (state != DisplayState.On)
            {
                _requests = 0;
                return BlankAction.Nothing;
            }

            // Asking repeatedly and getting nowhere means something on this
            // machine is holding the display on - a media session, a remote
            // control tool, a driver. That used to end the guard, which is the
            // same as saying "your screen stays lit tonight because it was lit
            // ten seconds after you locked it". Slow down instead, and keep
            // asking: the hold is temporary far more often than the lock is.
            if (_requests >= _maxRequests)
                return BlankAction.BackOff;

            _requests++;
            return BlankAction.TurnOff;
        }

        /// <summary>
        /// The lid moved. Opening it means somebody is probably here, which is a
        /// reason to wait rather than to stop: opening a lid and then walking
        /// away without signing in is precisely how a lock screen ends up lit for
        /// an hour. Closing it is the event this whole class exists to survive,
        /// so it changes nothing.
        /// </summary>
        public BlankAction HandleLid(LidState state)
        {
            if (_stopped) return BlankAction.Nothing;
            if (state == LidState.Opened) return BlankAction.Defer;
            return BlankAction.Nothing;
        }

        /// <summary>The session was unlocked while nothing else was happening.</summary>
        public BlankAction HandleUnlocked()
        {
            if (_stopped) return BlankAction.Nothing;
            return StopBecause("the session was unlocked");
        }

        /// <summary>The caller's time limit ran out. Windows owns the display from here.</summary>
        public BlankAction HandleTimeLimit()
        {
            if (_stopped) return BlankAction.Nothing;
            return StopBecause("the time limit was reached");
        }

        private BlankAction StopBecause(string reason)
        {
            _stopped = true;
            StopReason = reason;
            return BlankAction.Stop;
        }
    }

    /// <summary>
    /// Keeps the displays dark for a short while after AFKLocker locks the
    /// session, then gets out of the way.
    ///
    /// It owns a hidden top-level window - not a message-only one, for the same
    /// reason as <see cref="LidNotificationWindow"/> - registered for console
    /// display state and lid changes. It does not poll, holds no execution
    /// state, and never keeps the machine awake: it only reacts to what Windows
    /// reports, and every path leads to it stopping on its own.
    ///
    /// It does not run a message loop. The manual lock pumps one for it and
    /// exits when <see cref="Finished"/> fires; the watcher already has one.
    /// </summary>
    public sealed class DisplayBlanker : IDisposable
    {
        /// <summary>
        /// How long it keeps guarding the screens after a lock.
        ///
        /// This was 45 seconds, chosen to cover "lock, then close the lid". It
        /// was too short for the way people actually do it: lock, watch the lock
        /// screen appear, put things away, and close the lid a minute later. By
        /// then nothing was watching, and on a machine where Windows never
        /// darkens the lock screen on its own, the screen stayed lit until
        /// somebody came back.
        ///
        /// Ten minutes was the next attempt, and it was still a guess about how
        /// long somebody stays away. The honest limit is the lock itself: while
        /// the session is locked nobody is using this machine, and the moment it
        /// is unlocked this stops and the process exits. So the number below is
        /// not a policy, it is a backstop against a session that somehow never
        /// reports being unlocked.
        ///
        /// The program is still not resident in the sense that matters: it does
        /// not exist while you are working, only while the machine is locked,
        /// and it holds no execution state and keeps nothing awake.
        /// </summary>
        public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromHours(12);

        /// <summary>
        /// How long the first stretch after a lock stays quick to react.
        ///
        /// Closing the lid produces a burst of display reconfiguration that has
        /// to be answered within a second or two or the screen visibly stays on.
        /// Later relights are a different thing - usually a person - and get a
        /// patient response instead.
        /// </summary>
        private static readonly TimeSpan AggressiveWindow = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Windows reconfigures the displays over several messages when the lid
        /// closes. Asking during that settles nothing, so each request waits for
        /// the noise to stop.
        /// </summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(1200);

        /// <summary>
        /// The wait before darkening a screen that lit up well after the lock.
        ///
        /// Long enough for somebody who just woke the machine to see the lock
        /// screen and start typing, short enough that a screen nobody touches
        /// does not sit lit. This is the console lock display timeout Windows is
        /// supposed to provide and does not always apply.
        /// </summary>
        private static readonly TimeSpan PatientSettleDelay = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long to leave the screen alone once somebody is clearly back at
        /// the machine: the lid was opened, or a key or the mouse was used.
        ///
        /// Opening a laptop and having the screen go dark again while you are
        /// still reaching for the keyboard is worse than the problem this class
        /// exists to solve. Any further sign of a person restarts this, so it
        /// cannot expire under someone who is still there - and signing in ends
        /// the whole thing anyway.
        /// </summary>
        private static readonly TimeSpan WakeGrace = TimeSpan.FromSeconds(90);

        /// <summary>
        /// How often to ask again while the screen is still lit.
        ///
        /// This is the number that matters most, and it is set by a measured
        /// fact about Windows rather than taste: <b>Windows ignores a
        /// display-off request while there has been recent user input.</b> The
        /// request returns success and nothing happens.
        ///
        /// AFKLocker asks at the worst possible moment - roughly a second after
        /// the click that locked the machine - so the first request is almost
        /// always discarded. Measured on a real machine: with 94 seconds of
        /// idle, the same request took effect in 200 milliseconds.
        ///
        /// So the answer is not a cleverer request, it is patience. Ask again
        /// every few seconds, for as long as the session stays locked, and the
        /// first attempt after the person actually walks away is the one that
        /// lands. Asking costs a bounded message send and only happens while the
        /// screen is lit, which is precisely when it should.
        /// </summary>
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

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
        private readonly TimeSpan _timeLimit;

        /// <summary>
        /// How long to wait before checking that a request actually landed.
        ///
        /// This is not how the class works - it reacts to notifications - but a
        /// request that Windows quietly ignores produces no notification at all,
        /// and silence would otherwise look like success. Bounded by the same
        /// request cap and time limit as everything else.
        /// </summary>
        private static readonly TimeSpan VerifyInterval = TimeSpan.FromMilliseconds(2500);

        private DisplayBlankPolicy _policy;
        private NotificationWindow _window;
        private Timer _settle;
        private Timer _verify;
        private Timer _limit;
        private SessionSwitchEventHandler _sessionHandler;
        private bool _sessionLocked = true;
        private bool _running;
        private bool _finished;
        private bool _disposed;

        // What the display is actually doing, and whether that is known at all.
        //
        // Assuming "on" here is what broke this class in the field. The first
        // request usually lands instantly, so the display is already off and
        // Windows sends no state-change notification - there was no change. A
        // wrong assumption of "on" then never got corrected, the verify timer
        // spent a request against it every 2.5 seconds, and the whole budget was
        // gone about twelve seconds after locking. Anyone who closed the lid
        // promptly saw it work; anyone who paused first found nothing left
        // guarding the screens.
        //
        // Windows sends the current state as soon as the notification is
        // registered, so waiting for that costs milliseconds and removes the
        // guess entirely.
        private bool _stateKnown;
        private bool _backingOff;
        private DisplayState _lastState = DisplayState.On;
        private DateTime _startedAt = DateTime.UtcNow;

        /// <summary>Until when the screen is off-limits because somebody is here.</summary>
        private DateTime _graceUntil = DateTime.MinValue;

        public DisplayBlanker(IDisplayController displays, IUserInputMonitor input)
            : this(displays, input, DefaultTimeLimit)
        {
        }

        public DisplayBlanker(IDisplayController displays, IUserInputMonitor input, TimeSpan timeLimit)
        {
            if (displays == null) throw new ArgumentNullException("displays");
            if (input == null) throw new ArgumentNullException("input");

            _displays = displays;
            _input = input;
            _timeLimit = timeLimit;
        }

        /// <summary>Raised once, when it has stopped for good.</summary>
        public event EventHandler Finished;

        /// <summary>
        /// True once it has stopped. Needed because <see cref="Start"/> can
        /// finish synchronously, so a caller that only subscribed to
        /// <see cref="Finished"/> would wait forever for an event that already
        /// happened.
        /// </summary>
        public bool HasFinished
        {
            get { return _finished; }
        }

        /// <summary>Why it stopped. Null until it has.</summary>
        public string StopReason
        {
            get { return _policy == null ? null : _policy.StopReason; }
        }

        /// <summary>
        /// Starts watching and makes the first display-off request. Returns false
        /// if Windows would not register the notification, in which case the
        /// single request has still been made and nothing is left running.
        /// </summary>
        public bool Start()
        {
            if (_running) return true;

            _policy = new DisplayBlankPolicy(SafeLastInput());
            _running = true;
            _startedAt = DateTime.UtcNow;

            // Nobody reuses one of these today, but leaving the finished flag
            // set from a previous run would make a restarted blanker claim it
            // had already stopped - and the manual lock skips its message loop
            // on exactly that claim.
            _finished = false;
            _graceUntil = DateTime.MinValue;
            _backingOff = false;
            _stateKnown = false;

            _window = new NotificationWindow(this);
            bool registered = _window.Start();

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

            _limit = new Timer();
            _limit.Interval = (int)Math.Max(1000, _timeLimit.TotalMilliseconds);
            _limit.Tick += delegate { Apply(_policy.HandleTimeLimit()); };
            _limit.Start();

            _settle = new Timer();
            _settle.Interval = (int)SettleDelay.TotalMilliseconds;
            _settle.Tick += delegate
            {
                _settle.Stop();
                _backingOff = false;
                RequestOff();
            };

            _verify = new Timer();
            _verify.Interval = (int)VerifyInterval.TotalMilliseconds;
            _verify.Tick += delegate
            {
                // Only act on something known. Silence is not evidence: a
                // display that is already off produces no notification, and
                // treating that silence as "still lit" is exactly the bug that
                // made this give up twelve seconds after every lock.
                if (_stateKnown && _lastState == DisplayState.On)
                    Decide(DisplayState.On);
            };
            _verify.Start();

            Apply(_policy.Begin());

            if (!registered)
            {
                // Without notifications there is nothing to react to, so there is
                // no reason to keep a window and two timers alive for 45 seconds.
                Apply(BlankAction.Stop);
            }

            return registered;
        }

        /// <summary>Restarts the countdown to the next display-off request.</summary>
        private void Rearm(TimeSpan delay)
        {
            if (_settle == null) return;
            _settle.Stop();
            _settle.Interval = (int)Math.Max(1, delay.TotalMilliseconds);
            _settle.Start();
        }

        /// <summary>
        /// Quick while the lid is still being closed, patient afterwards.
        /// </summary>
        private TimeSpan CurrentSettleDelay
        {
            get
            {
                return DateTime.UtcNow - _startedAt < AggressiveWindow
                    ? SettleDelay
                    : PatientSettleDelay;
            }
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
            Decide(state);
        }

        private void Decide(DisplayState state)
        {
            if (!_running) return;

            BlankAction action = _policy.HandleDisplayState(state, _sessionLocked, SafeLastInput());

            if (action == BlankAction.TurnOff)
            {
                // Deliberately not immediate: the display coming back on is the
                // middle of a reconfiguration, not the end of one.
                _backingOff = false;
                Rearm(CurrentSettleDelay);
                return;
            }

            if (action == BlankAction.Defer)
            {
                // Somebody is at the machine. Hands off the screen entirely for
                // a while, and restart that from scratch however many times they
                // touch it.
                _backingOff = false;
                _graceUntil = DateTime.UtcNow + WakeGrace;
                Rearm(WakeGrace);
                return;
            }

            if (action == BlankAction.BackOff)
            {
                // Already waiting out a slow retry: leave the countdown alone.
                // Restarting it every time the display reports itself on is how
                // a long timer never fires at all.
                if (_backingOff) return;

                _backingOff = true;
                Rearm(RetryDelay);
                return;
            }

            Apply(action);
        }

        private void OnLid(LidState state)
        {
            if (!_running) return;
            Apply(_policy.HandleLid(state));
        }

        private void Apply(BlankAction action)
        {
            if (action == BlankAction.TurnOff)
            {
                RequestOff();
            }
            else if (action == BlankAction.Stop)
            {
                Stop();
            }
        }

        private void RequestOff()
        {
            // The one gate every path goes through. The timers are driven from
            // several places - a display event, the verify tick, a retry - and
            // any of them could otherwise darken the screen of somebody who has
            // just opened the lid. Checking here rather than at each caller is
            // what makes that impossible rather than merely unlikely.
            TimeSpan remaining = _graceUntil - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                Rearm(remaining);
                return;
            }

            try
            {
                _displays.TurnOff();
            }
            catch (Exception)
            {
                // Cosmetic by definition. A machine that locked but kept its
                // screens lit is still locked, and that is the guarantee.
            }
        }

        /// <summary>
        /// Stops watching. Safe to call more than once, and safe to call from
        /// inside the notification handler - which is where it is normally
        /// called from, since the reasons to stop arrive as messages.
        ///
        /// The order below is not cosmetic. <see cref="Finished"/> is raised
        /// before the window is torn down, and the teardown itself is pushed to
        /// the next turn of the message loop, because destroying a window while
        /// its own WndProc is on the stack is how the first version of this
        /// class hung: the teardown failed, the event never fired, and the
        /// process that was waiting for it stayed alive forever holding the
        /// display feature hostage. Nothing here may depend on the teardown
        /// succeeding.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _finished = true;

            DisposeTimer(ref _settle);
            DisposeTimer(ref _verify);
            DisposeTimer(ref _limit);

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
                // Never let cleanup be the thing that breaks the caller.
            }
            timer = null;
        }

        /// <summary>
        /// Takes the window down on the next turn of the message loop, so it is
        /// never destroyed from inside its own message handler.
        /// </summary>
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
                // The process is either exiting or the window is already gone.
                // Either way this is not worth failing over.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            // A caller disposing us is not inside our WndProc, so the window can
            // go now rather than waiting for a loop turn that may never come.
            TearDownWindow();
        }

        /// <summary>
        /// The hidden window that receives the power notifications. Kept private
        /// because nothing outside needs to know this class owns a window.
        /// </summary>
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

                // The lid is a bonus signal; the display state is the one that
                // makes this class work at all.
                return _displayRegistration != IntPtr.Zero;
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

                if (Handle != IntPtr.Zero)
                    DestroyHandle();
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
                    if (value == (int)DisplayState.Off ||
                        value == (int)DisplayState.On ||
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
