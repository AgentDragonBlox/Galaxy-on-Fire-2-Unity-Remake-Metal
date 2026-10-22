// MacGameControllersTests.cs
// Feature: CLAUDE.md "UI and platforms" > Controllers on macOS (UI.MacGameControllers): a controller Apple's GameController
// framework lists is skipped when the Input System already reads one of its family itself (both see the same pad), and a
// Sony pad's virtual device reports Sony so the PlayStation glyphs show.

using GoF2Remake.UI;
using NUnit.Framework;
using F = GoF2Remake.UI.MacGameControllers.Family;

namespace GoF2Remake.Tests
{
    public class MacGameControllersTests
    {
        [Test]
        public void GameControllerCategoriesFindTheirFamily()
        {
            Assert.AreEqual(F.Xbox, MacGameControllers.FamilyOf("Xbox One", "Xbox Wireless Controller"));
            Assert.AreEqual(F.Sony, MacGameControllers.FamilyOf("DualSense", "DualSense Wireless Controller"));
            Assert.AreEqual(F.Sony, MacGameControllers.FamilyOf("DualShock 4", "Wireless Controller"));
            Assert.AreEqual(F.SwitchPro, MacGameControllers.FamilyOf("Switch Pro Controller", "Pro Controller"));
            Assert.AreEqual(F.Nimbus, MacGameControllers.FamilyOf("MFi", "Nimbus+"));
            Assert.AreEqual(F.Other, MacGameControllers.FamilyOf("MFi", "8BitDo Pro 2"));
            Assert.AreEqual(F.Other, MacGameControllers.FamilyOf("Nintendo Switch Joy-Con (L/R)", "Joy-Con (L/R)"));
            Assert.AreEqual(F.Other, MacGameControllers.FamilyOf(null, null));
        }

        [Test]
        public void TheInputSystemsOwnMacLayoutsFindTheirFamily()
        {
            Assert.AreEqual(F.Xbox, MacGameControllers.FamilyOfLayout("XboxGamepadMacOS"));
            Assert.AreEqual(F.Xbox, MacGameControllers.FamilyOfLayout("XboxOneGampadMacOSWireless"));
            Assert.AreEqual(F.Sony, MacGameControllers.FamilyOfLayout("DualSenseGamepadHID"));
            Assert.AreEqual(F.Sony, MacGameControllers.FamilyOfLayout("DualShock4GamepadHID"));
            Assert.AreEqual(F.SwitchPro, MacGameControllers.FamilyOfLayout("SwitchProControllerHID"));
            Assert.AreEqual(F.Nimbus, MacGameControllers.FamilyOfLayout("NimbusGamepadHid"));
            Assert.AreEqual(F.Other, MacGameControllers.FamilyOfLayout("Gamepad"));
            Assert.AreEqual(F.Other, MacGameControllers.FamilyOfLayout("MacGameControllerGamepad"));
        }

        [Test]
        public void ASonyPadReportedAsSonyShowsPlayStationGlyphs()
        {
            Assert.AreEqual(PadStyle.PlayStation, InputMode.StyleOf("MacGameControllerGamepad", "Sony Interactive Entertainment", "DualSense"));
            Assert.IsTrue(InputMode.IsDualShock4("MacGameControllerGamepad", "DualShock 4"));
            Assert.AreEqual(PadStyle.Xbox, InputMode.StyleOf("MacGameControllerGamepad", "8BitDo", "8BitDo Pro 2"));
        }
    }
}
