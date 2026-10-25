// AppleDeviceState.mm
// Apple fork, iOS: the device's thermal state and Low Power Mode for PerformanceGovernor (compiled into the Xcode project;
// on macOS the governor reads NSProcessInfo through the Objective-C runtime instead).

#import <Foundation/Foundation.h>

extern "C"
{
    // NSProcessInfoThermalState: 0 nominal, 1 fair, 2 serious, 3 critical.
    int GoF2Device_ThermalState()
    {
        return (int)[NSProcessInfo processInfo].thermalState;
    }

    // 1 while Low Power Mode is on.
    int GoF2Device_LowPowerMode()
    {
        if (@available(macOS 12.0, iOS 9.0, *)) return [NSProcessInfo processInfo].isLowPowerModeEnabled ? 1 : 0;
        return 0;
    }
}
