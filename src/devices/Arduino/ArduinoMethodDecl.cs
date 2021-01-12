﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

#pragma warning disable CS1591
namespace Iot.Device.Arduino
{
    public sealed class ArduinoMethodDeclaration
    {
        public ArduinoMethodDeclaration(int token, MethodBase methodBase, ArduinoMethodDeclaration? requestedBy, byte[]? ilBytes)
        {
            Index = -1;
            MethodBase = methodBase;
            RequestedBy = requestedBy;
            IlBytes = ilBytes;
            Flags = MethodFlags.None;
            Token = token;

            var attribs = methodBase.GetCustomAttributes(typeof(ArduinoImplementationAttribute)).Cast<ArduinoImplementationAttribute>().ToList();

            if (methodBase.IsAbstract)
            {
                MaxLocals = 0;
                MaxStack = 0;
                HasBody = false;
                NativeMethod = Arduino.NativeMethod.None;
            }
            else if (attribs.Any(x => x.MethodNumber != Arduino.NativeMethod.None))
            {
                MaxLocals = 0;
                MaxStack = 0;
                HasBody = false;
                NativeMethod = attribs.First().MethodNumber;
                Flags |= MethodFlags.SpecialMethod;
            }
            else
            {
                var body = methodBase.GetMethodBody();
                if (body == null)
                {
                    throw new InvalidOperationException("Use this ctor only for methods that have a body");
                }

                MaxLocals = body.LocalVariables.Count;
                MaxStack = body.MaxStackSize;
                HasBody = true;
                NativeMethod = Arduino.NativeMethod.None;
            }

            ArgumentCount = methodBase.GetParameters().Length;
            if (methodBase.CallingConvention.HasFlag(CallingConventions.HasThis))
            {
                ArgumentCount += 1;
            }

            if (methodBase.IsStatic)
            {
                Flags |= MethodFlags.Static;
            }

            if (methodBase.IsVirtual)
            {
                Flags |= MethodFlags.Virtual;
            }

            if (methodBase is MethodInfo methodInfo)
            {
                if (methodInfo.ReturnParameter == null || methodInfo.ReturnParameter.ParameterType == typeof(void))
                {
                    Flags |= MethodFlags.Void;
                }
            }

            if (methodBase.IsConstructor && !methodBase.IsStatic)
            {
                Flags |= MethodFlags.Ctor;
            }

            if (methodBase.IsAbstract)
            {
                Flags |= MethodFlags.Abstract;
            }

            Name = $"{MethodBase.DeclaringType} - {methodBase}";
        }

        public ArduinoMethodDeclaration(int token, MethodBase methodBase, ArduinoMethodDeclaration? requestedBy, MethodFlags flags, NativeMethod nativeMethod)
        {
            Index = -1;
            Token = token;
            MethodBase = methodBase;
            Flags = flags;
            NativeMethod = nativeMethod;
            MaxLocals = MaxStack = 0;
            HasBody = false;
            RequestedBy = requestedBy;
            ArgumentCount = methodBase.GetParameters().Length;
            if (methodBase.CallingConvention.HasFlag(CallingConventions.HasThis))
            {
                ArgumentCount += 1;
            }

            MethodInfo? mi = methodBase as MethodInfo;

            if (mi != null && mi.ReturnParameter.ParameterType == typeof(void))
            {
                Flags |= MethodFlags.Void;
            }

            if (methodBase.IsConstructor && !methodBase.IsStatic)
            {
                Flags |= MethodFlags.Ctor;
            }

            if (methodBase.IsStatic)
            {
                Flags |= MethodFlags.Static;
            }

            if (methodBase.IsVirtual)
            {
                Flags |= MethodFlags.Virtual;
            }

            Name = $"{MethodBase.DeclaringType} - {MethodBase} (Special Method)";
        }

        public bool HasBody
        {
            get;
        }

        public int Index
        {
            get;
            internal set;
        }

        public string Name
        {
            get;
        }

        public int Token { get; }
        public MethodBase MethodBase { get; }

        /// <summary>
        /// To simplify backtracking: The method that first requested this one to be loaded
        /// </summary>
        public ArduinoMethodDeclaration? RequestedBy { get; }

        public byte[]? IlBytes { get; }

        public MethodInfo MethodInfo
        {
            get
            {
                if (MethodBase is MethodInfo info)
                {
                    return info;
                }

                // Can't directly call fields. Internally, we do call ctors (especially cctors) directly, because
                // that is required for the init sequence
                throw new InvalidOperationException("Not callable Method");
            }
        }

        public MethodFlags Flags
        {
            get;
        }

        public NativeMethod NativeMethod { get; }

        public int MaxLocals
        {
            get;
        }

        public int MaxStack
        {
            get;
        }

        public int ArgumentCount
        {
            get;
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
