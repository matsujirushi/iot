﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using ArduinoCsCompiler.Runtime;
using Iot.Device.Arduino;
using Iot.Device.Common;
using Microsoft.Extensions.Logging;
using UnitsNet;

#pragma warning disable CS1591
namespace ArduinoCsCompiler
{
    public sealed class MicroCompiler : IDisposable
    {
        private const int DataVersion = 1;

        /// <summary>
        /// The list of system assemblies (these may contain kernel interop calls)
        /// The list contains the name and a public type within that assembly
        /// </summary>
        private static readonly List<(string, Type)> SystemAssemblies = new List<(string, Type)>()
        {
            // "System.Private.Corelib.dll",
            ("System.Console", typeof(System.Console)),
            ("System.Diagnostics.Process", typeof(System.Diagnostics.Process)),
            // ("System.Net.Primitives", typeof(System.Net.IPAddress)),
            // ("System.Private.Uri", typeof(System.Uri)),
        };

        internal static readonly string PrivateImplementationDetailsName = "<PrivateImplementationDetails>";

        private readonly ArduinoBoard _board;
        private readonly List<ArduinoTask> _activeTasks;
        private readonly object _activeTasksLock;
        private readonly ILogger _logger;
        private readonly CompilerCommandHandler _commandHandler;

        private ExecutionSet? _activeExecutionSet;

        // List of classes that have arduino-native implementations
        // These classes substitute (part of) a framework class
        private List<Type> _replacementClasses;

        private bool _disposed = false;
        private Debugger? _debugger;

        public MicroCompiler(ArduinoBoard board, bool resetExistingCode = true)
        {
            _logger = this.GetCurrentClassLogger();
            _board = board;
            _debugger = null;

            _activeTasksLock = new object();
            _activeTasks = new List<ArduinoTask>();
            _activeExecutionSet = null;

            _commandHandler = new CompilerCommandHandler(this);
            board.AddCommandHandler(_commandHandler);

            if (resetExistingCode)
            {
                ClearAllData(true, false);
            }

            // Generate the list of all replacement classes (they're all called Mini*)
            _replacementClasses = new List<Type>();
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.GetCustomAttribute<ArduinoReplacementAttribute>() != null)
                {
                    _replacementClasses.Add(type);
                }
            }
        }

        internal CompilerCommandHandler CommandHandler => _commandHandler;

        private static bool HasStaticFields(Type cls)
        {
            foreach (var fld in cls.GetFields())
            {
                if (fld.IsStatic)
                {
                    return true;
                }
            }

            return false;
        }

        public static Type GetSystemPrivateType(string typeName)
        {
            var ret = Type.GetType(typeName);
            if (ret == null)
            {
                throw new InvalidOperationException($"Type {typeName} not found");
            }

            return ret;
        }

        private static void SwapFields<T>(List<T> fields, int firstIndex, int secondIndex)
        {
            T? temp = fields[firstIndex];
            fields[firstIndex] = fields[secondIndex];
            fields[secondIndex] = temp;
        }

        private string GetMethodName(ArduinoMethodDeclaration decl)
        {
            return decl.MethodBase.Name;
        }

        internal void TaskDone(ArduinoTask task)
        {
            lock (_activeTasksLock)
            {
                _activeTasks.Remove(task);
            }
        }

        internal void OnCompilerCallback(int taskId, MethodState state, object? args)
        {
            if (_activeExecutionSet == null)
            {
                return;
            }

            if (args == null)
            {
                return;
            }

            lock (_activeTasksLock)
            {
                var task = _activeTasks.FirstOrDefault(x => x.TaskId == taskId);

                if (task == null)
                {
                    // Because two threads run here, we might already have parsed this result
                    _logger.LogError($"Invalid method state update. {taskId} does not denote an active task.");
                    return;
                }

                if (task.State != MethodState.Running && task.HasResult)
                {
                    // Result already known
                    _logger.LogDebug($"Task {taskId} reported a result but had already ended.");

                    return;
                }

                var codeRef = task.MethodInfo;

                if (state == MethodState.Debugging)
                {
                    _logger.LogTrace("Hit a breakpoint. Decoding breakpoint position");
                    if (_debugger == null)
                    {
                        _logger.LogError("Code hit a breakpoint, but we're not debugging right now. This should not happen.");
                    }
                    else
                    {
                        _debugger.SaveLastExecutionState((byte[])args);
                    }

                    return; // Don't update the task state - for an outside observer, debugging does not affect the task state.
                }

                if (state == MethodState.Aborted)
                {
                    _logger.LogError($"Execution of method {GetMethodName(codeRef)} caused an exception. Check previous messages.");
                    // In this case, the data contains the exception tokens and the call stack tokens
                    task.AddData(state, ((int[])args).Cast<object>().ToArray());
                    return;
                }

                if (state == MethodState.Killed)
                {
                    _logger.LogError($"Execution of method {GetMethodName(codeRef)} was forcibly terminated.");
                    // Still update the task state, this will prevent a deadlock if somebody is waiting for this task to end
                    task.AddData(state, new object[0]);
                    return;
                }

                object[] outObjects = new object[1];
                if (state == MethodState.Stopped)
                {
                    object retVal;
                    byte[] data = (byte[])args;

                    _logger.LogDebug($"Task {taskId} has ended normally.");

                    // The method ended, therefore we know that the only element of args is the return value and can derive its correct type
                    Type returnType;
                    // We sometimes also execute ctors directly, but they return void
                    if (codeRef.MethodBase.MemberType == MemberTypes.Constructor)
                    {
                        returnType = typeof(void);
                    }
                    else
                    {
                        returnType = codeRef.MethodInfo!.ReturnType;
                    }

                    if (returnType == typeof(void))
                    {
                        // Empty return set
                        task.AddData(state, new object[0]);
                        return;
                    }
                    else if (returnType == typeof(bool))
                    {
                        retVal = data[0] != 0;
                    }
                    else if (returnType == typeof(UInt32))
                    {
                        retVal = BitConverter.ToUInt32(data);
                    }
                    else if (returnType == typeof(Int32))
                    {
                        retVal = BitConverter.ToInt32(data);
                    }
                    else if (returnType == typeof(float))
                    {
                        retVal = BitConverter.ToSingle(data);
                    }
                    else if (returnType == typeof(double))
                    {
                        retVal = BitConverter.ToDouble(data);
                    }
                    else if (returnType == typeof(Int64))
                    {
                        retVal = BitConverter.ToInt64(data);
                    }
                    else if (returnType == typeof(UInt64))
                    {
                        retVal = BitConverter.ToUInt64(data);
                    }
                    else
                    {
                        throw new NotSupportedException("Unsupported return type");
                    }

                    outObjects[0] = retVal;
                }

                task.AddData(state, outObjects);
            }
        }

        /// <summary>
        /// This adds a set of low-level methods to the execution set. These are intended to be copied to flash, as they will be used
        /// by many programs. We call the method set constructed here "the kernel".
        /// </summary>
        /// <param name="set">Execution set</param>
        private void PrepareLowLevelInterface(ExecutionSet set)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MicroCompiler));
            }

            void AddMethod(EquatableMethod method, int nativeMethod)
            {
                if (!set.HasMethod(method, method, out _))
                {
                    set.GetReplacement(method.DeclaringType);
                    var replacement = set.GetReplacement(method, method);
                    if (replacement != null)
                    {
                        method = replacement;
                        if (set.HasMethod(method, method, out _))
                        {
                            return;
                        }
                    }

                    int token = set.GetOrAddMethodToken(method, method);
                    ArduinoMethodDeclaration decl = new ArduinoMethodDeclaration(token, method, null, MethodFlags.SpecialMethod, nativeMethod);
                    set.AddMethod(decl);
                }
            }

            Type lowLevelInterface = typeof(ArduinoHardwareLevelAccess);
            foreach (var method in lowLevelInterface.GetMethods(BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var attr = (ArduinoImplementationAttribute)method.GetCustomAttributes(typeof(ArduinoImplementationAttribute)).First();
                AddMethod(new EquatableMethod(method), attr.MethodNumber);
            }

            MethodInfo? methodToReplace;

            // And the internal classes
            foreach (var replacement in _replacementClasses)
            {
                var attribs = replacement.GetCustomAttributes(typeof(ArduinoReplacementAttribute));
                ArduinoReplacementAttribute ia = (ArduinoReplacementAttribute)attribs.Single();
                if (ia.TypeToReplace == null)
                {
                    throw new NotSupportedException($"Internal error: {replacement.Name} has no class to replace");
                }

                if (ia.ReplaceEntireType)
                {
                    PrepareClass(set, replacement);
                    set.AddReplacementType(ia.TypeToReplace, replacement, ia.IncludingSubclasses, ia.IncludingPrivates);
                    if (ia.TypeToReplace.FullName == "Interop+Kernel32")
                    {
                        // The replacement class for Interop+Kernel32 shall replace all instances of this class, which is internal to
                        // many of the system assemblies, because the P/Invoke call declarations are shared between different parts of the runtime.
                        foreach (var s in SystemAssemblies)
                        {
                            Assembly ass = Assembly.GetAssembly(s.Item2) ?? throw new NotSupportedException($"Assembly containing {s.Item2} not found");
                            String assemblyName = ass.GetName().Name!;
                            if (assemblyName != s.Item1)
                            {
                                throw new NotSupportedException($"The type {s.Item2} was expected to be found in {s.Item1} but it was in {assemblyName} instead");
                            }

                            var interopType = ass.GetType("Interop+Kernel32", true, false);
                            set.AddReplacementType(interopType, replacement, false, true);
                        }
                    }
                }
                else
                {
                    foreach (var m in replacement.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    {
                        // Methods that have this attribute shall be replaced - if the value is ArduinoImplementation.None, the C# implementation is used,
                        // otherwise a native implementation is provided
                        attribs = m.GetCustomAttributes(typeof(ArduinoImplementationAttribute));
                        ArduinoImplementationAttribute? iaMethod = (ArduinoImplementationAttribute?)attribs.SingleOrDefault();
                        if (iaMethod != null)
                        {
                            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                            if (ia.IncludingPrivates)
                            {
                                flags |= BindingFlags.NonPublic;
                            }

                            methodToReplace = ia.TypeToReplace!.GetMethods(flags).SingleOrDefault(x => EquatableMethod.MethodsHaveSameSignature(x, m) || EquatableMethod.AreSameOperatorMethods(x, m));
                            if (methodToReplace == null)
                            {
                                // That may be ok if it is our own internal implementation, but for now we abort, since we currently have no such case
                                throw new InvalidOperationException($"A replacement method has nothing to replace: {m.MethodSignature()}");
                            }
                            else
                            {
                                set.AddReplacementMethod(methodToReplace, m);
                            }
                        }
                    }

                    // Also go over ctors (if any)
                    foreach (var m in replacement.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    {
                        // Methods that have this attribute shall be replaced - if the value is ArduinoImplementation.None, the C# implementation is used,
                        // otherwise a native implementation is provided
                        attribs = m.GetCustomAttributes(typeof(ArduinoImplementationAttribute));
                        ArduinoImplementationAttribute? iaMethod = (ArduinoImplementationAttribute?)attribs.SingleOrDefault();
                        if (iaMethod != null)
                        {
                            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                            if (ia.IncludingPrivates)
                            {
                                flags |= BindingFlags.NonPublic;
                            }

                            var ctor = ia.TypeToReplace!.GetConstructors(flags).SingleOrDefault(x => EquatableMethod.MethodsHaveSameSignature(x, m) || EquatableMethod.AreSameOperatorMethods(x, m));
                            if (ctor == null)
                            {
                                // That may be ok if it is our own internal implementation, but for now we abort, since we currently have no such case
                                throw new InvalidOperationException($"A replacement method has nothing to replace: {m.MethodSignature()}");
                            }
                            else
                            {
                                set.AddReplacementMethod(ctor, m);
                            }
                        }
                    }
                }
            }

            // Some special replacements required
            Type type = typeof(System.RuntimeTypeHandle);
            MethodInfo? replacementMethodInfo;
            var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static);
            methodToReplace = methods.First(x => x.Name == "CreateInstanceForAnotherGenericParameter");

            type = typeof(MiniType);
            replacementMethodInfo = type.GetMethod("CreateInstanceForAnotherGenericParameter");
            set.AddReplacementMethod(methodToReplace, replacementMethodInfo);

            // Some classes are dynamically created in the runtime - we need them anyway
            HashSet<object> hb = new HashSet<object>();
            PrepareClass(set, hb.Comparer.GetType()); // The actual instance here is ObjectEqualityComparer<object>

            PrepareClass(set, typeof(IEquatable<object>));

            // PrepareClass(set, typeof(System.Span<Int32>));
            HashSet<string> hs = new HashSet<string>();
            PrepareClass(set, hs.Comparer.GetType()); // GenericEqualityComparer<string>
            HashSet<int> hi = new HashSet<int>();
            PrepareClass(set, hi.Comparer.GetType()); // GenericEqualityComparer<int>
            PrepareClass(set, typeof(IEquatable<Nullable<int>>));

            PrepareClass(set, typeof(System.Array));

            PrepareClass(set, typeof(System.Object));
            PrepareClass(set, typeof(System.IComparable<object>));

            // These are referenced by the system
            PrepareClass(set, typeof(System.Exception));
            PrepareClass(set, typeof(System.DivideByZeroException));

            // We'll always need to provide these methods, or we'll get into trouble because they're not explicitly used before anything that depends on
            // them in the runtime
            type = typeof(System.Object);
            replacementMethodInfo = type.GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance)!; // Not the static one
            // Method numbers are hardcoded in attribute ctors!
            AddMethod(replacementMethodInfo, 2);
            replacementMethodInfo = type.GetMethod("ToString")!;
            AddMethod(replacementMethodInfo, 4);
            replacementMethodInfo = type.GetMethod("GetHashCode")!;
            AddMethod(replacementMethodInfo, 3);

            if (set.CompilerSettings.CreateKernelForFlashing)
            {
                // Finally, mark this set as "the kernel"
                set.CreateKernelSnapShot();
            }
        }

        internal void PrepareClass(ExecutionSet set, Type classType)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MicroCompiler));
            }

            HashSet<Type> baseTypes = new HashSet<Type>();

            baseTypes.Add(classType);
            DetermineBaseAndMembers(baseTypes, classType);

            foreach (var cls in baseTypes)
            {
                PrepareClassDeclaration(set, cls);
            }
        }

        private void PrepareClassDeclaration(ExecutionSet set, Type classType)
        {
            if (set.HasDefinition(classType))
            {
                return;
            }

            var replacement = set.GetReplacement(classType);

            if (replacement != null)
            {
                classType = replacement;
                if (set.HasDefinition(classType))
                {
                    return;
                }
            }

            List<FieldInfo> fields = new List<FieldInfo>();
            List<MemberInfo> methods = new List<MemberInfo>();

            GetFields(classType, fields, methods);

            if (classType == typeof(String))
            {
                // For string, we need to make sure the fields come in the correct order the EE expects.
                // The order can change randomly otherwise
                int idxLength = fields.IndexOf(fields.Single(x => x.Name == "_stringLength"));
                int idxFirstChar = fields.IndexOf(fields.Single(x => x.Name == "_firstChar"));
                if (idxLength > idxFirstChar)
                {
                    SwapFields(fields, idxLength, idxFirstChar);
                }
            }

            if (classType == typeof(Exception))
            {
                // For exception, we need to make sure the field "_message" is the first, because we directly access it in the EE.
                int idxMessage = fields.IndexOf(fields.Single(x => x.Name == "_message"));
                int idxSecond = 0; // swap with the zero'th element (unless it is already there)
                if (fields.Count < 2)
                {
                    throw new InvalidOperationException("System.Exception is expected to have at least two fields");
                }

                if (idxMessage != 0)
                {
                    SwapFields(fields, idxMessage, idxSecond);
                }
            }

            List<ClassMember> memberTypes = new List<ClassMember>();

            for (var index = 0; index < fields.Count; index++)
            {
                int staticFieldSize = 0;
                var field = fields[index];
                var fieldType = GetVariableType(field.FieldType, StructAlignment(classType, fields), out var size);
                if (field.IsStatic)
                {
                    fieldType |= VariableKind.StaticMember;
                    if (!field.FieldType.IsValueType)
                    {
                        size = SizeOfVoidPointer();
                    }

                    staticFieldSize = Math.Max(SizeOfVoidPointer(), size);
                }

                // The only (known) field that can contain a function pointer. Getting the type correct here helps in type tracking and debugging
                if (field.DeclaringType == typeof(Delegate) && field.Name == "_methodPtr")
                {
                    fieldType = VariableKind.FunctionPointer;
                }

                int token = 0;
                object? fieldValue;
                if (field.IsLiteral && classType.IsEnum)
                {
                    // This is a constant enum value field - provide the value instead of the token
                    fieldValue = field.GetValue(null)!;
                    Type underlyingType = Enum.GetUnderlyingType(classType);
                    if (underlyingType == typeof(int))
                    {
                        unchecked
                        {
                            int v = (int)fieldValue;
                            token = v;
                        }
                    }
                    else if (underlyingType == typeof(UInt32))
                    {
                        unchecked
                        {
                            uint v = (UInt32)fieldValue;
                            token = (int)v;
                        }
                    }
                    else if (underlyingType == typeof(byte))
                    {
                        unchecked
                        {
                            byte v = (byte)fieldValue;
                            token = v;
                        }
                    }
                    else if (underlyingType == typeof(UInt16))
                    {
                        unchecked
                        {
                            UInt16 v = (UInt16)fieldValue;
                            token = v;
                        }
                    }
                    else if (underlyingType == typeof(Int16))
                    {
                        unchecked
                        {
                            Int16 v = (Int16)fieldValue;
                            token = v;
                        }
                    }
                    else
                    {
                        throw new NotSupportedException($"Unable to cast {fieldValue} to a constant, when trying to read constant {field.Name} of {classType.MemberInfoSignature()}");
                    }
                }
                else if (field.IsLiteral)
                {
                    // This is a const field of a class. They're not normally used (unless via reflection), because the compiler directly replaces
                    // their use with a ldc.i4.XY or a LDSTR instruction.
                    continue;
                }
                else if (field.DeclaringType != null && field.DeclaringType.Name.Contains(PrivateImplementationDetailsName))
                {
                    // Now this is a constant field of the PrivateImplementationDetails class. We need its value somewhere
                    if (field.FieldType.Name.Contains("StaticArrayInitType", StringComparison.Ordinal))
                    {
                        // The value is added when an LD_TOKEN instruction is encountered, later in the parser
                        token = set.GetOrAddFieldToken(field);
                    }
                    else
                    {
                        fieldValue = field.GetValue(null)!;
                        byte[] data;
                        var t = fieldValue.GetType();
                        if (t == typeof(Int32))
                        {
                            data = BitConverter.GetBytes((int)fieldValue);
                        }
                        else if (t == typeof(Int64))
                        {
                            data = BitConverter.GetBytes((Int64)fieldValue);
                        }
                        else if (t == typeof(Int16))
                        {
                            data = BitConverter.GetBytes((Int16)fieldValue);
                        }
                        else
                        {
                            throw new NotSupportedException($"Unable to interpret value {fieldValue} as any known internal type");
                        }

                        token = set.GetOrAddFieldToken(field, data);
                    }
                }
                else
                {
                    token = set.GetOrAddFieldToken(field);
                }

                var newvar = new ClassMember(field, fieldType, token, size, staticFieldSize);
                memberTypes.Add(newvar);
            }

            for (var index = 0; index < methods.Count; index++)
            {
                var m = methods[index] as ConstructorInfo;
                if (m != null)
                {
                    memberTypes.Add(new ClassMember(m, VariableKind.Method, set.GetOrAddMethodToken(m, m), new List<int>()));
                }
            }

            var sizeOfClass = GetClassSize(classType);

            var interfaces = classType.GetInterfaces().ToList();

            // Add this first, so we break the recursion to this class further down
            var newClass = new ClassDeclaration(classType, sizeOfClass.Dynamic, sizeOfClass.Statics, set.GetOrAddClassToken(classType.GetTypeInfo()), memberTypes, interfaces);
            set.AddClass(newClass);
            foreach (var iface in interfaces)
            {
                PrepareClassDeclaration(set, iface);
            }

            if (classType.IsConstructedGenericType)
            {
                // If EqualityComparer<something> is used, we need to force a reference to IEquatable<something> and ObjectEqualityComparer<something>
                // as they sometimes fail to get recognized. This is because of the indirections in DefaultEqualityComparer
                var openType = classType.GetGenericTypeDefinition();
                if (openType == typeof(EqualityComparer<>))
                {
                    // The logic here can be refined by reversing the logic in ComparerHelpers.CreateDefaultEqualityComparer
                    var typeArgs = classType.GetGenericArguments();
                    var requiredInterface = typeof(IEquatable<>).MakeGenericType(typeArgs);
                    PrepareClassDeclaration(set, requiredInterface);
                    if (!typeArgs[0].IsValueType)
                    {
                        var alsoRequired = GetSystemPrivateType("System.Collections.Generic.ObjectEqualityComparer`1")!.MakeGenericType(typeArgs);
                        PrepareClassDeclaration(set, alsoRequired);
                    }
                    else if (typeArgs[0].IsGenericType && typeArgs[0].GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        var embeddedType = typeArgs[0].GetGenericArguments()[0];
                        var alsoRequired = GetSystemPrivateType("System.Collections.Generic.NullableEqualityComparer`1")!.MakeGenericType(embeddedType);
                        PrepareClassDeclaration(set, alsoRequired);
                    }
                    else if (!typeArgs[0].IsAssignableTo(requiredInterface))
                    {
                        // If the value type being declared does not implement IEquatable<OfItself> we need an ObjectEqualityComparer<T> instead
                        var alsoRequired = GetSystemPrivateType("System.Collections.Generic.ObjectEqualityComparer`1")!.MakeGenericType(typeArgs);
                        PrepareClassDeclaration(set, alsoRequired);
                    }
                    else if (typeArgs[0].IsValueType)
                    {
                        try
                        {
                            // This throws if the given types violate a constraint for the comparer
                            var alsoRequired = GetSystemPrivateType("System.Collections.Generic.GenericEqualityComparer`1")!.MakeGenericType(typeArgs);
                            PrepareClassDeclaration(set, alsoRequired);
                            alsoRequired = GetSystemPrivateType("System.Collections.Generic.GenericComparer`1")!.MakeGenericType(typeArgs);
                            PrepareClassDeclaration(set, alsoRequired);
                        }
                        catch (ArgumentException x)
                        {
                            _logger.LogWarning(x, x.Message);
                        }
                    }
                }

                if (openType == typeof(Comparer<>))
                {
                    var typeArgs = classType.GetGenericArguments();
                    var requiredInterface = typeof(IComparable<>).MakeGenericType(typeArgs);
                    PrepareClassDeclaration(set, requiredInterface);
                    var alsoRequired = GetSystemPrivateType("System.Collections.Generic.ObjectComparer`1")!.MakeGenericType(typeArgs);
                    PrepareClassDeclaration(set, alsoRequired);
                }
            }
        }

        private void CompleteClasses(ExecutionSet set)
        {
            // Complete the classes in the execution set - we won't be able to extend them later.
            for (int i = 0; i < set.Classes.Count; i++)
            {
                var c = set.Classes[i];
                foreach (var m in c.TheType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    // Add all virtual members (the others are not assigned to classes in our metadata)
                    if (m.IsConstructor || m.IsVirtual || m.IsAbstract)
                    {
                        PrepareCodeInternal(set, m, null);
                    }
                    else
                    {
                        // Or if the method is implementing an interface
                        List<EquatableMethod> methodsBeingImplemented = new List<EquatableMethod>();
                        MicroCompiler.CollectBaseImplementations(set, m, methodsBeingImplemented);
                        if (methodsBeingImplemented.Any())
                        {
                            PrepareCodeInternal(set, m, null);
                        }
                    }
                }

                foreach (var m in c.TheType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    // Add all ctors
                    PrepareCodeInternal(set, m, null);
                }
            }
        }

        /// <summary>
        /// Complete the execution set by making sure all dependencies are resolved
        /// </summary>
        /// <param name="set">The <see cref="ExecutionSet"/> to complete</param>
        /// <param name="forKernel">True if a kernel shall be compiled (requires class completion, so the kernel classes can be finalized)</param>
        internal void FinalizeExecutionSet(ExecutionSet set, bool forKernel)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MicroCompiler));
            }

            if (forKernel)
            {
                CompleteClasses(set);
            }

            // Because the code below is still not water proof (there could have been virtual methods added only in the end), we do this twice
            for (int i = 0; i < 2; i++)
            {
                // Contains all classes traversed so far
                List<ClassDeclaration> declarations = new List<ClassDeclaration>(set.Classes);
                // Contains the new ones to be traversed this time (start with all)
                List<ClassDeclaration> newDeclarations = new List<ClassDeclaration>(declarations);
                while (true)
                {
                    // Sort: Interfaces first, then bases before their derived types (so that if a base rewires one virtual method to another - possibly abstract -
                    // method, the derived method's actual implementation is linked. I.e. IEqualityComparer.Equals(object,object) -> EqualityComparer.Equals(object, object) ->
                    // EqualityComparer<T>.Equals(T,T) -> -> GenericEqualityComparer<T>.Equals(T,T)
                    newDeclarations.Sort(new ClassDeclarationByInheritanceSorter());
                    DetectRequiredVirtualMethodImplementations(set, newDeclarations);

                    // Of all classes in the list, load their static cctors. This may also add new classes in turn
                    for (var j = 0; j < set.Classes.Count; j++)
                    {
                        var cls = set.Classes[j];
                        var cctor = cls.TheType.TypeInitializer;
                        if (cctor == null || cls.SuppressInit)
                        {
                            continue;
                        }

                        PrepareCodeInternal(set, cctor, null);
                    }

                    if (set.Classes.Count == declarations.Count)
                    {
                        break;
                    }

                    // Find the new ones
                    newDeclarations = new List<ClassDeclaration>();
                    foreach (var decl in set.Classes)
                    {
                        if (!declarations.Contains(decl))
                        {
                            newDeclarations.Add(decl);
                        }
                    }

                    declarations = new List<ClassDeclaration>(set.Classes);
                }
            }

            // The list of classes may contain both the original class (i.e. String) and its replacement (MiniString) with the same token and (hopefully) all else equal as well.
            // This happens for partially replaced classes. Remove the mini class again.
            for (int i = 0; i < set.Classes.Count; i++)
            {
                var cls = set.Classes[i];
                int idx;
                // Check whether this class is in the replacement list
                if ((idx = _replacementClasses.IndexOf(cls.TheType)) >= 0) // No need to test for the attribute
                {
                    var replacement = _replacementClasses[idx];
                    int tokenOfReplacement = set.GetOrAddClassToken(replacement.GetTypeInfo());
                    // If there is an element satisfying this condition, it is our original class
                    var orig = set.Classes.SingleOrDefault(x => x.NewToken == tokenOfReplacement && x.TheType != replacement);
                    if (orig == null)
                    {
                        continue;
                    }

                    // Remove this replacement
                    set.Classes.RemoveAt(i);
                    i--;
                }
            }

            set.RemoveUnusedDataFields();

            if (!forKernel)
            {
                PrepareStaticCtors(set);
                if (set.CompilerSettings.LaunchProgramFromFlash)
                {
                    Type t = typeof(ArduinoNativeHelpers);
                    var method = t.GetMethod("MainStub", BindingFlags.Static | BindingFlags.NonPublic)!;
                    PrepareCodeInternal(set, method, null);
                    int tokenOfStartupMethod = set.GetOrAddMethodToken(method, method);
                    set.TokenOfStartupMethod = tokenOfStartupMethod;
                }
            }

            _logger.LogInformation($"Estimated program memory usage: {set.EstimateRequiredMemory()} bytes.");
        }

        /// <summary>
        /// Orders classes by inheritance (interfaces and base classes before derived classes)
        /// </summary>
        internal class ClassDeclarationByInheritanceSorter : IComparer<ClassDeclaration>
        {
            /// <inheritdoc cref="IComparer{T}.Compare"/>
            public int Compare(ClassDeclaration? x, ClassDeclaration? y)
            {
                if (x == y)
                {
                    return 0;
                }

                // No nulls expected here
                Type xt = x!.TheType;
                Type yt = y!.TheType;

                if (xt.IsInterface && !yt.IsInterface)
                {
                    return -1;
                }

                if (!xt.IsInterface && yt.IsInterface)
                {
                    return 1;
                }

                if (xt.IsInterface && yt.IsInterface)
                {
                    return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
                }

                // Both x and y are not interfaces now (and not equal)
                if (xt.IsAssignableFrom(yt))
                {
                    return -1;
                }

                if (yt.IsAssignableFrom(xt))
                {
                    return 1;
                }

                return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Detects the required (potentially used) virtual methods in the execution set
        /// </summary>
        private void DetectRequiredVirtualMethodImplementations(ExecutionSet set, List<ClassDeclaration> declarations)
        {
            foreach (var a in set.ArrayListImplementation)
            {
                // this adds MiniArray.GetEnumerator(T[]) as implementation of T[].IList<T>()
                PrepareCodeInternal(set, a.Value, null);
                var m = set.GetMethod(a.Value);
                var arrayClass = set.Classes.Single(x => x.NewToken == (int)KnownTypeTokens.Array);
                if (arrayClass.Members.All(y => y.Method != a.Value))
                {
                    var interestingInterface = typeof(IEnumerable<>).MakeGenericType(a.Key);
                    var method = interestingInterface.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy) ?? throw new MissingMethodException(interestingInterface.Name, "GetEnumerator");
                    int interfaceMethodToken = set.GetOrAddMethodToken(method, m.MethodBase);
                    arrayClass.AddClassMember(new ClassMember(a.Value, VariableKind.Method, m.Token, new List<int>() { interfaceMethodToken }));
                }
            }

            for (var i = 0; i < declarations.Count; i++)
            {
                var cls = declarations[i];
                List<FieldInfo> fields = new List<FieldInfo>();
                List<MemberInfo> methods = new List<MemberInfo>();

                GetFields(cls.TheType, fields, methods);
                for (var index = 0; index < methods.Count; index++)
                {
                    var m = methods[index];
                    if (MemberLinkRequired(set, m, out var baseMethodInfos))
                    {
                        var mb = (MethodBase)m; // This cast must work

                        if (cls.Members.Any(x => x.Method == m))
                        {
                            continue;
                        }

                        // If this method is required because base implementations are called, we also need its implementation (obviously)
                        // Unfortunately, this can recursively require further classes and methods
                        PrepareCodeInternal(set, mb, null);

                        List<int> baseTokens = baseMethodInfos.Select(x => set.GetOrAddMethodToken(x, mb)).ToList();
                        cls.AddClassMember(new ClassMember(mb, VariableKind.Method, set.GetOrAddMethodToken(mb, mb), baseTokens));
                    }
                }
            }
        }

        /// <summary>
        /// Send all class declaration from from to to.
        /// </summary>
        /// <param name="set">Execution set</param>
        /// <param name="fromSnapShot">Elements to skip (already loaded)</param>
        /// <param name="toSnapShot">Elements to include (must be a superset of <paramref name="fromSnapShot"/>)</param>
        /// <param name="markAsReadOnly">Mark uploaded classes as readonly</param>
        internal void SendClassDeclarations(ExecutionSet set, ExecutionSet.SnapShot fromSnapShot, ExecutionSet.SnapShot toSnapShot, bool markAsReadOnly)
        {
            if (markAsReadOnly)
            {
                _logger.LogDebug("Now loading the kernel...");
            }
            else
            {
                _logger.LogDebug("Loading user program...");
            }

            int idx = 0;
            // Include all elements that are not in from but in to. Do not include elements in neither collection.
            var list = set.Classes.Where(x => !fromSnapShot.AlreadyAssignedTokens.Contains(x.NewToken) && toSnapShot.AlreadyAssignedTokens.Contains(x.NewToken));
            var classesToLoad = list.OrderBy(x => (uint)x.NewToken).ToList();
            foreach (var c in classesToLoad)
            {
                var cls = c.TheType;
                Int32 parentToken = 0;
                Type parent = cls.BaseType!;
                if (parent != null)
                {
                    parentToken = set.GetOrAddClassToken(parent.GetTypeInfo());
                }

                int token = set.GetOrAddClassToken(cls.GetTypeInfo());

                short classFlags = 0;
                if (cls.IsValueType)
                {
                    classFlags = 1;
                }

                if (cls.IsEnum)
                {
                    classFlags |= 2;
                }

                if (cls.IsArray)
                {
                    classFlags |= 4;
                }

                _logger.LogDebug($"Sending class {idx + 1} of {classesToLoad.Count}: Declaration for {cls.MemberInfoSignature()} (Token 0x{token:x8}). Number of members: {c.Members.Count}, Dynamic size {c.DynamicSize} Bytes, Static Size {c.StaticSize} Bytes.");
                _commandHandler.SendClassDeclaration(token, parentToken, (c.DynamicSize, c.StaticSize), classFlags, c.Members, c.Interfaces.Select(x => set.GetOrAddClassToken(x.GetTypeInfo())).ToArray());

                if (markAsReadOnly)
                {
                    c.ReadOnly = true;
                }

                idx++;
                // Need to repeatedly copy to flash, or a set that just fits into flash cannot be loaded since the total RAM size is much less than the total flash size
                if (set.CompilerSettings.DoCopyToFlash(markAsReadOnly) && (idx % 100 == 0))
                {
                    CopyToFlash();
                }
            }
        }

        internal void PrepareStringLoad(int constantSize, int stringSize)
        {
            _commandHandler.PrepareStringLoad(constantSize, stringSize);
        }

        internal void SendConstants(IList<(int Token, byte[] InitializerData, string StringData)> constElements, ExecutionSet.SnapShot fromSnapShot,
            ExecutionSet.SnapShot toSnapShot, bool markAsReadOnly)
        {
            var list = constElements.Where(x => !fromSnapShot.AlreadyAssignedTokens.Contains(x.Token) && toSnapShot.AlreadyAssignedTokens.Contains(x.Token));
            var uploadList = list.OrderBy(x => x.Token).ToList();
            int cnt = uploadList.Count;
            int idx = 1;
            foreach (var e in uploadList)
            {
                if (e.InitializerData == null)
                {
                    continue;
                }

                _logger.LogDebug($"Sending constant {idx}/{cnt}. Size {e.InitializerData.Length} bytes. Token 0x{e.Token:X8}.");
                _commandHandler.SendConstant(e.Token, e.InitializerData);
                idx++;
            }
        }

        internal void SendSpecialTypeList(IList<int> typeList, ExecutionSet.SnapShot fromSnapShot, ExecutionSet.SnapShot toSnapShot, bool forKernel)
        {
            // Counting the existing list elements should be enough here.
            var listToLoad = typeList.Skip(fromSnapShot.SpecialTypes.Count).Take(toSnapShot.SpecialTypes.Count - fromSnapShot.SpecialTypes.Count).ToList();
            _commandHandler.SendSpecialTypeList(listToLoad);
        }

        internal void SendGlobalMetadata(UInt32 staticRootVectorSize)
        {
            _commandHandler.SendGlobalMetadata(staticRootVectorSize);
        }

        internal void SendStrings(IList<(int Token, byte[] InitializerData, string StringData)> constElements, ExecutionSet.SnapShot fromSnapShot,
            ExecutionSet.SnapShot toSnapShot, bool markAsReadOnly)
        {
            var list = constElements.Where(x => !fromSnapShot.AlreadyAssignedStringTokens.Contains(x.Token) && toSnapShot.AlreadyAssignedStringTokens.Contains(x.Token));
            var uploadList = list.OrderBy(x => x.Token).ToList();
            int cnt = uploadList.Count;
            int idx = 1;
            foreach (var e in uploadList)
            {
                if (e.InitializerData == null)
                {
                    continue;
                }

                _logger.LogDebug($"Sending string {idx}/{cnt}. Size {e.InitializerData.Length} bytes: {e.StringData}");
                _commandHandler.SendConstant(e.Token, e.InitializerData);
                idx++;
            }
        }

        internal void SendMethods(ExecutionSet set, ExecutionSet.SnapShot fromSnapShot, ExecutionSet.SnapShot toSnapShot, bool markAsReadOnly)
        {
            // The flag is not currently required for methods, since they don't change
            if (markAsReadOnly)
            {
                _logger.LogDebug("Now loading kernel methods...");
            }
            else
            {
                _logger.LogDebug("Loading user program methods...");
            }

            var list = set.Methods().Where(x => !fromSnapShot.AlreadyAssignedTokens.Contains(x.Token) && toSnapShot.AlreadyAssignedTokens.Contains(x.Token));
            var uploadList = list.OrderBy(x => (uint)x.Token).ToList();
            int cnt = uploadList.Count;
            int idx = 0;
            foreach (var me in uploadList)
            {
                MethodBase methodInfo = me.MethodBase.Method;
                _logger.LogDebug($"Loading Method {idx + 1} of {cnt} (NewToken 0x{me.Token:X}), named {methodInfo.MethodSignature(false)}.");
                SendMethod(set, me);
                idx++;
                if (set.CompilerSettings.DoCopyToFlash(markAsReadOnly) && (idx % 100 == 0))
                {
                    CopyToFlash();
                }
            }
        }

        /// <summary>
        /// Detects whether the method must be known by the class declaration.
        /// This is used a) to find the class to construct from a newobj instruction (which provides the ctor token only)
        /// and b) to resolve virtual method calls on a concrete class.
        /// </summary>
        /// <param name="set">The current execution set</param>
        /// <param name="method">The method instance</param>
        /// <param name="methodsBeingImplemented">Returns the list of methods (from interfaces or base classes) that this method implements</param>
        /// <returns>True if the method shall be part of the class declaration</returns>
        private static bool MemberLinkRequired(ExecutionSet set, MemberInfo method, out List<EquatableMethod> methodsBeingImplemented)
        {
            methodsBeingImplemented = new List<EquatableMethod>();

            if (method is MethodInfo m)
            {
                // Static methods, do not need a link to the class (so far)
                if (m.IsStatic)
                {
                    return false;
                }

                // For ordinary methods, it gets more complicated.
                // We need to find out whether this method overrides some other method or implements an interface method
                if (m.IsAbstract)
                {
                    // An abstract method can never be called, so it is never the real call target of a callvirt instruction
                    return false;
                }

                CollectBaseImplementations(set, new EquatableMethod(m), methodsBeingImplemented);

                // We need the implementation if at least one base implementation is being called and is used
                return methodsBeingImplemented.Count > 0 && methodsBeingImplemented.Any(x => set.HasMethod(x, m, out _));
            }

            return false;
        }

        internal static void CollectBaseImplementations(ExecutionSet set, EquatableMethod method, List<EquatableMethod> methodsBeingImplemented)
        {
            Type? cls = method.DeclaringType?.BaseType;
            while (cls != null)
            {
                foreach (var candidate in cls.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    EquatableMethod equatableCandidate = new EquatableMethod(candidate);
                    if (EquatableMethod.IsOverriddenImplementation(equatableCandidate, method, false))
                    {
                        methodsBeingImplemented.Add(equatableCandidate);
                    }
                }

                cls = cls.BaseType;
            }

            cls = method.DeclaringType;
            if (cls == null)
            {
                return;
            }

            foreach (var interf in cls.GetInterfaces())
            {
                // If an interface is in the suppression list, don't use it for collecting dependencies
                if (set.IsSuppressed(interf))
                {
                    continue;
                }

                foreach (var candidate in interf.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    EquatableMethod equatableCandidate = new EquatableMethod(candidate);
                    if (EquatableMethod.IsOverriddenImplementation(equatableCandidate, method, true))
                    {
                        methodsBeingImplemented.Add(equatableCandidate);
                    }
                }
            }
        }

        private static void GetFields(Type classType, List<FieldInfo> fields, List<MemberInfo> methods)
        {
            foreach (var m in classType.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m is FieldInfo field)
                {
                    fields.Add(field);
                }
                else if (m is MethodInfo method)
                {
                    methods.Add(method);
                }
                else if (m is ConstructorInfo ctor)
                {
                    methods.Add(ctor);
                }
            }
        }

        private static int SizeOfVoidPointer()
        {
            return 4;
        }

        /// <summary>
        /// Calculates the size of the class instance in bytes, excluding the management information (such as the vtable)
        /// </summary>
        /// <param name="classType">The class type</param>
        /// <returns>A tuple with the size of an instance and the size of the static part of the class</returns>
        private (int Dynamic, int Statics) GetClassSize(Type classType)
        {
            List<FieldInfo> fields = new List<FieldInfo>();
            List<MemberInfo> methods = new List<MemberInfo>();
            GetFields(classType, fields, methods);
            int minSizeOfMember = StructAlignment(classType, fields);

            int sizeDynamic = 0;
            int sizeStatic = 0;
            int numberOfNonStaticFields = fields.Count(x => x.IsStatic == false);
            foreach (var f in fields)
            {
                GetVariableType(f.FieldType, minSizeOfMember, out int sizeOfMember);

                if (f.IsStatic)
                {
                    if (classType.IsValueType)
                    {
                        sizeStatic += sizeOfMember;
                    }
                    else
                    {
                        sizeStatic += (sizeOfMember <= 4) ? 4 : 8;
                    }
                }
                else
                {
                    if (classType.IsValueType)
                    {
                        if (numberOfNonStaticFields > 1)
                        {
                            // If this is a value type with more than one field, add alignment, as with classes
                            sizeDynamic += sizeOfMember <= minSizeOfMember ? minSizeOfMember : sizeOfMember;
                        }
                        else
                        {
                            sizeDynamic += sizeOfMember;
                        }
                    }
                    else if (f.FieldType.IsValueType)
                    {
                        // Storing a value type field in a (non-value-type) class shall use the value size rounded up to 4 or 8
                        if (sizeOfMember <= 4)
                        {
                            sizeDynamic += 4;
                        }
                        else
                        {
                            if (sizeOfMember % 8 != 0)
                            {
                                sizeOfMember = (sizeOfMember + 8) & ~0x7;
                            }

                            sizeDynamic += sizeOfMember;
                        }
                    }
                    else
                    {
                        sizeDynamic += (sizeOfMember <= 4) ? 4 : 8;
                    }
                }
            }

            if (classType.BaseType != null)
            {
                var baseSizes = GetClassSize(classType.BaseType);
                // Static sizes are not inherited (but do we need to care about accessing a static field via a derived class?)
                sizeDynamic += baseSizes.Dynamic;
            }

            return (sizeDynamic, sizeStatic);
        }

        private bool AddClassDependency(HashSet<Type> allTypes, Type newType)
        {
            return allTypes.Add(newType);
        }

        /// <summary>
        /// Calculates the transitive hull of all types we need to instantiate this class and run its methods
        /// This can be a lengthy list!
        /// </summary>
        private void DetermineBaseAndMembers(HashSet<Type> allTypesToLoad, Type classType)
        {
            if (classType.BaseType != null)
            {
                if (AddClassDependency(allTypesToLoad, classType.BaseType))
                {
                    DetermineBaseAndMembers(allTypesToLoad, classType.BaseType);
                }
            }

            foreach (var t in classType.GetInterfaces())
            {
                if (AddClassDependency(allTypesToLoad, t))
                {
                    DetermineBaseAndMembers(allTypesToLoad, t);
                }
            }

            // This causes a lot of classes to be added, but we'll probably not need them - unless any of their ctors is in the call chain
            // This is detected separately.
            // Maybe we still need any value types involved, though
            ////foreach (var m in classType.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
            ////{
            ////    if (m is FieldInfo field)
            ////    {
            ////        if (AddClassDependency(allTypesToLoad, field.FieldType))
            ////        {
            ////            DetermineBaseAndMembers(allTypesToLoad, field.FieldType);
            ////        }
            ////    }
            ////}
        }

        private int StructAlignment(Type t, List<FieldInfo> fields)
        {
            int minSizeOfMember = 1;
            // Structs with reference type need to be aligned for the GC to probe any embedded addresses
            if (t.IsValueType && fields.Any(x => !x.FieldType.IsValueType))
            {
                minSizeOfMember = 4;
            }

            // Structs with multiple fields need to be aligned, too. Or we might do an unaligned memory access.
            // TODO: This should eventually check for StructAlignmentAttribute, but then unaligned access requires support from the backend
            else if (t.IsValueType && fields.Count(x => !x.IsStatic) > 1)
            {
                minSizeOfMember = 4;
            }

            return minSizeOfMember;
        }

        /// <summary>
        /// Returns the type of a variable for the IL. This merely distinguishes signed from unsigned types, since
        /// the execution stack auto-extends smaller types.
        /// </summary>
        /// <param name="t">Type to query</param>
        /// <param name="minSizeOfMember">Minimum size of the member (used to force alignment)</param>
        /// <param name="sizeOfMember">Returns the actual size of the member, used for value-type arrays (because byte[] should use just one byte per entry)</param>
        /// <returns></returns>
        internal static VariableKind GetVariableType(Type t, int minSizeOfMember, out int sizeOfMember)
        {
            string name = t.Name;

            if (t.IsPointer)
            {
                sizeOfMember = SizeOfVoidPointer();
                return VariableKind.AddressOfVariable;
            }

            if (t == typeof(sbyte))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 1);
                return VariableKind.Int32;
            }

            if (t == typeof(Int32))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 4);
                return VariableKind.Int32;
            }

            if (t == typeof(UInt32))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 4);
                return VariableKind.Uint32;
            }

            if (t == typeof(Int16))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 2);
                return VariableKind.Int32;
            }

            if (t == typeof(UInt16))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 2);
                return VariableKind.Uint32;
            }

            if (t == typeof(Char))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 2);
                return VariableKind.Uint32;
            }

            if (t == typeof(byte))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 1);
                return VariableKind.Uint32;
            }

            if (t == typeof(bool))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 1);
                return VariableKind.Boolean;
            }

            if (t == typeof(Int64))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 8);
                return VariableKind.Int64;
            }

            if (t == typeof(UInt64))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 8);
                return VariableKind.Uint64;
            }

            if (t == typeof(float))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 4);
                return VariableKind.Float;
            }

            if (t == typeof(double))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 8);
                return VariableKind.Double;
            }

            if (t == typeof(DateTime) || t == typeof(TimeSpan))
            {
                sizeOfMember = Math.Max(minSizeOfMember, 8);
                return VariableKind.Uint64;
            }

            if (t.IsArray)
            {
                var elemType = t.GetElementType();
                if (elemType!.IsValueType)
                {
                    GetVariableType(elemType, minSizeOfMember, out sizeOfMember);
                    return VariableKind.ValueArray;
                }
                else
                {
                    sizeOfMember = SizeOfVoidPointer();
                    return VariableKind.ReferenceArray;
                }
            }

            if (t.IsEnum)
            {
                sizeOfMember = Math.Max(minSizeOfMember, 4);
                return VariableKind.Uint32;
            }

            if (t.IsValueType && !t.IsGenericTypeParameter)
            {
                if (t.IsGenericType)
                {
                    // There are a few special types for which CreateInstance always throws an exception
                    var openType = t.GetGenericTypeDefinition();
                    if (openType.Name.StartsWith("ByReference", StringComparison.Ordinal))
                    {
                        sizeOfMember = Math.Max(minSizeOfMember, 4);
                        return VariableKind.Reference;
                    }

                    if (openType == typeof(Span<>))
                    {
                        // Normally, this lives on the stack only. But if it lives within another struct, it uses 8 bytes
                        sizeOfMember = SizeOfVoidPointer() + sizeof(Int32);
                        return VariableKind.LargeValueType;
                    }
                }

                // Calculate class size (Note: Can't use GetClassSize here, as this would be recursive)
                sizeOfMember = 0;
                // If this attribute is given, use its size property (which is applied ie for empty structures)
                var attrib = t.StructLayoutAttribute;
                int attribSize = 0;
                if (attrib != null && attrib.Size > 0)
                {
                    // Minimum size is 4
                    attribSize = Math.Max(attrib.Size, 4);
                }

                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) // Not the static ones
                {
                    GetVariableType(f.FieldType, minSizeOfMember, out var s);
                    sizeOfMember += s;
                }

                // If the StructLayoutAttribute gives a bigger size than the field combination, use that one. It seems sometimes the field combination is bigger, maybe due to some unioning, but
                // that feature is not supported yet.
                if (attribSize > sizeOfMember)
                {
                    sizeOfMember = attribSize;
                }

                if (sizeOfMember <= 4)
                {
                    sizeOfMember = Math.Max(minSizeOfMember, 4);
                    return VariableKind.Uint32;
                }

                if (sizeOfMember <= 8)
                {
                    sizeOfMember = Math.Max(minSizeOfMember, 8);
                    return VariableKind.Uint64;
                }
                else
                {
                    // Round up to next 4 bytes
                    if ((sizeOfMember & 4) != 0)
                    {
                        sizeOfMember += 3;
                        sizeOfMember = sizeOfMember & ~0x3;
                    }

                    return VariableKind.LargeValueType;
                }
            }

            sizeOfMember = SizeOfVoidPointer();
            return VariableKind.Object;
        }

        internal static bool HasIntrinsicAttribute(EquatableMethod method)
        {
            var attribute = Type.GetType("System.Runtime.CompilerServices.IntrinsicAttribute", true)!;
            var attribs = method.GetCustomAttributes(attribute);
            return attribs.Any();
        }

        internal static bool HasReplacementAttribute(Type type,
#if NET5_0_OR_GREATER
            [NotNullWhen(true)]
#endif
            out ArduinoReplacementAttribute attribute)
        {
            var repl = type.GetCustomAttribute<ArduinoReplacementAttribute>();
            if (repl != null)
            {
                attribute = repl;
                return true;
            }

            attribute = null!;
            return false;
        }

        internal void CollectDependendentMethods(ExecutionSet set, EquatableMethod methodInfo, IlCode? code, HashSet<EquatableMethod> newMethods)
        {
            if (methodInfo.IsAbstract)
            {
                // This is a method that will never be called directly, so we can safely skip it.
                // There won't be code for it, anyway.
                return;
            }

            // If this is true, we don't have to parse the implementation
            if (EquatableMethod.HasArduinoImplementationAttribute(methodInfo, out var attrib) && attrib!.MethodNumber != 0)
            {
                return;
            }

            if (code == null)
            {
                code = IlCodeParser.FindAndPatchTokens(set, methodInfo.Method);
            }

            foreach (var method in code.DependentMethods)
            {
                // Do we need to replace this method?
                var replacementType = set.GetReplacement(method.DeclaringType);
                EquatableMethod? finalMethod = null;
                if (replacementType != null)
                {
                    finalMethod = set.GetReplacement(method, methodInfo, replacementType);
                }
                else
                {
                    finalMethod = set.GetReplacement(method, methodInfo);
                }

                if (finalMethod == null)
                {
                    finalMethod = method;
                }

                if (finalMethod.Method is MethodInfo me)
                {
                    // Ensure we're not scanning the same implementation twice, as this would result
                    // in a stack overflow when a method is recursive (even indirect)
                    if (!set.HasMethod(me, methodInfo, out var code1) && newMethods.Add(me))
                    {
                        CollectDependendentMethods(set, me, code1, newMethods);
                    }
                }
                else if (finalMethod.Method is ConstructorInfo co)
                {
                    if (!set.HasMethod(co, methodInfo, out var code2) && newMethods.Add(co))
                    {
                        CollectDependendentMethods(set, co, code2, newMethods);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Token {method} is not a valid method.");
                }
            }

            /*
            // TODO: Something is very fishy about these... check later.
            // We see in EqualityComparer<T>::get_Default the token 0x0a000a50, but ILDasm says it is 0A000858. None of them
            // match the class field, which is 04001895. Is the mess because this is a static field of a generic type?
            // Similarly, the field tokens we see in the HashTable`1 ctor do not match the class definitions
            foreach (var s in fieldsRequired)
            {
                var resolved = ResolveMember(methodInfo, s);
                if (resolved == null)
                {
                    // Warning only, since might be an incorrect match
                    _board.Log($"Unable to resolve token {s}.");
                    continue;
                }
            }
            */
        }

        public ArduinoTask GetTask(ExecutionSet set, EquatableMethod methodInfo)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MicroCompiler));
            }

            if (set.HasMethod(methodInfo, methodInfo, out _))
            {
                unchecked
                {
                    var tsk = new ArduinoTask(this, set.GetMethod(methodInfo), (short)_activeTasks.Count);
                    lock (_activeTasksLock)
                    {
                        _activeTasks.Add(tsk);
                    }

                    return tsk;
                }
            }

            throw new InvalidOperationException($"Method {methodInfo} not loaded");
        }

        public ExecutionSet PrepareProgram(MethodInfo mainEntryPoint, CompilerSettings compilerSettings)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MicroCompiler));
            }

            if (!mainEntryPoint.IsStatic)
            {
                throw new InvalidOperationException("Main entry point must be a static method");
            }

            if (mainEntryPoint.IsConstructedGenericMethod)
            {
                throw new InvalidOperationException("Main entry point must not be a generic method");
            }

            if (compilerSettings.LaunchProgramFromFlash)
            {
                var parameters = mainEntryPoint.GetParameters();
                if (parameters.Length > 1 && parameters[0].GetType() != typeof(string[]))
                {
                    // Expect the main entry point to have either no arguments or an argument of type string[] (as the default main methods do)
                    throw new InvalidOperationException("To launch a program directly from flash, the main entry point must take 0 arguments or 1 argument of type string[]");
                }
            }

            ExecutionSet exec;

            if (ExecutionSet.CompiledKernel == null || ExecutionSet.CompiledKernel.CompilerSettings != compilerSettings)
            {
                exec = new ExecutionSet(this, compilerSettings);
                // We never want these types in our execution set - reflection is not supported, except in very specific cases
                exec.SuppressType("System.Reflection.MethodBase");
                exec.SuppressType("System.Reflection.MethodInfo");
                exec.SuppressType("System.Reflection.ConstructorInfo");
                exec.SuppressType("System.Reflection.Module");
                exec.SuppressType("System.Reflection.Assembly");
                exec.SuppressType("System.Reflection.RuntimeAssembly");
                exec.SuppressType("System.Globalization.HebrewNumber");
                exec.SuppressType("System.Resources.RuntimeResourceSet");
                exec.SuppressType("System.Resources.ResourceReader");
                // exec.SuppressNamespace("System.Runtime.Intrinsics", true);
                // Native libraries are not supported
                exec.SuppressType(typeof(System.Runtime.InteropServices.NativeLibrary));

                // Only the invariant culture is supported (we might later change this to "only one culture is supported", and
                // upload the strings matching a specific culture)
                exec.SuppressType(typeof(System.Globalization.HebrewCalendar));
                exec.SuppressType(typeof(System.Globalization.JapaneseCalendar));
                exec.SuppressType(typeof(System.Globalization.JapaneseLunisolarCalendar));
                exec.SuppressType(typeof(System.Globalization.ChineseLunisolarCalendar));
                exec.SuppressType(typeof(IDeserializationCallback));
                exec.SuppressType(typeof(IConvertible)); // Remove support for this rarely used interface which links many methods (i.e. on String)
                exec.SuppressType(typeof(OutOfMemoryException)); // For the few cases, where this is explicitly called, we don't need to keep it - it's quite fatal, anyway.
                exec.SuppressType(typeof(Microsoft.Win32.Registry));
                exec.SuppressType(typeof(Microsoft.Win32.RegistryKey));
                // These shall never be loaded - they're host only (but might slip into the execution set when the startup code is referencing them)
                exec.SuppressType(typeof(MicroCompiler));
                exec.SuppressType(typeof(System.Device.Gpio.Drivers.LibGpiodDriver));
                exec.SuppressType(typeof(System.Device.Gpio.Drivers.RaspberryPi3Driver));
                exec.SuppressType(typeof(System.Device.Gpio.Drivers.UnixDriver));
                exec.SuppressType(typeof(System.Device.Gpio.Drivers.Windows10Driver));

                // We don't currently support threads or tasks
                // exec.SuppressType(typeof(System.Threading.Tasks.Task));
                // exec.SuppressType(typeof(System.Threading.Tasks.ValueTask));
                // exec.SuppressType("System.Threading.Tasks.ThreadPoolTaskScheduler");

                // Can't afford to load these, at least not on the Arduino Due. They're way to big.
                exec.SuppressType(typeof(UnitsNet.QuantityFormatter));
                exec.SuppressType(typeof(UnitsNet.UnitAbbreviationsCache));

                foreach (string compilerSettingsAdditionalSuppression in compilerSettings.AdditionalSuppressions)
                {
                    exec.SuppressType(compilerSettingsAdditionalSuppression);
                }

                //// exec.SuppressType("System.Runtime.Serialization.SerializationInfo"); // Serialization is not currently supported

                PrepareLowLevelInterface(exec);
                if (compilerSettings.CreateKernelForFlashing)
                {
                    // Clone the kernel and save as static member
                    ExecutionSet.CompiledKernel = new ExecutionSet(exec, this, compilerSettings);
                }
                else
                {
                    ExecutionSet.CompiledKernel = null;
                }
            }
            else
            {
                // Another clone, to leave the static member alone. Replace the compiler in that kernel with the current one.
                exec = new ExecutionSet(ExecutionSet.CompiledKernel, this, compilerSettings);
            }

            if (mainEntryPoint.DeclaringType != null)
            {
                PrepareClass(exec, mainEntryPoint.DeclaringType);
            }

            PrepareCodeInternal(exec, mainEntryPoint, null);

            exec.MainEntryPointMethod = mainEntryPoint;
            FinalizeExecutionSet(exec, false);
            return exec;
        }

        /// <summary>
        /// Creates and loads an execution set (a program to be executed on a remote microcontroller)
        /// </summary>
        /// <typeparam name="T">The type of the main entry method. Typically something like <code>Func{int, int, int}</code></typeparam>
        /// <param name="mainEntryPoint">The main entry method for the program</param>
        /// <returns>The execution set. Use it's <see cref="ExecutionSet.MainEntryPoint"/> property to get a callable reference to the remote code.</returns>
        /// <exception cref="Exception">This may throw exceptions in case the execution of some required static constructors (type initializers) fails.</exception>
        public ExecutionSet PrepareAndRunExecutionSet<T>(T mainEntryPoint)
            where T : Delegate
        {
            return PrepareAndRunExecutionSet(mainEntryPoint, new CompilerSettings());
        }

        /// <summary>
        /// Creates and loads an execution set (a program to be executed on a remote microcontroller)
        /// </summary>
        /// <typeparam name="T">The type of the main entry method. Typically something like <code>Func{int, int, int}</code></typeparam>
        /// <param name="mainEntryPoint">The main entry method for the program</param>
        /// <param name="settings">Custom compiler settings</param>
        /// <returns>The execution set. Use it's <see cref="ExecutionSet.MainEntryPoint"/> property to get a callable reference to the remote code.</returns>
        /// <exception cref="Exception">This may throw exceptions in case the execution of some required static constructors (type initializers) fails.</exception>
        public ExecutionSet PrepareAndRunExecutionSet<T>(T mainEntryPoint, CompilerSettings? settings)
        where T : Delegate
        {
            var exec = PrepareProgram(mainEntryPoint.Method, settings ?? new CompilerSettings());
            try
            {
                exec.Load(true);
            }
            catch (Exception x)
            {
                ClearAllData(true);
                _logger.LogError(x, $"Error loading program: {x.Message}");
                if (x.InnerException != null)
                {
                    _logger.LogError(x.InnerException, $"Inner exception: {x.InnerException.Message}");
                }

                throw;
            }

            return exec;
        }

        /// <summary>
        /// Creates and loads an execution set (a program to be executed on a remote microcontroller).
        /// </summary>
        /// <param name="mainEntryPoint">The main entry method for the program</param>
        /// <param name="settings">Custom compiler settings</param>
        /// <returns>The execution set. Use it's <see cref="ExecutionSet.MainEntryPoint"/> property to get a callable reference to the remote code.</returns>
        /// <exception cref="Exception">This may throw exceptions in case the execution of some required static constructors (type initializers) fails.</exception>
        public ExecutionSet PrepareAndRunExecutionSet(MethodInfo mainEntryPoint, CompilerSettings settings)
        {
            var exec = PrepareProgram(mainEntryPoint, settings);
            try
            {
                exec.Load(true);
            }
            catch (Exception)
            {
                ClearAllData(true);
                throw;
            }

            return exec;
        }

        internal void PrepareCodeInternal(ExecutionSet set, EquatableMethod methodInfo, ArduinoMethodDeclaration? parent)
        {
            // Ensure the class is known, if it needs replacement
            var classReplacement = set.GetReplacement(methodInfo.DeclaringType);
            EquatableMethod? replacement = set.GetReplacement(methodInfo, methodInfo);
            EquatableMethod parentMethod = parent == null ? methodInfo : parent.MethodBase;
            if (classReplacement != null && replacement == null)
            {
                // See below, this is the fix for it
                replacement = set.GetReplacement(methodInfo, parentMethod.Method, classReplacement);
            }

            if (replacement != null)
            {
                methodInfo = replacement;
            }

            if (set.HasMethod(methodInfo, parentMethod, out _))
            {
                return;
            }

            if (classReplacement != null && replacement == null)
            {
                // If the class requires full replacement, all methods must be replaced (or throw an error inside GetReplacement, if it is not defined), but it must
                // never return null. Seen during development, because generic parameter types did not match.
                throw new InvalidOperationException($"Internal error: The class {classReplacement} should fully replace {methodInfo.DeclaringType}, however method {methodInfo} has no replacement (and no error either)");
            }

            if (EquatableMethod.HasArduinoImplementationAttribute(methodInfo, out var implementation) && implementation!.MethodNumber != 0)
            {
                int tk1 = set.GetOrAddMethodToken(methodInfo, parentMethod);
                var newInfo1 = new ArduinoMethodDeclaration(tk1, methodInfo, parent, MethodFlags.SpecialMethod, implementation!.MethodNumber);
                set.AddMethod(newInfo1);
                return;
            }

            if (HasIntrinsicAttribute(methodInfo))
            {
                // If the method is marked with [Intrinsic] (an internal attribute supporting the JIT compiler), we need to check whether it requires special handling as well.
                // We cannot use the normal replacement technique with generic classes such as ByReference<T>, because Type.GetType doesn't allow open generic classes.
                if (methodInfo.Name == ".ctor" && methodInfo.DeclaringType!.Name == "ByReference`1")
                {
                    int tk1 = set.GetOrAddMethodToken(methodInfo, methodInfo);
                    var newInfo1 = new ArduinoMethodDeclaration(tk1, methodInfo, parent, MethodFlags.SpecialMethod, ArduinoImplementationAttribute.GetStaticHashCode("ByReferenceCtor"));
                    set.AddMethod(newInfo1);
                    return;
                }

                if (methodInfo.Name == "get_Value" && methodInfo.DeclaringType!.Name == "ByReference`1")
                {
                    int tk1 = set.GetOrAddMethodToken(methodInfo, methodInfo);
                    var newInfo1 = new ArduinoMethodDeclaration(tk1, methodInfo, parent, MethodFlags.SpecialMethod, ArduinoImplementationAttribute.GetStaticHashCode("ByReferenceValue"));
                    set.AddMethod(newInfo1);
                    return;
                }
            }

            var body = methodInfo.GetMethodBody();
            bool hasBody = !methodInfo.IsAbstract;

            var ilBytes = body?.GetILAsByteArray()!.ToArray();
            IlCode parserResult;

            bool constructedCode = false;
            bool needsParsing = true;
            MethodFlags constructedFlags = MethodFlags.None;
            List<FieldInfo> manuallyReferencedFields = new List<FieldInfo>();
            if (body == null && !methodInfo.IsAbstract)
            {
                Type multicastType = typeof(MulticastDelegate);
                if (multicastType.IsAssignableFrom(methodInfo.DeclaringType))
                {
                    // The compiler inserts auto-generated code for the methods of the specific delegate.
                    // We generate this code here.
                    hasBody = true;
                    if (methodInfo.IsConstructor)
                    {
                        // find the matching constructor in MulticastDelegate. Actually, we're not using a real constructor, but a method that acts on behalf of it
                        var methods = multicastType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var baseCtor = methods.Single(x => x.Name == "CtorClosedStatic"); // Implementation is same for static and instance, except for a null test

                        // Make sure this stub method is in memory
                        PrepareCodeInternal(set, baseCtor, parent);
                        // Directly use the new token (the baseCtor.Token cannot be resolved further down, because it belongs to another assembly)
                        int token = set.GetOrAddMethodToken(baseCtor, methodInfo);
                        needsParsing = false;

                        // the code we need to generate is
                        // LDARG.0
                        // LDARG.1
                        // LDARG.2
                        // CALL MulticastDelegate.baseCtor // with the original ctor token!
                        // RET
                        byte[] code = new byte[]
                        {
                            02, // LDARG.0
                            03, // LDARG.1
                            04, // LDARG.2
                            0x28, // CALL
                            (byte)(token & 0xFF),
                            (byte)((token >> 8) & 0xFF),
                            (byte)((token >> 16) & 0xFF),
                            (byte)((token >> 24) & 0xFF),
                            0x2A, // RET
                        };
                        ilBytes = code;
                        constructedCode = true;
                        constructedFlags = MethodFlags.Ctor;
                    }
                    else
                    {
                        var args = methodInfo.GetParameters();
                        Type t = methodInfo.DeclaringType!;
                        var methodDetail = (MethodInfo)methodInfo.Method;
                        var targetField = t.GetField("_target", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                        var methodPtrField = t.GetField("_methodPtr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

                        manuallyReferencedFields.Add(targetField);
                        manuallyReferencedFields.Add(methodPtrField);

                        List<byte> code = new List<byte>();
                        int numargs = args.Length;
                        if (methodInfo.IsStatic)
                        {
                            throw new InvalidOperationException("The Invoke() method of a delegate cannot be static");
                        }

                        code.Add((byte)OpCode.CEE_LDARG_0); // This is the this pointer of the delegate. We need to get its _target and _methodPtr references

                        // Leaves the target object on the stack (null for static methods). We'll have to decide in the EE whether we need it or not (meaning whether
                        // the actual target is static or not)
                        int fieldToken = set.GetOrAddFieldToken(targetField);
                        AddCallWithToken(code, OpCode.CEE_LDFLD, fieldToken);

                        // Push all remaining arguments to the stack -> they'll be the arguments to the method
                        for (int i = 0; i < numargs; i++)
                        {
                            code.Add((byte)OpCode.CEE_LDARG_S);
                            code.Add((byte)(i + 1));
                        }

                        code.Add((byte)OpCode.CEE_LDARG_0);

                        // Leaves the target (of type method ptr) on the stack. This shall be the final argument to the calli instruction
                        int methodFieldToken = set.GetOrAddFieldToken(methodPtrField);
                        AddCallWithToken(code, OpCode.CEE_LDFLD, methodFieldToken);

                        AddCallWithToken(code, OpCode.CEE_CALLI, 0); // The argument is irrelevant, the EE knows the calling convention to the target method, and we hope it matches

                        code.Add((byte)OpCode.CEE_RET);
                        ilBytes = code.ToArray();
                        constructedCode = true;
                        constructedFlags = MethodFlags.Virtual;
                        if (methodDetail.ReturnType == typeof(void))
                        {
                            constructedFlags |= MethodFlags.Void;
                        }

                        needsParsing = false; // We have already translated the tokens

                        if (t.BaseType != null) // Should not really be the case here, ever.
                        {
                            if (!set.HasDefinition(t.BaseType))
                            {
                                PrepareClass(set, t.BaseType);
                            }
                        }
                    }
                }
                else
                {
                    // TODO: There are a bunch of methods currently getting here because they're not implemented
                    // throw new NotSupportedException($"{methodInfo.MethodSignature()} has no visible implementation, but is required");
                    _logger.LogWarning($"{methodInfo.MethodSignature()} has no visible implementation");
                    return;
                }
            }

            if (methodInfo.Name == "MainStub" && methodInfo.DeclaringType == typeof(ArduinoNativeHelpers))
            {
                // Assemble the startup code for our program. This shall contain a call to all static initializers and finally a call to the
                // original main method.
                constructedFlags = MethodFlags.Void | MethodFlags.Static;
                constructedCode = true;
                int token;
                needsParsing = false; // We insert already translated tokens (because the methods we call come from all possible places, the Resolve would otherwise fail)
                List<byte> code = new List<byte>();
                foreach (var m in set.FirmwareStartupSequence!)
                {
                    // Use patched tokens
                    token = set.GetOrAddMethodToken(m.Method, parentMethod);
                    AddCallWithToken(code, OpCode.CEE_CALL, token);
                }

                var mainMethod = set.MainEntryPointMethod!;
                // This method must have 0 or 1 arguments (tested at the very beginning of the compiler run)
                if (mainMethod.GetParameters().Length == 1)
                {
                    // the only argument is of type string[]. Create an empty array.
                    AddCommand(code, OpCode.CEE_LDC_I4_0);
                    token = set.GetOrAddClassToken(typeof(string[]).GetTypeInfo());
                    AddCallWithToken(code, OpCode.CEE_NEWARR, token);
                }

                token = set.GetOrAddMethodToken(mainMethod, parentMethod);
                AddCallWithToken(code, OpCode.CEE_CALL, token);

                if (mainMethod.ReturnType != typeof(void))
                {
                    // discard return value, if any
                    AddCommand(code, OpCode.CEE_POP);
                }

                AddCommand(code, OpCode.CEE_RET);
                ilBytes = code.ToArray();
            }

            if (ilBytes == null && hasBody)
            {
                throw new MissingMethodException($"{methodInfo.MethodSignature()} has no visible implementation but a body");
            }

            if (ilBytes != null && ilBytes.Length > Math.Pow(2, 14) - 1)
            {
                throw new InvalidProgramException($"Max IL size of real time method is 2^14 Bytes. Actual size is {ilBytes.Length}.");
            }

            if (needsParsing == false)
            {
                parserResult = new IlCode(methodInfo, ilBytes, new List<MethodBase>(), manuallyReferencedFields, new List<TypeInfo>(), new List<ExceptionClause>());
            }
            else if (hasBody)
            {
                parserResult = IlCodeParser.FindAndPatchTokens(set, methodInfo, ilBytes!);

                foreach (var type in parserResult.DependentTypes)
                {
                    if (!set.HasDefinition(type))
                    {
                        PrepareClass(set, type);
                    }
                }
            }
            else
            {
                parserResult = new IlCode(methodInfo, null);
            }

            int tk = set.GetOrAddMethodToken(methodInfo, parentMethod);

            ArduinoMethodDeclaration newInfo;
            if (constructedCode)
            {
                newInfo = new ArduinoMethodDeclaration(tk, methodInfo, parent, constructedFlags, 0, Math.Max(8, methodInfo.GetParameters().Length + 3), parserResult);
            }
            else
            {
                newInfo = new ArduinoMethodDeclaration(tk, methodInfo, parent, parserResult);
            }

            if (set.AddMethod(newInfo))
            {
                // If the class containing this method contains statics, we need to send its declaration
                // TODO: Parse code to check for LDSFLD or STSFLD instructions and skip if none found.
                if (methodInfo.DeclaringType != null && GetClassSize(methodInfo.DeclaringType).Statics > 0)
                {
                    PrepareClass(set, methodInfo.DeclaringType);
                }

                // TODO: Change to dictionary and transfer IlCode object to correct place (it's evaluated inside, but discarded there)
                HashSet<EquatableMethod> methods = new HashSet<EquatableMethod>();
                CollectDependendentMethods(set, methodInfo, parserResult, methods);

                var list = methods.ToList();
                for (var index = 0; index < list.Count; index++)
                {
                    var dep = list[index];
                    // If we have a ctor in the call chain we need to ensure we have its class loaded.
                    // This happens if the created object is only used in local variables but not as a class member
                    // seen so far.
                    if (dep.IsConstructor && dep.DeclaringType != null)
                    {
                        PrepareClass(set, dep.DeclaringType);
                    }
                    else if (dep.DeclaringType != null && HasStaticFields(dep.DeclaringType))
                    {
                        // Also load the class declaration if it contains static fields.
                        // TODO: We currently assume that no class is accessing static fields of another class.
                        PrepareClass(set, dep.DeclaringType);
                    }

                    PrepareCodeInternal(set, dep, newInfo);
                }
            }
        }

        private void AddCallWithToken(List<byte> code, OpCode opCode, int token)
        {
            AddCommand(code, opCode);
            code.Add((byte)(token & 0xFF));
            code.Add((byte)((token >> 8) & 0xFF));
            code.Add((byte)((token >> 16) & 0xFF));
            code.Add((byte)((token >> 24) & 0xFF));
        }

        private void AddCommand(List<byte> code, OpCode opCode)
        {
            if ((int)opCode < 0x100)
            {
                code.Add((byte)opCode);
            }
            else
            {
                code.Add(254);
                code.Add((byte)opCode);
            }
        }

        private void SendMethod(ExecutionSet set, ArduinoMethodDeclaration decl)
        {
            ClassMember[] localTypes = new ClassMember[decl.MaxLocals];
            var body = decl.MethodBase.GetMethodBody();
            int i;
            // This is null in case of a method without implementation (an interface or abstract method). In this case, there are no locals, either
            if (body != null)
            {
                for (i = 0; i < decl.MaxLocals; i++)
                {
                    var classType = body.LocalVariables[i].LocalType;
                    // This also needs alignment, because "classType" might be a long value type
                    var type = GetVariableType(classType, SizeOfVoidPointer(), out int size);
                    ClassMember local = new ClassMember($"Local #{i}", type, 0, (ushort)size);
                    localTypes[i] = local;
                }
            }

            ClassMember[] argTypes = new ClassMember[decl.ArgumentCount];
            int startOffset = 0;
            // If the method is not static, the fist argument is the "this" pointer, which is not explicitly mentioned in the parameter list. It is of type object
            // for reference types and usually of type reference for value types (but depends whether the method is virtual or not, analyzation of these cases is underway)
            if ((decl.MethodBase.CallingConvention & CallingConventions.HasThis) != 0)
            {
                startOffset = 1;
                argTypes[0] = new ClassMember($"Argument 0: this", VariableKind.Object, 0, 4);
            }

            var parameters = decl.MethodBase.GetParameters();
            for (i = startOffset; i < decl.ArgumentCount; i++)
            {
                var classType = parameters[i - startOffset].ParameterType;
                var type = GetVariableType(classType, SizeOfVoidPointer(), out var size);
                ClassMember arg = new ClassMember($"Argument {i}", type, 0, size);
                argTypes[i] = arg;
            }

            _commandHandler.SendMethod(decl, localTypes, argTypes);
        }

        internal void PrepareStaticCtors(ExecutionSet set)
        {
            List<ClassDeclaration> classes = set.Classes.Where(x => !x.SuppressInit && x.TheType.TypeInitializer != null).ToList();
            List<IlCode> codeSequences = new List<IlCode>();
            for (var index = 0; index < classes.Count; index++)
            {
                ClassDeclaration? cls = classes[index];
                if (!cls.SuppressInit && cls.TheType.TypeInitializer != null)
                {
                    set.HasMethod(cls.TheType.TypeInitializer, cls.TheType.TypeInitializer, out var code);
                    if (code == null)
                    {
                        throw new InvalidOperationException("Inconsistent data set");
                    }

                    codeSequences.Add(code);
                }
            }

            codeSequences.Sort(new DependencySorter());

            // Todo: The above doesn't work reliably yet, therefore do a bit of manual mangling.
            // We need to figure out dependencies between the cctors (i.e. we know that System.Globalization.JapaneseCalendar..ctor depends on System.DateTime..cctor)
            // For now, we just do that by "knowledge" (analyzing the code manually showed these dependencies)
            // The last of the BringToFront elements below will be the first cctor that gets executed
            BringToFront(codeSequences, GetSystemPrivateType("System.Collections.HashHelpers"));
            BringToFront(codeSequences, typeof(System.Text.UTF8Encoding));
            BringToFront(codeSequences, typeof(System.Text.Encoding));
            BringToFront(codeSequences, typeof(System.Text.EncoderFallback));
            BringToFront(codeSequences, typeof(System.Text.EncoderReplacementFallback));
            BringToFront(codeSequences, typeof(System.Text.EncoderExceptionFallback));
            BringToFront(codeSequences, GetSystemPrivateType("System.Diagnostics.Tracing.FrameworkEventSource"));
            BringToFront(codeSequences, typeof(CancellationTokenSource));
            BringToFront(codeSequences, typeof(MiniCultureInfo));
            BringToFront(codeSequences, typeof(Stopwatch));
            BringToFront(codeSequences, GetSystemPrivateType("System.Collections.Generic.NonRandomizedStringEqualityComparer"));
            BringToFront(codeSequences, typeof(System.DateTime));
            BringToFront(codeSequences, typeof(MiniString)); // Initializes String.Empty
            SendToBack(codeSequences, GetSystemPrivateType("System.DateTimeFormat"));
            SendToBack(codeSequences, typeof(System.TimeZoneInfo));

            set.FirmwareStartupSequence = codeSequences;
        }

        public void ExecuteStaticCtors(ExecutionSet set)
        {
            var codeSequences = set.FirmwareStartupSequence;
            if (codeSequences == null)
            {
                // It could (theoretically) be empty, but never null
                throw new InvalidOperationException("No startup code to execute");
            }

            for (var index2 = 0; index2 < codeSequences.Count; index2++)
            {
                var initializer = codeSequences[index2].Method;
                var task = GetTask(set, initializer);
                _logger.LogDebug($"Task {task.TaskId}: Running static initializer of {initializer.DeclaringType!.MemberInfoSignature()}. Step {index2 + 1}/{codeSequences.Count}...");
                task.Invoke(CancellationToken.None);
                task.WaitForResult();
                if (task.GetMethodResults(set, out _, out var state) == false || state != MethodState.Stopped)
                {
                    throw new InvalidProgramException($"Error executing static ctor of class {initializer.DeclaringType}");
                }

                _logger.LogDebug($"Task {task.TaskId}: Static initializer of {initializer.DeclaringType!.MemberInfoSignature()} done.");
            }
        }

        /// <summary>
        /// This sorts the static constructors by dependencies. A constructor that has a dependency to another class
        /// must be executed after that class. Let's hope the dependencies are not circular.
        /// TODO: This doesn't work perfectly yet, therefore some manual tweaking is required
        /// </summary>
        internal class DependencySorter : IComparer<IlCode>
        {
            public int Compare(IlCode? x, IlCode? y)
            {
                if (x == null)
                {
                    return 1;
                }

                if (y == null)
                {
                    return -1;
                }

                var xType = x.Method.DeclaringType;
                var yType = y.Method.DeclaringType;
                if (xType == yType)
                {
                    return 0;
                }
                else if (xType == null)
                {
                    return 1;
                }
                else if (yType == null)
                {
                    return -1;
                }

                if (x.DependentTypes.Contains(yType))
                {
                    return 1;
                }

                if (x.DependentMethods.Any(a => a.DeclaringType == yType))
                {
                    return 1;
                }
                else if (y.DependentTypes.Contains(x.Method.DeclaringType))
                {
                    return -1;
                }
                else if (y.DependentMethods.Any(a => a.DeclaringType == xType))
                {
                    return -1;
                }

                if (xType.Name.Contains("EqualityComparer", StringComparison.Ordinal) && !yType.Name.Contains("EqualityComparer", StringComparison.Ordinal))
                {
                    return -1;
                }

                if (yType.Name.Contains("EqualityComparer", StringComparison.Ordinal) && !xType.Name.Contains("EqualityComparer", StringComparison.Ordinal))
                {
                    return 1;
                }

                if (!string.IsNullOrEmpty(xType.Namespace) && !string.IsNullOrEmpty(yType.Namespace))
                {
                    // Ctors from system come before any other namespaces
                    if (xType.Namespace.StartsWith("System", StringComparison.Ordinal) && !yType.Namespace.Contains("System", StringComparison.Ordinal))
                    {
                        return -1;
                    }

                    if (!xType.Namespace.StartsWith("System", StringComparison.Ordinal) && yType.Namespace.Contains("System", StringComparison.Ordinal))
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }

        private void BringToFront(List<IlCode> classes, Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            int idx = classes.FindIndex(x => x.Method.DeclaringType == type);
            if (idx < 0)
            {
                return;
            }

            var temp = classes[idx];
            // Move the element to the front. Note: Don't replace with the element that is already there, otherwise this would
            // eventually become last instead of second.
            classes.RemoveAt(idx);
            classes.Insert(0, temp);
        }

        private void SendToBack(List<IlCode> classes, Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            int idx = classes.FindIndex(x => x.Method.DeclaringType == type);
            if (idx < 0)
            {
                return;
            }

            // Move to back
            var temp = classes[idx];
            classes.RemoveAt(idx);
            classes.Add(temp);
        }

        /// <summary>
        /// Executes the given method with the provided arguments asynchronously
        /// </summary>
        /// <remarks>Argument count/type not checked yet</remarks>
        /// <param name="method">Handle to method to invoke.</param>
        /// <param name="taskId">An id identifying the started task (a counter usually does)</param>
        /// <param name="arguments">Argument list</param>
        internal void Invoke(EquatableMethod method, short taskId, params object[] arguments)
        {
            if (_activeExecutionSet == null)
            {
                throw new InvalidOperationException("No execution set loaded");
            }

            var decl = _activeExecutionSet.GetMethod(method);
            _logger.LogInformation($"Starting execution on {decl}...");
            if (method.GetParameters().Length != arguments.Length)
            {
                throw new ArgumentException($"The number of arguments for the method {method.MemberInfoSignature()} does not match. {arguments.Length} arguments were provided");
            }

            _commandHandler.ExecuteIlCode(decl.Token, taskId, arguments);
        }

        public void KillTask(EquatableMethod? methodInfo)
        {
            if (_activeExecutionSet == null)
            {
                throw new InvalidOperationException("No execution set loaded");
            }

            if (methodInfo != null)
            {
                var decl = _activeExecutionSet.GetMethod(methodInfo);

                _commandHandler.SendKillTask(decl.Token);
            }
            else
            {
                _commandHandler.SendKillTask(0);
            }
        }

        /// <summary>
        /// Clears all execution data from the arduino, so that the memory is freed again.
        /// </summary>
        /// <param name="force">True to also kill the current task. If false and code is being executed, nothing happens.</param>
        /// <param name="includingFlash">Clear the flash, so a complete new kernel can be loaded</param>
        public void ClearAllData(bool force, bool includingFlash = false)
        {
            if (includingFlash)
            {
                _logger.LogDebug("Erasing flash.");
                _commandHandler.ClearFlash();
            }

            _logger.LogDebug("Resetting execution engine.");
            _commandHandler.SendIlResetCommand(force);
            lock (_activeTasksLock)
            {
                _activeTasks.Clear();
            }

            _activeExecutionSet = null;
        }

        public void Dispose()
        {
            _debugger = null;
        }

        public void SetExecutionSetActive(ExecutionSet executionSet)
        {
            if (_activeExecutionSet != null)
            {
                throw new InvalidOperationException("An execution set is already active. Perform a clear first");
            }

            _activeExecutionSet = executionSet;
        }

        public void CopyToFlash()
        {
            _commandHandler.CopyToFlash();
        }

        /// <summary>
        /// Returns true if the given kernel snapshot is already installed on the board.
        /// </summary>
        /// <param name="snapShot">Kernel snapshot to verify</param>
        /// <returns>True if the given snapshot is loaded, false if either no kernel is loaded or its checksum doesn't match</returns>
        public bool BoardHasKernelLoaded(ExecutionSet.SnapShot snapShot)
        {
            return _commandHandler.IsMatchingFirmwareLoaded(DataVersion, snapShot.GetHashCode());
        }

        internal void WriteFlashHeader(ExecutionSet.SnapShot snapShot, int startupToken, CodeStartupFlags flags)
        {
            _commandHandler.WriteFlashHeader(DataVersion, snapShot.GetHashCode(), startupToken, flags);
        }

        public bool QueryBoardCapabilities(
            [NotNullWhen(true)]
            out IlCapabilities ilCapabilities)
        {
            ilCapabilities = null!;
            _commandHandler.QueryCapabilities();

            int loops = 10;
            while (loops-- > 0)
            {
                // wait for result on receiver thread instead of synchronously waiting for a reply, since that one may be intermixed with other callback informations
                if (_commandHandler.IlCapabilities != null)
                {
                    ilCapabilities = _commandHandler.IlCapabilities;
                    return true;
                }

                Thread.Sleep(50);
            }

            return false;
        }

        public Debugger CreateDebugger()
        {
            if (_activeExecutionSet == null)
            {
                throw new InvalidOperationException("Cannot create a debugger without a loaded execution set");
            }

            _debugger = new Debugger(this, _activeExecutionSet);

            // Always start with debugging disabled
            _commandHandler.SendDebuggerCommand(DebuggerCommand.DisableDebugging);
            return _debugger;
        }
    }
}
