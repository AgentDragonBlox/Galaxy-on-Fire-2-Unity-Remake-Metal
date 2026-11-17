// PerformanceGovernor.cs
// Apple fork: keeps the game cool and within the battery's budget on iPhones, iPads and Macs (a MacBook Air has no fan).
// Every 2 s it reads the device's thermal state and Low Power Mode and sets Bootstrap.GovernorFrameCap / GovernorScale,
// limits applied on top of the player's options, never above them (the V-Sync default stays the default):
//   thermal serious    60 fps cap, render scale x0.85
//   thermal critical   30 fps cap, render scale x0.7
//   Low Power Mode     30 fps cap on iOS, 60 on a Mac
// It steps up at once and back down only after 10 s at the cooler state, so a state flickering at a threshold doesn't
// reallocate the camera's targets every few seconds. MetalFX keeps a lowered render scale sharp.
// Read on iOS through AppleDeviceState.mm (Plugins/iOS, compiled into the Xcode project); on macOS straight through the
// Objective-C runtime (NSProcessInfo), as MacGameControllers reads GameController: no native plugin to build.

#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GoF2Remake
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public sealed class PerformanceGovernor : MonoBehaviour
    {
        const float PollSeconds = 2f, CoolDownSeconds = 10f;

        static PerformanceGovernor instance;

        /// <summary>0 normal, 1 warm (thermal state serious), 2 hot (critical).</summary>
        public static int Level => instance != null ? instance.level : 0;
        public static bool LowPower => instance != null && instance.lowPower;

        int level;
        bool lowPower;
        float nextPoll, coolingSince = -1f;

        public static void Install()
        {
            if (instance != null) return;
            var go = new GameObject("PerformanceGovernor") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<PerformanceGovernor>();
        }

        void Update()
        {
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + PollSeconds;

            int thermal;
            bool lpm;
            try
            {
                thermal = Device.ThermalState();
                lpm = Device.LowPowerMode();
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                Debug.LogWarning("PerformanceGovernor: the device state isn't readable, the governor is off. " + e.Message);
                enabled = false;
                return;
            }

            int target = thermal >= 3 ? 2 : thermal == 2 ? 1 : 0;
            if (target > level) { level = target; coolingSince = -1f; }
            else if (target < level)
            {
                if (coolingSince < 0f) coolingSince = Time.unscaledTime;
                else if (Time.unscaledTime - coolingSince >= CoolDownSeconds) { level = target; coolingSince = -1f; }
            }
            else coolingSince = -1f;
            lowPower = lpm;
            Apply();
        }

        void Apply()
        {
            int cap = level == 2 ? 30 : level == 1 ? 60 : 0;
            if (lowPower)
            {
                int lowPowerCap = Application.isMobilePlatform ? 30 : 60;
                cap = cap > 0 ? Mathf.Min(cap, lowPowerCap) : lowPowerCap;
            }
            float scale = level == 2 ? 0.7f : level == 1 ? 0.85f : 1f;
            if (cap == Bootstrap.GovernorFrameCap && Mathf.Approximately(scale, Bootstrap.GovernorScale)) return;
            Bootstrap.GovernorFrameCap = cap;
            Bootstrap.GovernorScale = scale;
            Bootstrap.ApplyPerformanceLimits();
            Debug.Log($"PerformanceGovernor: thermal level {level}, low power {lowPower}: frame cap {(cap > 0 ? cap.ToString() : "none")}, render scale x{scale:0.00}");
        }

        void OnDestroy()
        {
            if (instance != this) return;
            instance = null;
            Bootstrap.GovernorFrameCap = 0;
            Bootstrap.GovernorScale = 1f;
        }

        /// <summary>NSProcessInfo's thermalState (0 nominal, 1 fair, 2 serious, 3 critical) and isLowPowerModeEnabled.</summary>
        [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
        static class Device
        {
#if UNITY_IOS && !UNITY_EDITOR
            [DllImport("__Internal")] static extern int GoF2Device_ThermalState();
            [DllImport("__Internal")] static extern int GoF2Device_LowPowerMode();

            public static int ThermalState() => GoF2Device_ThermalState();
            public static bool LowPowerMode() => GoF2Device_LowPowerMode() != 0;
#else
            const string LibObjc = "/usr/lib/libobjc.A.dylib";

            [DllImport(LibObjc)] static extern IntPtr objc_getClass(string name);
            [DllImport(LibObjc)] static extern IntPtr sel_registerName(string name);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] static extern long SendLong(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] static extern byte SendBool(IntPtr self, IntPtr sel);
            [DllImport(LibObjc, EntryPoint = "objc_msgSend")] static extern byte SendBoolSel(IntPtr self, IntPtr sel, IntPtr arg);

            static IntPtr processInfo, selThermalState, selLowPower;
            static bool hasLowPower;

            /// <summary>[NSProcessInfo processInfo], the process's shared instance (lives as long as the process).</summary>
            static IntPtr ProcessInfo()
            {
                if (processInfo != IntPtr.Zero) return processInfo;
                processInfo = Send(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
                selThermalState = sel_registerName("thermalState");
                selLowPower = sel_registerName("isLowPowerModeEnabled");
                // macOS 12+ only: an unknown selector raises an Objective-C exception, so ask first.
                hasLowPower = processInfo != IntPtr.Zero && SendBoolSel(processInfo, sel_registerName("respondsToSelector:"), selLowPower) != 0;
                return processInfo;
            }

            public static int ThermalState() => ProcessInfo() != IntPtr.Zero ? (int)SendLong(processInfo, selThermalState) : 0;
            public static bool LowPowerMode() => ProcessInfo() != IntPtr.Zero && hasLowPower && SendBool(processInfo, selLowPower) != 0;
#endif
        }
    }
}
#endif
