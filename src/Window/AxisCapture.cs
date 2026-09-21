using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // Taking an axis from the player, as KSP's own input screen does it
    // (SettingsInputBinding.SetupAxis and Update, decompiled): KSP's input
    // manager defines the axes "joy<device>.<axis>" for 11 devices and 20 axes
    // each; their values are noted when listening starts, and the first axis that
    // moves more than half its range away from where it stood is taken. Escape
    // cancels.
    //
    // While it listens, KSP's controls are locked, so that the stick moved does
    // not fly the vessel on the way.
    internal static class AxisCapture
    {
        private const string LockId = "ReDefinition-axis-capture";
        private const int Devices = 11;
        private const int AxesPerDevice = 20;
        private const float Threshold = 0.5f;

        internal struct Taken
        {
            public string IdTag;
            public string Device;
            public int DeviceIndex;
            public int AxisIndex;
        }

        private static string listening;
        private static Action<Taken> take;
        private static float[] start;
        private static bool locked;

        internal static bool Listening(string key)
        {
            return listening != null && listening == key;
        }

        internal static void Start(string key, Action<Taken> taken)
        {
            if (Listening(key))
            {
                Stop();
                return;
            }
            KeyCapture.Stop();
            start = new float[Devices * AxesPerDevice];
            for (int i = 0; i < start.Length; i++) start[i] = Read(i / AxesPerDevice, i % AxesPerDevice);
            listening = key;
            take = taken;
            Lock();
        }

        internal static void Stop()
        {
            listening = null;
            take = null;
            Unlock();
        }

        // From the add-on's Update, every frame.
        internal static void Poll()
        {
            if (listening == null)
            {
                Unlock();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Stop();
                return;
            }
            for (int device = 0; device < Devices; device++)
            {
                for (int axis = 0; axis < AxesPerDevice; axis++)
                {
                    if (Mathf.Abs(Read(device, axis) - start[device * AxesPerDevice + axis]) <= Threshold) continue;
                    Action<Taken> taken = take;
                    listening = null;
                    take = null;
                    Unlock();
                    if (taken != null)
                    {
                        taken(new Taken
                        {
                            IdTag = "joy" + device + "." + axis,
                            Device = DeviceName(device),
                            DeviceIndex = device,
                            AxisIndex = axis,
                        });
                    }
                    return;
                }
            }
        }

        // An axis KSP's input manager does not define reads as still.
        private static float Read(int device, int axis)
        {
            try
            {
                return Input.GetAxis("joy" + device + "." + axis);
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        // As KSP names it: the controller's name trimmed, or "Joystick <n>".
        private static string DeviceName(int device)
        {
            try
            {
                string[] names = Input.GetJoystickNames();
                if (device < names.Length && !string.IsNullOrEmpty(names[device]))
                    return InputDevices.TrimDeviceName(names[device]);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("axis-device-name", "A controller's name could not be read ("
                                                          + CompatibilityLog.Reason(e) + ").");
            }
            return "Joystick " + device;
        }

        private static void Lock()
        {
            if (locked) return;
            InputLockManager.SetControlLock(ControlTypes.All, LockId);
            locked = true;
        }

        private static void Unlock()
        {
            if (!locked) return;
            InputLockManager.RemoveControlLock(LockId);
            locked = false;
        }
    }
}
