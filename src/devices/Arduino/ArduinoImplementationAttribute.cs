﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Iot.Device.Arduino
{
    /// <summary>
    /// Declares a method as being implemented natively on the Arduino
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
    public class ArduinoImplementationAttribute : Attribute
    {
        /// <summary>
        /// Default ctor. Use to indicate that a method is implemented in C#
        /// </summary>
        public ArduinoImplementationAttribute()
        {
            MethodNumber = 0;
            Name = string.Empty;
        }

        /// <summary>
        /// This method is implemented in native C++ code. The visible body of the method is not executed.
        /// </summary>
        /// <param name="methodName">Name of the implementation method. The internal code is the hash code of this. No two methods must have names with colliding hash codes.
        /// This is verified when writing the C++ header</param>
        /// <remarks>See comments on <see cref="ArduinoImplementationAttribute(string,int)"/></remarks>
        public ArduinoImplementationAttribute(string methodName)
        {
            if (!string.IsNullOrWhiteSpace(methodName))
            {
                MethodNumber = methodName.GetHashCode();
            }
            else
            {
                MethodNumber = 0;
            }

            Name = methodName;
        }

        /// <summary>
        /// This method is implemented in native C++ code. The visible body of the method is not executed.
        /// </summary>
        /// <param name="methodName">Name of the implementation method.</param>
        /// <param name="methodNumber">Internal number of the method. Must be unique across as implementations and not collide with hash codes. It is recommended
        /// to manually set that to consecutive numbers for consecutive functions, because that reduces code size and increases performance</param>
        /// <remarks>
        /// Auto-assigned numbers have the downside that they're spread over the whole range of int, which makes it almost impossible for the compiler to generate
        /// efficient jump tables in the switch statements.
        /// </remarks>
        public ArduinoImplementationAttribute(string methodName, int methodNumber)
        {
            MethodNumber = methodNumber;
            Name = methodName;
        }

        /// <summary>
        /// Name used when constructing this instance
        /// </summary>
        public string Name
        {
            get;
        }

        /// <summary>
        /// The implementation number
        /// </summary>
        public int MethodNumber
        {
            get;
        }

        /// <summary>
        /// If this is set, the parameter types are only compared by name, not type (useful to replace a method with an argument of an internal type)
        /// This can also be used to replace methods with generic argument types
        /// </summary>
        public bool CompareByParameterNames
        {
            get;
            set;
        }

        /// <summary>
        /// If this is set, the type of the generic arguments is ignored, meaning that all implementations use the same method.
        /// </summary>
        public bool IgnoreGenericTypes
        {
            get;
            set;
        }
    }
}