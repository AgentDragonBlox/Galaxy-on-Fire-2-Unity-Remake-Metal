// MacGameControllers.cs
// Remake, macOS: controllers through Apple's GameController framework. The Input System reads controllers on macOS at the
// HID level and knows only the models it has layouts for (Xbox pads, DualShock 4 / DualSense, the Switch Pro Controller,
// the Nimbus+); everything else (8BitDo and other third-party pads, Joy-Cons, MFi controllers) showed up as a generic
// joystick, which the game's <Gamepad> bindings ignore. Apple's framework maps them all, as native Mac games use it.
// Each GameController controller with an extended gamepad profile becomes a virtual Gamepad (MacGameControllerGamepad, like
// VrPad's), read through the Objective-C runtime (libobjc's objc_msgSend, the framework opened with dlopen: nothing to
// compile on a Mac, Mono and IL2CPP alike) and fed in InputSystem.onBeforeUpdate, so every binding, rebinding, the menus'
// navigation and InputMode work unchanged. A controller the Input System already reads as a gamepad of the same family
// (Xbox, Sony, Switch Pro, Nimbus) is left to it: both see the same pad, and the Input System's own device has rumble.
// Apple's mapping: buttonA / B / X / Y = south / east / west / north (by position, Nintendo pads too), buttonMenu = Menu
// (start), buttonOptions = View / Create / Share (select), the stick presses, shoulders, triggers, d-pad. No rumble on the
// virtual pads (GameController's haptics are CoreHaptics; not built). Not run on a Mac yet.

#if UNITY_STANDALONE_OSX || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;

namespace GoF2Remake.UI
{
    /// <summary>A controller read through macOS's GameController framework (MacGameControllers).</summary>
    [InputControlLayout(displayName = "Game Controller (macOS)")]
    [UnityEngine.Scripting.Preserve]
    public class MacGameControllerGamepad : Gamepad { }

    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class MacGameControllers
    {
        /// <summary>The InputDeviceDescription interface of the virtual pads (their layout's matcher).</summary>
        public const string Interface = "MacGameController";

        /// <summary>Which pads a controller counts with: the Input System's own layouts on macOS read these families.</summary>
        public enum Family { Other, Xbox, Sony, SwitchPro, Nimbus }

        static bool installed, unavailable, broken;
        static readonly Dictionary<IntPtr, Gamepad> pads = new Dictionary<IntPtr, Gamepad>();
        static readonly List<IntPtr> seen = new List<IntPtr>();
        static readonly List<IntPtr> stale = new List<IntPtr>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            installed = false;
            unavailable = false;
            broken = false;
            pads.Clear();
        }

        /// <summary>A controller's family from GameController's productCategory ("Xbox One", "DualSense", "DualShock 4",
        /// "Switch Pro Controller", "MFi"...) and vendorName.</summary>
        public static Family FamilyOf(string productCategory, string vendorName)
        {
            if (Has(productCategory, "Xbox")) return Family.Xbox;
            if (Has(productCategory, "DualShock") || Has(productCategory, "DualSense")) return Family.Sony;
            if (Has(productCategory, "Switch Pro")) return Family.SwitchPro;
            if (Has(vendorName, "Nimbus")) return Family.Nimbus;
            return Family.Other;
        }

        /// <summary>The family of a gamepad the Input System reads itself, by its layout (Other: none of the four).</summary>
        public static Family FamilyOfLayout(string layout)
        {
            if (Has(layout, "Xbox") || Has(layout, "XInput")) return Family.Xbox;
            if (Has(layout, "DualShock") || Has(layout, "DualSense")) return Family.Sony;
            if (Has(layout, "SwitchPro")) return Family.SwitchPro;
            if (Has(layout, "Nimbus")) return Family.Nimbus;
            return Family.Other;
        }

        static bool Has(string s, string part) => !string.IsNullOrEmpty(s) && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool OnMac => Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            if (!OnMac || Application.isBatchMode) return;
            Install();
        }

        static void Install()
        {
            if (installed || unavailable) return;
            try { Objc.Load(); }
            catch (Exception e)
            {
                unavailable = true;
                Debug.LogWarning("MacGameControllers: the GameController framework isn't available (" + e.Message + ")");
                return;
            }
            if (!Objc.Ready) { unavailable = true; Debug.LogWarning("MacGameControllers: no GCController class"); return; }
            installed = true;
            InputSystem.RegisterLayout<MacGameControllerGamepad>(matches: new InputDeviceMatcher().WithInterface(Interface));
            InputSystem.onBeforeUpdate -= Feed;
            InputSystem.onBeforeUpdate += Feed;
            var runner = new GameObject("MacGameControllers") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<MacGameControllerRunner>();
            Sync();
        }

        /// <summary>MacGameControllerRunner, when it goes (Play mode ends, the game quits): the virtual pads go too.</summary>
        internal static void Uninstall()
        {
            InputSystem.onBeforeUpdate -= Feed;
            foreach (var pad in pads.Values) if (pad != null && pad.added) InputSystem.RemoveDevice(pad);
            pads.Clear();
            installed = false;
        }

        /// <summary>MacGameControllerRunner, twice a second: a virtual pad for every controller GameController lists that
        /// the Input System doesn't read itself, and none for the ones gone. Devices are added here, outside the input
        /// update.</summary>
        internal static void Sync()
        {
            if (!installed) return;
            if (broken)
            {
                // Feed failed: the pads go (here, outside the input update) and the framework isn't read again this run.
                Uninstall();
                unavailable = true;
                return;
            }
            var pool = Objc.objc_autoreleasePoolPush();
            try
            {
                // The Input System's own gamepads by family, each of which takes one GameController controller of it.
                var native = new Dictionary<Family, int>();
                foreach (var g in Gamepad.all)
                {
                    if (g is MacGameControllerGamepad || !g.added) continue;
                    var f = FamilyOfLayout(g.layout);
                    if (f != Family.Other) native[f] = native.TryGetValue(f, out var n) ? n + 1 : 1;
                }
                seen.Clear();
                var list = Objc.Send(Objc.GCController, Objc.selControllers);
                long count = list == IntPtr.Zero ? 0 : Objc.SendLong(list, Objc.selCount);
                for (long i = 0; i < count; i++)
                {
                    var controller = Objc.SendIndex(list, Objc.selObjectAtIndex, (ulong)i);
                    if (controller == IntPtr.Zero) continue;
                    if (Objc.Send(controller, Objc.selExtendedGamepad) == IntPtr.Zero) continue;   // a micro gamepad only
                    string category = Objc.Responds(controller, Objc.selProductCategory) ? Objc.Text(Objc.Send(controller, Objc.selProductCategory)) : null;
                    string vendor = Objc.Text(Objc.Send(controller, Objc.selVendorName));
                    var family = FamilyOf(category, vendor);
                    if (family != Family.Other && native.TryGetValue(family, out var left) && left > 0)
                    {
                        native[family] = left - 1;   // the Input System's own device reads this one
                        continue;
                    }
                    seen.Add(controller);
                    if (pads.ContainsKey(controller)) continue;
                    string product = !string.IsNullOrEmpty(category) && category != "MFi" && category != "HID" ? category
                                   : !string.IsNullOrEmpty(vendor) ? vendor : "Game Controller";
                    var pad = (Gamepad)InputSystem.AddDevice(new InputDeviceDescription
                    {
                        interfaceName = Interface,
                        product = product,
                        manufacturer = family == Family.Sony ? "Sony Interactive Entertainment" : vendor,
                        serial = controller.ToInt64().ToString("x"),
                    });
                    pads[controller] = pad;
                    Debug.Log($"MacGameControllers: {product} ({vendor}) as a gamepad");
                }
                stale.Clear();
                foreach (var key in pads.Keys) if (!seen.Contains(key)) stale.Add(key);
                foreach (var key in stale)
                {
                    var pad = pads[key];
                    pads.Remove(key);
                    if (pad != null && pad.added) InputSystem.RemoveDevice(pad);
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { Objc.objc_autoreleasePoolPop(pool); }
        }

        /// <summary>InputSystem.onBeforeUpdate: each virtual pad's state from its controller, in the same update. Only the
        /// controllers GameController lists now are read (the list holds them), so a pad gone since the last Sync is
        /// skipped.</summary>
        static void Feed()
        {
            if (broken || pads.Count == 0 || InputState.currentUpdateType == InputUpdateType.BeforeRender) return;
            var pool = Objc.objc_autoreleasePoolPush();
            try
            {
                var list = Objc.Send(Objc.GCController, Objc.selControllers);
                long count = list == IntPtr.Zero ? 0 : Objc.SendLong(list, Objc.selCount);
                for (long i = 0; i < count; i++)
                {
                    var controller = Objc.SendIndex(list, Objc.selObjectAtIndex, (ulong)i);
                    if (!pads.TryGetValue(controller, out var pad) || pad == null || !pad.added) continue;
                    var g = Objc.Send(controller, Objc.selExtendedGamepad);
                    if (g == IntPtr.Zero) continue;
                    InputSystem.QueueStateEvent(pad, Read(g));
                }
            }
            catch (Exception e) { Debug.LogException(e); broken = true; }
            finally { Objc.objc_autoreleasePoolPop(pool); }
        }

        static GamepadState Read(IntPtr g)
        {
            var s = new GamepadState
            {
                leftStick = Stick(Objc.Send(g, Objc.selLeftThumbstick)),
                rightStick = Stick(Objc.Send(g, Objc.selRightThumbstick)),
                leftTrigger = Objc.SendFloat(Objc.Send(g, Objc.selLeftTrigger), Objc.selValue),
                rightTrigger = Objc.SendFloat(Objc.Send(g, Objc.selRightTrigger), Objc.selValue),
            };
            var dpad = Objc.Send(g, Objc.selDpad);
            s = s.WithButton(GamepadButton.South, Pressed(g, Objc.selButtonA))
                 .WithButton(GamepadButton.East, Pressed(g, Objc.selButtonB))
                 .WithButton(GamepadButton.West, Pressed(g, Objc.selButtonX))
                 .WithButton(GamepadButton.North, Pressed(g, Objc.selButtonY))
                 .WithButton(GamepadButton.LeftShoulder, Pressed(g, Objc.selLeftShoulder))
                 .WithButton(GamepadButton.RightShoulder, Pressed(g, Objc.selRightShoulder))
                 .WithButton(GamepadButton.LeftTrigger, s.leftTrigger > 0.5f)
                 .WithButton(GamepadButton.RightTrigger, s.rightTrigger > 0.5f)
                 .WithButton(GamepadButton.DpadUp, Pressed(dpad, Objc.selUp))
                 .WithButton(GamepadButton.DpadDown, Pressed(dpad, Objc.selDown))
                 .WithButton(GamepadButton.DpadLeft, Pressed(dpad, Objc.selLeft))
                 .WithButton(GamepadButton.DpadRight, Pressed(dpad, Objc.selRight))
                 .WithButton(GamepadButton.Start, PressedIf(g, Objc.selButtonMenu))
                 .WithButton(GamepadButton.Select, PressedIf(g, Objc.selButtonOptions))
                 .WithButton(GamepadButton.LeftStick, PressedIf(g, Objc.selLeftThumbstickButton))
                 .WithButton(GamepadButton.RightStick, PressedIf(g, Objc.selRightThumbstickButton));
            return s;
        }

        static Vector2 Stick(IntPtr pad) => pad == IntPtr.Zero ? Vector2.zero
            : new Vector2(Objc.SendFloat(Objc.Send(pad, Objc.selXAxis), Objc.selValue),
                          Objc.SendFloat(Objc.Send(pad, Objc.selYAxis), Objc.selValue));

        static bool Pressed(IntPtr owner, IntPtr selButton)
        {
            if (owner == IntPtr.Zero) return false;
            var button = Objc.Send(owner, selButton);
            return button != IntPtr.Zero && Objc.SendBool(button, Objc.selIsPressed) != 0;
        }

        /// <summary>A button of a later macOS (the menu / options buttons 10.15, the stick presses 10.14.1): asked only when
        /// the profile has it, since an unknown selector raises an Objective-C exception.</summary>
        static bool PressedIf(IntPtr g, IntPtr selButton) => Objc.Responds(g, selButton) && Pressed(g, selButton);

        /// <summary>The Objective-C runtime and the GameController framework.</summary>
        [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
        static class Objc
        {
            const string LibObjc = "/usr/lib/libobjc.A.dylib";
            const string LibSystem = "/usr/lib/libSystem.B.dylib";
            const string Framework = "/System/Library/Frameworks/GameController.framework/GameController";

            [DllImport(LibSystem)] static extern IntPtr dlopen(string path, int mode);
            [DllImport(LibObjc)] static extern IntPtr objc_getClass(string name);
            [DllImport(LibObjc)] static extern IntPtr sel_registerName(string name);
            [DllImport(LibObjc)] public static extern IntPtr objc_autoreleasePoolPush();
            [DllImport(LibObjc)] public static extern void objc_autoreleasePoolPop(IntPtr pool);
            // objc_msgSend called with each return type it is used for (arm64 and x86_64 both take the plain entry point for
            // these: objc_msgSend_fpret is i386's float return and x86_64's long double only).
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendIndex(IntPtr self, IntPtr sel, ulong index);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] static extern IntPtr SendSel(IntPtr self, IntPtr sel, IntPtr arg);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] public static extern long SendLong(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] public static extern float SendFloat(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] public static extern byte SendBool(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] static extern byte SendBoolSel(IntPtr self, IntPtr sel, IntPtr arg);

            public static IntPtr GCController;
            public static IntPtr selControllers, selCount, selObjectAtIndex, selExtendedGamepad, selProductCategory, selVendorName,
                selUtf8, selResponds, selLeftThumbstick, selRightThumbstick, selXAxis, selYAxis, selValue, selIsPressed,
                selLeftTrigger, selRightTrigger, selLeftShoulder, selRightShoulder, selButtonA, selButtonB, selButtonX, selButtonY,
                selDpad, selUp, selDown, selLeft, selRight, selButtonMenu, selButtonOptions, selLeftThumbstickButton,
                selRightThumbstickButton;

            public static bool Ready => GCController != IntPtr.Zero;

            public static void Load()
            {
                dlopen(Framework, 2);   // RTLD_NOW; the class is registered once the framework is loaded
                GCController = objc_getClass("GCController");
                IntPtr S(string name) => sel_registerName(name);
                selControllers = S("controllers"); selCount = S("count"); selObjectAtIndex = S("objectAtIndex:");
                selExtendedGamepad = S("extendedGamepad"); selProductCategory = S("productCategory"); selVendorName = S("vendorName");
                selUtf8 = S("UTF8String"); selResponds = S("respondsToSelector:");
                selLeftThumbstick = S("leftThumbstick"); selRightThumbstick = S("rightThumbstick");
                selXAxis = S("xAxis"); selYAxis = S("yAxis"); selValue = S("value"); selIsPressed = S("isPressed");
                selLeftTrigger = S("leftTrigger"); selRightTrigger = S("rightTrigger");
                selLeftShoulder = S("leftShoulder"); selRightShoulder = S("rightShoulder");
                selButtonA = S("buttonA"); selButtonB = S("buttonB"); selButtonX = S("buttonX"); selButtonY = S("buttonY");
                selDpad = S("dpad"); selUp = S("up"); selDown = S("down"); selLeft = S("left"); selRight = S("right");
                selButtonMenu = S("buttonMenu"); selButtonOptions = S("buttonOptions");
                selLeftThumbstickButton = S("leftThumbstickButton"); selRightThumbstickButton = S("rightThumbstickButton");
            }

            public static bool Responds(IntPtr obj, IntPtr sel) => obj != IntPtr.Zero && SendBoolSel(obj, selResponds, sel) != 0;

            public static string Text(IntPtr nsString)
            {
                if (nsString == IntPtr.Zero) return null;
                var utf8 = Send(nsString, selUtf8);
                return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
            }
        }
    }
}
#endif
