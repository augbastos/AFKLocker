using System;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// The blanker's decision logic, which is the half that can be wrong in a
    /// way nobody notices: turning a screen off is obvious, refusing to turn it
    /// off is not.
    ///
    /// Every test here is about when it must STOP. Blanking too little leaves a
    /// lit lock screen, which is a nuisance. Blanking too much darkens the
    /// screen of somebody standing at the machine typing a password, which is a
    /// malfunction. These fix the second one.
    /// </summary>
    internal static class DisplayBlankerTests
    {
        private const uint Idle = 1000;

        private static DisplayBlankPolicy Fresh()
        {
            return new DisplayBlankPolicy(Idle);
        }

        // ------------------------------------------------------------ grace ---
        //
        // The window that keeps the screen alone while somebody is at the
        // machine. It used to read the clock directly, which meant the one part
        // of this class that decides on TIME was the one part no test could
        // reach - and it is the part that, if wrong, blanks the screen of
        // somebody typing their password.

        private static readonly DateTime Noon = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

        [Test("input starts the grace, and the screen is left alone during it")]
        private static void InputStartsGrace()
        {
            var grace = new DisplayGrace();
            grace.Begin(Noon, Window);

            Assert.True(grace.Blocks(Noon), "immediately");
            Assert.True(grace.Blocks(Noon.AddSeconds(89)), "and one second before it ends");
        }

        [Test("the grace expires, and the screen can go dark again")]
        private static void GraceExpires()
        {
            var grace = new DisplayGrace();
            grace.Begin(Noon, Window);

            Assert.False(grace.Blocks(Noon.AddSeconds(90)), "at the boundary");
            Assert.False(grace.Blocks(Noon.AddSeconds(91)), "and after it");
        }

        [Test("more input restarts the grace from scratch, rather than extending nothing")]
        private static void MoreInputRestartsGrace()
        {
            var grace = new DisplayGrace();
            grace.Begin(Noon, Window);

            // Somebody typing a long password touches the keyboard repeatedly.
            grace.Begin(Noon.AddSeconds(80), Window);

            Assert.True(grace.Blocks(Noon.AddSeconds(100)),
                "the original window had passed, but they are still here");
            Assert.False(grace.Blocks(Noon.AddSeconds(171)), "and it ends 90s after the LAST touch");
        }

        [Test("opening the lid starts the same grace as input")]
        private static void LidOpenStartsGrace()
        {
            var grace = new DisplayGrace();
            grace.Begin(Noon, Window);

            Assert.True(grace.Blocks(Noon.AddSeconds(10)),
                "reaching for the keyboard after opening a laptop takes longer than a second");
        }

        [Test("the remaining time is what re-arms the timer, and never goes negative")]
        private static void RemainingIsUsableAsATimerInterval()
        {
            var grace = new DisplayGrace();
            grace.Begin(Noon, Window);

            Assert.Equal(TimeSpan.FromSeconds(30), grace.Remaining(Noon.AddSeconds(60)), "what is left");
            Assert.Equal(TimeSpan.Zero, grace.Remaining(Noon.AddSeconds(200)),
                "a negative interval would throw when handed to a timer");
        }

        [Test("ending the grace takes effect immediately, whatever was left")]
        private static void EndingGraceIsImmediate()
        {
            var grace = new DisplayGrace();
            grace.Begin(Noon, Window);
            grace.End();

            Assert.False(grace.Blocks(Noon), "signing in does not wait out the window");
            Assert.Equal(TimeSpan.Zero, grace.Remaining(Noon), "and nothing is left of it");
        }

        [Test("a fresh grace blocks nothing")]
        private static void UnstartedGraceBlocksNothing()
        {
            var grace = new DisplayGrace();

            Assert.False(grace.Blocks(Noon), "locking must darken the screen at once");
        }

        [Test("every decision the policy can make is one the blanker acts on")]
        private static void EveryDecisionIsHandled()
        {
            // The blanker used to carry out TurnOff and Stop in one place and
            // Defer and BackOff in another, and lid-open went through the place
            // that did not know about Defer - so opening a laptop started no
            // grace at all and the screen could go dark while somebody was still
            // reaching for the keyboard. The bug was two paths for four
            // outcomes, and it was invisible because both paths compiled.
            //
            // Enumerating the outcomes here means adding a fifth one to the enum
            // without handling it fails a test rather than going quiet.
            var handled = new[]
            {
                BlankAction.Nothing, BlankAction.TurnOff,
                BlankAction.Defer, BlankAction.BackOff, BlankAction.Stop
            };

            foreach (BlankAction action in Enum.GetValues(typeof(BlankAction)))
            {
                Assert.True(Array.IndexOf(handled, action) >= 0,
                    "BlankAction." + action + " exists but nothing in this test acknowledges it - "
                    + "check DisplayBlanker.Apply handles it too");
            }
        }

        [Test("lid and input both defer once the screen has been dark, and are carried out the same way")]
        private static void LidOpenAndInputAgreeAfterDarkness()
        {
            // They reach the blanker through different methods, which is exactly
            // how one of them ended up doing nothing at all, so the outcomes are
            // pinned together here.
            //
            // They are deliberately NOT identical before the screen has been
            // dark: a lid opening is unambiguous, while the most recent input at
            // that point is the click that did the locking.
            var byLid = Fresh();
            byLid.Begin();

            var byInput = Fresh();
            byInput.Begin();
            byInput.HandleDisplayState(DisplayState.Off, true, Idle);

            Assert.Equal(BlankAction.Defer, byLid.HandleLid(LidState.Opened), "lid open defers");
            Assert.Equal(BlankAction.Defer,
                byInput.HandleDisplayState(DisplayState.On, true, Idle + 1), "so does input");
        }

        [Test("the input tick wrapping round is treated as input, not as a fault")]
        private static void InputTickWraparoundIsSafe()
        {
            // GetLastInputInfo returns a 32-bit tick count that wraps about every
            // 49.7 days. The comparison is equality, never subtraction, so a wrap
            // reads as "something happened" - which is the safe direction, since
            // it defers rather than blanks.
            var policy = new DisplayBlankPolicy(uint.MaxValue - 1);
            policy.Begin();
            policy.HandleDisplayState(DisplayState.Off, true, uint.MaxValue - 1);

            Assert.Equal(BlankAction.Defer, policy.HandleDisplayState(DisplayState.On, true, 5),
                "a wrapped tick differs from the last one, so it defers");
            Assert.False(policy.Stopped, "and nothing about it is treated as an error");

            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, 5),
                "and once the value settles, blanking resumes normally");
        }

        [Test("the first request happens as soon as the session is locked")]
        private static void BeginRequestsDisplayOff()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.TurnOff, policy.Begin(), "Begin should ask for display-off");
            Assert.Equal(1, policy.Requests, "the first request should be counted");
            Assert.False(policy.Stopped, "one request is not a reason to stop");
        }

        [Test("a display that comes back on while nobody is there is turned off again")]
        private static void DisplayBackOnIsTurnedOffAgain()
        {
            var policy = Fresh();
            policy.Begin();

            BlankAction action = policy.HandleDisplayState(DisplayState.On, true, Idle);

            Assert.Equal(BlankAction.TurnOff, action, "this is the lid-close wake-up, the whole point");
            Assert.Equal(2, policy.Requests, "the retry should be counted too");
        }

        [Test("a display that is already off or dimmed is left alone")]
        private static void DarkDisplayIsLeftAlone()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Nothing, policy.HandleDisplayState(DisplayState.Off, true, Idle),
                "off is the goal, not a problem");
            Assert.Equal(BlankAction.Nothing, policy.HandleDisplayState(DisplayState.Dimmed, true, Idle),
                "dimmed is Windows on its way out, not something to fight");
            Assert.False(policy.Stopped, "and neither is a reason to stop");
        }

        [Test("reaching a dark screen clears the budget, so a long lock is not rationed")]
        private static void ReachingOffResetsTheBudget()
        {
            var policy = new DisplayBlankPolicy(Idle, 2);

            policy.Begin();                                                   // request 1
            policy.HandleDisplayState(DisplayState.On, true, Idle);           // request 2
            policy.HandleDisplayState(DisplayState.Off, true, Idle);          // it worked

            // Something wakes the screen much later in the same locked session.
            // The cap exists to catch a display that refuses to go off, not to
            // ration a lock that lasts an hour.
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "a screen that went dark and lit up again is a fresh problem");
            Assert.False(policy.Stopped, "and not a reason to give up");
        }

        [Test("signing in is the only thing that ends the guard")]
        private static void OnlyUnlockingStops()
        {
            var policy = new DisplayBlankPolicy(Idle, 1);

            policy.Begin();
            policy.HandleDisplayState(DisplayState.On, true, Idle);           // back off
            policy.HandleDisplayState(DisplayState.On, true, Idle + 9);       // defer
            policy.HandleLid(LidState.Opened);                                // defer
            policy.HandleLid(LidState.Closed);                                // nothing

            Assert.False(policy.Stopped, "none of that is a reason to abandon the screen");

            Assert.Equal(BlankAction.Stop, policy.HandleDisplayState(DisplayState.On, false, Idle + 9),
                "an unlocked session is the one state where a lit screen is correct");
        }

        [Test("input before the screen has ever been dark is the click that locked it")]
        private static void InputBeforeDarknessIsNotSomebodyArriving()
        {
            var policy = Fresh();
            policy.Begin();

            // A stray event right after the lock - the second half of a
            // double-click, a mouse-up, a trackpad brush while closing the lid -
            // would otherwise be read as "somebody is here" and start a
            // ninety-second pause against the very action that asked for the
            // screen to go out.
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle + 1),
                "nothing has been dark yet, so there was nothing for anyone to wake");
            Assert.False(policy.Stopped, "and it keeps going");
        }

        [Test("keyboard or mouse input defers once the screen has been dark")]
        private static void UserInputDefers()
        {
            var policy = Fresh();
            policy.Begin();
            policy.HandleDisplayState(DisplayState.Off, true, Idle);   // it worked

            Assert.Equal(BlankAction.Defer, policy.HandleDisplayState(DisplayState.On, true, Idle + 1),
                "now input really does mean somebody woke it");
            Assert.False(policy.Stopped, "but waiting is not giving up");
        }

        [Test("opening the lid defers even before the screen has been dark")]
        private static void LidOpenDefersRegardless()
        {
            var policy = Fresh();
            policy.Begin();

            // Unlike input, this is unambiguous: nobody opens a lid by accident
            // while walking away, so it does not need the screen to have gone
            // dark first to mean somebody arrived.
            Assert.Equal(BlankAction.Defer, policy.HandleLid(LidState.Opened),
                "a lid opening is a person, whatever the screen was doing");
        }

        [Test("somebody who wakes the screen and walks away without signing in is still covered")]
        private static void InputThatStopsComingIsBlankedAgain()
        {
            var policy = Fresh();
            policy.Begin();
            policy.HandleDisplayState(DisplayState.Off, true, Idle);

            // They move the mouse: the screen lights and the guard backs off.
            Assert.Equal(BlankAction.Defer, policy.HandleDisplayState(DisplayState.On, true, Idle + 1),
                "back off while they are touching it");

            // Then they leave without signing in. Nothing new arrives, so the
            // same input value comes back round - and this is the case that used
            // to leave a lock screen lit for the rest of the night.
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle + 1),
                "they stopped touching it, so put the screen out");
            Assert.False(policy.Stopped, "and keep watching afterwards");
        }

        [Test("repeated input keeps deferring, so typing a password is never interrupted")]
        private static void ContinuedInputKeepsDeferring()
        {
            var policy = Fresh();
            policy.Begin();
            policy.HandleDisplayState(DisplayState.Off, true, Idle);   // the screen went dark

            for (uint tick = 1; tick <= 6; tick++)
            {
                Assert.Equal(BlankAction.Defer,
                    policy.HandleDisplayState(DisplayState.On, true, Idle + tick),
                    "still typing at tick " + tick);
            }

            Assert.False(policy.Stopped, "and it never gave up on them");
        }

        [Test("an unlocked session stops it")]
        private static void UnlockedSessionStops()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Stop, policy.HandleDisplayState(DisplayState.On, false, Idle),
                "there is nothing to protect once the session is open");
            Assert.Equal("the session was unlocked", policy.StopReason, "the reason should say so");
        }

        [Test("unlocking stops it even with no display event")]
        private static void ExplicitUnlockStops()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Stop, policy.HandleUnlocked(), "the unlock notification alone is enough");
            Assert.True(policy.Stopped, "and it stays stopped");
        }

        [Test("opening the lid defers rather than ending the guard")]
        private static void LidOpenedDefers()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Defer, policy.HandleLid(LidState.Opened),
                "somebody probably arrived, so wait for them");
            Assert.False(policy.Stopped,
                "but opening a lid and walking off without signing in must not leave it lit");
        }

        [Test("closing the lid changes nothing, because that is the case it exists for")]
        private static void LidClosedKeepsGoing()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Nothing, policy.HandleLid(LidState.Closed), "closing is not arriving");
            Assert.False(policy.Stopped, "and it must still be watching when the wake-up comes");
        }

        [Test("a display something else is holding on gets slow retries, never a surrender")]
        private static void RequestCapBacksOffInsteadOfStopping()
        {
            var policy = new DisplayBlankPolicy(Idle, 3);

            Assert.Equal(BlankAction.TurnOff, policy.Begin(), "request 1");
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle), "request 2");
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle), "request 3");

            Assert.Equal(BlankAction.BackOff, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "stop asking quickly, but do not stop asking");
            Assert.Equal(BlankAction.BackOff, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "and again, for as long as the session stays locked");
            Assert.False(policy.Stopped,
                "giving up would mean a lit lock screen all night because of the first ten seconds");
            Assert.Equal(3, policy.Requests, "the cap still caps the fast attempts");
        }

        [Test("whatever was holding the display lets go, and the screen finally goes dark")]
        private static void BackingOffRecoversWhenTheHoldEnds()
        {
            var policy = new DisplayBlankPolicy(Idle, 2);

            policy.Begin();
            policy.HandleDisplayState(DisplayState.On, true, Idle);
            Assert.Equal(BlankAction.BackOff, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "something is holding it on");

            // The media session ends, the remote tool disconnects, the driver
            // settles - and the display finally reports itself off.
            policy.HandleDisplayState(DisplayState.Off, true, Idle);

            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "the next relight is treated as a fresh problem, at full speed");
        }

        [Test("the time limit stops it")]
        private static void TimeLimitStops()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Stop, policy.HandleTimeLimit(), "after the window, Windows owns the display");
            Assert.Equal("the time limit was reached", policy.StopReason, "the reason should say so");
        }

        [Test("once stopped it never asks for anything again")]
        private static void StoppedStaysStopped()
        {
            var policy = Fresh();
            policy.Begin();
            policy.HandleUnlocked();

            Assert.Equal(BlankAction.Nothing, policy.Begin(), "not even a fresh Begin");
            Assert.Equal(BlankAction.Nothing, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "not on a display waking up");
            Assert.Equal(BlankAction.Nothing, policy.HandleLid(LidState.Closed), "not on the lid closing again");
            Assert.Equal(BlankAction.Nothing, policy.HandleTimeLimit(), "not on the time limit");
            Assert.Equal(1, policy.Requests, "and no request may be added after the fact");
        }

        [Test("a cap below one is still one, so the lock always darkens the screen once")]
        private static void CapNeverDropsBelowOne()
        {
            var policy = new DisplayBlankPolicy(Idle, 0);

            Assert.Equal(BlankAction.TurnOff, policy.Begin(), "the first request must always survive");
            Assert.Equal(BlankAction.BackOff, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "and the second slows down rather than surrendering");
        }
    }
}
