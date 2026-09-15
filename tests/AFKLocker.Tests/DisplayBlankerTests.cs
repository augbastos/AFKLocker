using System;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class DisplayBlankerTests
    {
        private const uint StartInput = 1000;

        private static DisplayBlankPolicy Fresh()
        {
            return new DisplayBlankPolicy(StartInput);
        }

        private static DisplayBlankPolicy FreshWithClosedLidReading()
        {
            var policy = Fresh();
            policy.HandleLid(LidState.Closed);
            return policy;
        }

        [Test("AFK activation immediately asks every display to turn off")]
        private static void BeginTurnsOff()
        {
            Assert.Equal(BlankAction.TurnOff, Fresh().Begin(), "activation should darken immediately");
        }

        [Test("a display that wakes while the lid is closed is turned off immediately")]
        private static void RelightWhileClosedTurnsOff()
        {
            var policy = Fresh();
            policy.Begin();

            Assert.Equal(BlankAction.TurnOff,
                policy.HandleDisplayState(DisplayState.On, true),
                "external-monitor topology changes must be answered");
        }

        [Test("an off or dimming display is left alone")]
        private static void DarkStatesAreLeftAlone()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.Nothing,
                policy.HandleDisplayState(DisplayState.Off, true), "off is the target");
            Assert.Equal(BlankAction.Nothing,
                policy.HandleDisplayState(DisplayState.Dimmed, true), "dimming is on the way there");
        }

        [Test("opening the lid wakes the displays without ending AFK mode")]
        private static void LidOpenTurnsOn()
        {
            var policy = FreshWithClosedLidReading();

            Assert.Equal(BlankAction.TurnOn, policy.HandleLid(LidState.Opened),
                "the screen should wake from the lid alone");
            Assert.True(policy.LidOpen, "the visible lid-open state should be remembered");
            Assert.False(policy.Stopped, "opening is not the same as returning to work");
        }

        [Test("a lit display is allowed while the lid is open")]
        private static void LidOpenAllowsDisplayOn()
        {
            var policy = FreshWithClosedLidReading();
            policy.HandleLid(LidState.Opened);

            Assert.Equal(BlankAction.Nothing,
                policy.HandleDisplayState(DisplayState.On, true),
                "AFKLocker must not fight the lid-open screen");
        }

        [Test("closing the lid again returns to the dark AFK state")]
        private static void RecloseTurnsOffAgain()
        {
            var policy = FreshWithClosedLidReading();
            policy.HandleLid(LidState.Opened);

            Assert.Equal(BlankAction.TurnOff, policy.HandleLid(LidState.Closed),
                "reclose should need no second launch or input");
            Assert.False(policy.LidOpen, "closed state should be remembered");
            Assert.Equal(BlankAction.TurnOff,
                policy.HandleDisplayState(DisplayState.On, true),
                "an external monitor relight after reclose is rejected");
        }

        [Test("the initial open-lid reading cannot undo activation display-off")]
        private static void InitialOpenStateIsSynchronizationOnly()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.TurnOff, policy.Begin(), "activation goes dark");
            Assert.Equal(BlankAction.Nothing, policy.HandleLid(LidState.Opened),
                "the registration's current-state report is not an opening transition");
            Assert.False(policy.LidOpen, "the display guard remains armed while the user closes the lid");

            Assert.Equal(BlankAction.TurnOff, policy.HandleLid(LidState.Closed),
                "the subsequent physical close keeps it dark");
            Assert.Equal(BlankAction.TurnOn, policy.HandleLid(LidState.Opened),
                "a later physical opening wakes it");
        }

        [Test("the trailing input from activation cannot cancel AFK mode")]
        private static void ActivationInputIsDebounced()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.Nothing, policy.HandleInput(StartInput + 1, false),
                "the mouse-up belonging to the double-click is ignored");
            Assert.False(policy.Stopped, "the mode must remain armed");
        }

        [Test("keyboard or mouse input ends AFK mode once armed")]
        private static void ArmedInputStops()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.Stop, policy.HandleInput(StartInput + 1, true),
                "real return-to-machine input should restore normal mode");
            Assert.Equal("keyboard or mouse input was detected", policy.StopReason,
                "the reason should be diagnostic");
        }

        [Test("an unchanged input timestamp is not treated as input")]
        private static void UnchangedInputDoesNothing()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.Nothing, policy.HandleInput(StartInput, true),
                "polling must not invent activity");
            Assert.False(policy.Stopped, "the mode remains armed overnight");
        }

        [Test("the 32-bit input timestamp wrapping is still detected as input")]
        private static void InputWrapIsSafe()
        {
            var policy = new DisplayBlankPolicy(uint.MaxValue - 1);

            Assert.Equal(BlankAction.Stop, policy.HandleInput(3, true),
                "equality rather than subtraction makes wrapping safe");
        }

        [Test("unlocking the Windows session restores normal mode")]
        private static void UnlockStops()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.Stop, policy.HandleUnlocked(), "unlock should finish AFK");
            Assert.Equal("the session was unlocked", policy.StopReason, "reason");
        }

        [Test("a display event that says the session is unlocked also stops")]
        private static void DisplayEventSeesUnlock()
        {
            var policy = Fresh();

            Assert.Equal(BlankAction.Stop,
                policy.HandleDisplayState(DisplayState.On, false),
                "normal unlocked display ownership belongs to Windows");
        }

        [Test("a stopped policy never issues another display command")]
        private static void StopIsFinal()
        {
            var policy = Fresh();
            policy.HandleUnlocked();

            Assert.Equal(BlankAction.Nothing, policy.Begin(), "not on begin");
            Assert.Equal(BlankAction.Nothing, policy.HandleLid(LidState.Closed), "not on lid close");
            Assert.Equal(BlankAction.Nothing,
                policy.HandleDisplayState(DisplayState.On, true), "not on display relight");
        }

        [Test("every display decision is represented in the execution switch")]
        private static void EveryDecisionIsHandled()
        {
            var handled = new[]
            {
                BlankAction.Nothing,
                BlankAction.TurnOff,
                BlankAction.TurnOn,
                BlankAction.Stop
            };

            foreach (BlankAction action in Enum.GetValues(typeof(BlankAction)))
                Assert.True(Array.IndexOf(handled, action) >= 0,
                    "BlankAction." + action + " needs an execution path");
        }
    }
}
