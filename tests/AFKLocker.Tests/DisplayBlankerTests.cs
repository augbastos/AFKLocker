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
            Assert.Equal(1, policy.Requests, "neither should have cost a request");
            Assert.False(policy.Stopped, "and neither is a reason to stop");
        }

        [Test("keyboard or mouse input stops it, because somebody is back")]
        private static void UserInputStops()
        {
            var policy = Fresh();
            policy.Begin();

            BlankAction action = policy.HandleDisplayState(DisplayState.On, true, Idle + 1);

            Assert.Equal(BlankAction.Stop, action, "input means a person, and a person wins");
            Assert.True(policy.Stopped, "and it stays stopped");
            Assert.Equal("the keyboard or mouse was used", policy.StopReason, "the reason should say so");
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

        [Test("opening the lid stops it")]
        private static void LidOpenedStops()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Stop, policy.HandleLid(LidState.Opened),
                "on a laptop, opening the lid is a person arriving");
            Assert.Equal("the lid was opened", policy.StopReason, "the reason should say so");
        }

        [Test("closing the lid changes nothing, because that is the case it exists for")]
        private static void LidClosedKeepsGoing()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.Nothing, policy.HandleLid(LidState.Closed), "closing is not arriving");
            Assert.False(policy.Stopped, "and it must still be watching when the wake-up comes");
        }

        [Test("it gives up rather than fight forever for the display")]
        private static void RequestCapStops()
        {
            var policy = new DisplayBlankPolicy(Idle, 3);

            Assert.Equal(BlankAction.TurnOff, policy.Begin(), "request 1");
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle), "request 2");
            Assert.Equal(BlankAction.TurnOff, policy.HandleDisplayState(DisplayState.On, true, Idle), "request 3");

            Assert.Equal(BlankAction.Stop, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "something else owns this display, and losing quietly beats looping");
            Assert.Equal(3, policy.Requests, "the cap must be a real ceiling");
            Assert.Equal("something else keeps the display on", policy.StopReason, "the reason should say so");
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
            policy.HandleLid(LidState.Opened);

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
            Assert.Equal(BlankAction.Stop, policy.HandleDisplayState(DisplayState.On, true, Idle),
                "and the second must not");
        }
    }
}
