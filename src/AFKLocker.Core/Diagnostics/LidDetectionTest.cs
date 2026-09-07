using System;

namespace AFKLocker.Core.Diagnostics
{
    /// <summary>
    /// Watches for real lid movement and reports what Windows delivered.
    ///
    /// This is the one check that hardware has to answer. Everything else can be
    /// read from the system; whether closing this particular lid produces an
    /// event can only be found out by closing it.
    ///
    /// It never locks anything. The watcher is not involved and no session
    /// locker is wired up - this listens and nothing more, so a user can run it
    /// without risking being locked out mid-test.
    /// </summary>
    public sealed class LidDetectionTest
    {
        private readonly ILidEventProvider _provider;

        private bool _started;
        private bool _registered;

        public LidDetectionTest(ILidEventProvider provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            _provider = provider;
        }

        /// <summary>True once a lid-closed event has been seen.</summary>
        public bool SawClose { get; private set; }

        /// <summary>True once a lid-opened event has been seen.</summary>
        public bool SawOpen { get; private set; }

        /// <summary>Number of lid events of any kind.</summary>
        public int EventCount { get; private set; }

        /// <summary>True when Windows accepted the registration at all.</summary>
        public bool Registered
        {
            get { return _registered; }
        }

        public event EventHandler Progress;

        /// <summary>
        /// Begins listening. Returns false when Windows refuses the
        /// registration, which is itself the answer: this machine cannot deliver
        /// lid events.
        /// </summary>
        public bool Start()
        {
            if (_started) return _registered;
            _started = true;

            _provider.LidStateChanged += OnLidStateChanged;
            _registered = _provider.Start();
            return _registered;
        }

        public void Stop()
        {
            if (!_started) return;
            _provider.LidStateChanged -= OnLidStateChanged;
            _provider.Stop();
            _started = false;
        }

        private void OnLidStateChanged(object sender, LidStateEventArgs e)
        {
            EventCount++;
            if (e.State == LidState.Closed) SawClose = true;
            else SawOpen = true;

            EventHandler handler = Progress;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public CheckOutcome Outcome
        {
            get
            {
                if (!_registered) return CheckOutcome.Fail;
                if (SawClose && SawOpen) return CheckOutcome.Pass;
                if (SawClose || SawOpen) return CheckOutcome.Warning;
                if (EventCount > 0) return CheckOutcome.Warning;
                return CheckOutcome.Fail;
            }
        }

        public string Detail
        {
            get
            {
                if (!_registered)
                    return "Windows refused to report lid state on this machine, so automatic "
                           + "lock cannot work here.";

                if (SawClose && SawOpen)
                    return "Both a close and an open event were received. Automatic lock will work "
                           + "on this machine.";

                if (SawClose)
                    return "A close event was received, but no open event. Automatic lock should "
                           + "work; the open event is not required for locking.";

                if (SawOpen)
                    return "Only an open event was received. Windows reports this lid, but the "
                           + "close was not seen - try the test again and hold the lid shut a moment.";

                return "No lid events arrived. On a laptop this usually means the ACPI Lid device "
                       + "is disabled in Device Manager; on a desktop it is expected.";
            }
        }
    }
}
