using System.Buffers;
using System.Reflection;
using System.Reflection.Emit;

namespace XpilotAudioEqualizer.Interop;

internal static class DynamicProxyFactory
{
    private static readonly ModuleBuilder Module;
    private static readonly Lazy<Type> SampleSourceProxyType = new(BuildSampleSourceProxy);
    private static readonly Lazy<Type> DotCommandProxyType = new(BuildDotCommandProxy);

    static DynamicProxyFactory()
    {
        var asmName = new AssemblyName("XpilotAudioEqualizer.RuntimeProxies");
        var asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        Module = asm.DefineDynamicModule("RuntimeProxies");

        // Synthesize InternalsVisibleTo access so the emitted IL can implement
        // internal AFV/xPilot interfaces at runtime.
        try
        {
            var ivaCtor = typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute)
                .GetConstructor([typeof(string)])!;
            var ivaBuilder = new CustomAttributeBuilder(ivaCtor, ["AFV.Sdk.Net"]);
            asm.SetCustomAttribute(ivaBuilder);
        }
        catch
        {
            // Best-effort; interface impl does not strictly require it.
        }
    }

    public static bool IsAvailable
    {
        get
        {
            try { return SampleSourceProxyType.Value is not null && DotCommandProxyType.Value is not null; }
            catch { return false; }
        }
    }

    public static object CreateSampleSourceProxy(object inner, SpanAction<float, object> processor, object? state)
    {
        var proxy = Activator.CreateInstance(SampleSourceProxyType.Value)!;
        SampleSourceProxyType.Value.GetField("Inner")!.SetValue(proxy, inner);
        SampleSourceProxyType.Value.GetField("Processor")!.SetValue(proxy, processor);
        SampleSourceProxyType.Value.GetField("State")!.SetValue(proxy, state);
        return proxy;
    }

    public static object CreateDotCommandProxy(Func<string, Func<string, Task>?, object?, Task> handler, object? state)
    {
        var proxy = Activator.CreateInstance(DotCommandProxyType.Value)!;
        DotCommandProxyType.Value.GetField("Handler")!.SetValue(proxy, handler);
        DotCommandProxyType.Value.GetField("State")!.SetValue(proxy, state);
        return proxy;
    }

    private static Type BuildSampleSourceProxy()
    {
        var sampleSourceIface = FindType("AFV.Sdk.Net.Core.Audio.Abstractions.ISampleSource")
            ?? FindType("AFV.Sdk.Net.src.Core.Audio.Abstractions.ISampleSource");
        var sourceStatusType = FindType("AFV.Sdk.Net.Core.Audio.Abstractions.SourceStatus")
            ?? FindType("AFV.Sdk.Net.src.Core.Audio.Abstractions.SourceStatus");
        if (sampleSourceIface is null || sourceStatusType is null)
        {
            throw new InvalidOperationException("ISampleSource/SourceStatus not found in loaded AFV.Sdk.Net.");
        }
        var getFrame = sampleSourceIface.GetMethod("GetAudioFrame")!;

        var tb = Module.DefineType("SampleSourceInterceptor",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        tb.AddInterfaceImplementation(sampleSourceIface);
        tb.DefineField("Inner", typeof(object), FieldAttributes.Public);
        tb.DefineField("Processor", typeof(SpanAction<float, object>), FieldAttributes.Public);
        tb.DefineField("State", typeof(object), FieldAttributes.Public);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        var mb = tb.DefineMethod(getFrame.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            getFrame.ReturnType,
            [typeof(Span<float>)]);
        var il = mb.GetILGenerator();
        var innerField = tb.GetField("Inner")!;
        var procField = tb.GetField("Processor")!;
        var stateField = tb.GetField("State")!;

        // object inner = this.Inner; processor fields...
        var innerLocal = il.DeclareLocal(typeof(object));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, innerField);
        il.Emit(OpCodes.Stloc, innerLocal);

        // status = ((ISampleSource)inner).GetAudioFrame(bufferOut)
        il.Emit(OpCodes.Ldloc, innerLocal);
        il.Emit(OpCodes.Castclass, sampleSourceIface);
        il.Emit(OpCodes.Ldarg_1); // Span<float> by value (Ldarg loads the span struct copy)
        il.Emit(OpCodes.Callvirt, getFrame);
        var statusLocal = il.DeclareLocal(sourceStatusType);
        il.Emit(OpCodes.Stloc, statusLocal);

        // if (Processor != null) Processor(bufferOut, State);
        var procLocal = il.DeclareLocal(typeof(SpanAction<float, object>));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, procField);
        il.Emit(OpCodes.Stloc, procLocal);
        var skipLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, procLocal);
        il.Emit(OpCodes.Brfalse_S, skipLabel);
        il.Emit(OpCodes.Ldloc, procLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, stateField);
        var invoke = typeof(SpanAction<float, object>).GetMethod("Invoke")!;
        il.Emit(OpCodes.Callvirt, invoke);
        il.MarkLabel(skipLabel);

        il.Emit(OpCodes.Ldloc, statusLocal);
        il.Emit(OpCodes.Ret);

        tb.DefineMethodOverride(mb, getFrame);
        return tb.CreateType()!;
    }

    private static Type BuildDotCommandProxy()
    {
        var dotCmdIface = FindType("Vatsim.Xpilot.CommandProcessing.IDotCommandProcessor");
        if (dotCmdIface is null)
        {
            throw new InvalidOperationException("IDotCommandProcessor not found in loaded xPilot.");
        }
        var process = dotCmdIface.GetMethod("Process")!;
        var handlerType = typeof(Func<string, Func<string, Task>?, object?, Task>);

        var tb = Module.DefineType("DotCommandInterceptor",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        tb.AddInterfaceImplementation(dotCmdIface);
        tb.DefineField("Handler", handlerType, FieldAttributes.Public);
        tb.DefineField("State", typeof(object), FieldAttributes.Public);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        var paramTypes = Array.ConvertAll(process.GetParameters(), p => p.ParameterType);
        var mb = tb.DefineMethod(process.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            process.ReturnType,
            paramTypes);
        // Preserve default value (resultHandler = null).
        mb.DefineParameter(2, ParameterAttributes.HasDefault | ParameterAttributes.Optional, "resultHandler");
        var il = mb.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, tb.GetField("Handler")!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, tb.GetField("State")!);
        il.Emit(OpCodes.Callvirt, handlerType.GetMethod("Invoke")!);
        il.Emit(OpCodes.Ret);

        tb.DefineMethodOverride(mb, process);
        return tb.CreateType()!;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = null;
            try { t = asm.GetType(fullName, throwOnError: false, ignoreCase: false); } catch { }
            if (t is not null)
            {
                return t;
            }
        }
        return Type.GetType(fullName, throwOnError: false);
    }
}
