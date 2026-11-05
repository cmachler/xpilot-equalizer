using System.Reflection;
using System.Reflection.Emit;

/// <summary>
/// MethodInfo.Invoke is trimmed from xPilot's self-contained .NET 10 runtime.
/// Use a DynamicMethod + IL to call the method instead.
/// LIMIT: the emitted DynamicMethod is owned by our assembly, so at JIT time
/// it must resolve every signature type from OUR load context. Methods whose
/// parameters live in host-framework assemblies (e.g. Avalonia's
/// DispatcherPriority) throw FileNotFoundException — call those directly
/// instead (see AudioEqualizerPlugin.PostToUiThread fallback). Works for
/// host-domain types (EventBus, voice manager) and BCL/CoreLib types.
/// </summary>
internal static class ReflectionInvoke
{
    public static object? Invoke(MethodInfo method, object? target, params object?[] args)
    {
        try
        {
        if (args.Length == 1 && args[0] is object[] arr)
        {
            args = arr;
        }

        var parameters = method.GetParameters();
        if (args.Length != parameters.Length)
        {
            var padded = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                padded[i] = i < args.Length ? args[i] : (parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null);
            }
            args = padded;
        }

        var paramTypes = new Type[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            paramTypes[i] = parameters[i].ParameterType;
        }

        var returnType = method.ReturnType;
        var dm = new DynamicMethod(
            "Invoke_" + ((MemberInfo)method).Name,
            returnType == typeof(void) ? typeof(object) : returnType,
            new[] { typeof(object), typeof(object[]) },
            typeof(ReflectionInvoke),
            skipVisibility: true);

        var il = dm.GetILGenerator();

        if (!method.IsStatic)
        {
            il.Emit(OpCodes.Ldarg_0);
            if (target is not null && target.GetType() != method.DeclaringType)
            {
                il.Emit(OpCodes.Castclass, method.DeclaringType!);
            }
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldelem_Ref);
            var pt = parameters[i].ParameterType;
            if (pt.IsValueType)
            {
                il.Emit(OpCodes.Unbox_Any, pt);
            }
            else if (pt != typeof(object))
            {
                il.Emit(OpCodes.Castclass, pt);
            }
        }

        if (method.IsStatic)
        {
            il.Emit(OpCodes.Call, method);
        }
        else
        {
            il.Emit(OpCodes.Callvirt, method);
        }

        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Ldnull);
        }
        else if (returnType.IsValueType)
        {
            il.Emit(OpCodes.Box, returnType);
        }

        il.Emit(OpCodes.Ret);

        Func<object?, object?[], object?> del;
        try
        {
            del = (Func<object?, object?[], object?>)dm.CreateDelegate(typeof(Func<object?, object?[], object?>));
        }
        catch (Exception ex)
        {
            throw new Exception($"ReflectionInvoke CreateDelegate {method.DeclaringType?.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}", ex);
        }
        try
        {
            return del(target, args);
        }
        catch (Exception ex)
        {
            throw new Exception($"ReflectionInvoke {method.DeclaringType?.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}", ex);
        }
        }
        catch (Exception ex)
        {
            throw new Exception($"ReflectionInvoke build {method.DeclaringType?.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}