﻿using System;

namespace Iot.Device.Arduino.Runtime
{
    [ArduinoReplacement(typeof(System.Threading.Thread), true)]
    internal class MiniThread
    {
        /// <summary>
        /// This method performs busy waiting for a specified number of milliseconds.
        /// It is not implemented as low-level function because this allows other code to continue.
        /// That means this does not block other tasks (and particularly the communication) from working.
        /// </summary>
        /// <param name="delayMs">Number of milliseconds to sleep</param>
        public static void Sleep(int delayMs)
        {
            if (delayMs <= 0)
            {
                return;
            }

            int ticks = Environment.TickCount;
            int endTicks = ticks + delayMs;
            if (ticks > endTicks)
            {
                // There will be a wraparound
                int previous = ticks;
                // wait until the tick count wraps around
                while (previous < ticks)
                {
                    previous = ticks;
                    ticks = Environment.TickCount;
                }
            }

            while (endTicks > ticks)
            {
                // Busy waiting is ok here - the microcontroller has no sleep state
                ticks = Environment.TickCount;
            }
        }

        public static void Sleep(TimeSpan delay)
        {
            Sleep((int)delay.TotalMilliseconds);
        }

        public static bool Yield()
        {
            // We are running in a single-thread environment, so this is effectively a no-op
            return false;
        }

        [ArduinoImplementation(NativeMethod.ArduinoNativeHelpersGetMicroseconds)]
        public static void SpinWait(int micros)
        {
            throw new NotImplementedException();
        }

        public static int OptimalMaxSpinWaitsPerSpinIteration
        {
            get
            {
                return 1;
            }
        }

        [ArduinoImplementation(NativeMethod.None)]
        public static int GetCurrentProcessorId()
        {
            return 0;
        }

        public void Join()
        {
            // Threads are not yet supported
        }
    }
}