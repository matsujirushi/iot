﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using Iot.Device.Arduino;

namespace ArduinoCsCompiler.Runtime
{
    [ArduinoReplacement(typeof(DependentHandle), false, IncludingPrivates = true)]
    internal struct MiniDependentHandle
    {
        [ArduinoImplementation("DependentHandle_InternalInitialize")]
        public static IntPtr InternalInitialize(object target, object dependent)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("DependentHandle_InternalGetTarget")]
        public static object InternalGetTarget(IntPtr dependentHandle)
        {
            return MiniUnsafe.As<IntPtr, object>(ref dependentHandle);
        }

        [ArduinoImplementation("DependentHandle_InternalGetDependent")]
        public static object InternalGetDependent(IntPtr dependentHandle)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("DependentHandle_InternalGetTargetAndDependent")]
        private static object InternalGetTargetAndDependent(
            IntPtr dependentHandle,
            out object dependent)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("DependentHandle_InternalSetDependent")]
        private static void InternalSetDependent(IntPtr dependentHandle, object dependent)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("DependentHandle_InternalSetTarget")]
        private static void InternalSetTargetToNull(IntPtr dependentHandle)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("DependentHandle_InternalFree")]
        private static void InternalFree(IntPtr dependentHandle)
        {
            throw new NotImplementedException();
        }
    }
}
