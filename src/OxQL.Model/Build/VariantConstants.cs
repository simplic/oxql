using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace OxQL.Model.Build;

/// <summary>
/// Reads the value of a property that is a constant of its class: <c>public override string Type
/// => "driver";</c>. It is the model's own type member: REST returns it because the serializer
/// calls the getter, the driver does not store it because it has no setter, and which value a
/// stored object would answer follows from its class alone.
/// </summary>
/// <remarks>
/// <para>
/// A getter is taken for a constant when its code never loads the instance (no <c>ldarg.0</c>): it
/// cannot depend on the object's state then, whatever it returns. The value is read by calling the
/// getter on an instance no constructor ran for (<see cref="RuntimeHelpers.GetUninitializedObject"/>),
/// which a getter that does not read the instance cannot tell from any other; so the only service
/// code that runs is the getter, the static code it calls and, once, the class's static
/// initialiser. No constructor runs and no finalizer: the instance is taken out of finalization,
/// since a finalizer would meet an object nothing initialised. The getter is called twice and both
/// answers have to be the same text, which leaves out a getter that reads a clock or a counter.
/// </para>
/// <para>
/// What this cannot tell: a getter that reads static state which changes later (a resource text
/// per culture, a setting) answers the same value twice here and another one in the service's REST
/// answer afterwards; the value read at the model's build is the one published. And a getter that
/// blocks, or static code that ends the process, is not caught: it runs at a service's start as
/// any static initialiser of the service does.
/// </para>
/// <para>
/// Every way this can fail is an answer, never an exception: the build that calls it runs at a
/// service's start.
/// </para>
/// </remarks>
public static class VariantConstants
{
    private static readonly Lazy<(OpCode[] One, OpCode[] Two)> Codes = new(Table);

    /// <summary>
    /// The constant <paramref name="type"/> answers for the property, in <paramref name="value"/>;
    /// the result is null then, and otherwise why the property is no constant of the class.
    /// </summary>
    public static string? Read(Type type, string propertyName, out string? value)
    {
        value = null;

        try
        {
            PropertyInfo? property;

            try
            {
                property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                return "the class has more than one property of that name";
            }

            if (property is null || property.GetIndexParameters().Length > 0)
                return "the class has no public property of that name";

            if (property.PropertyType != typeof(string))
                return "the property is not a string";

            if (property.SetMethod is not null)
                return "the property has a setter";

            if (property.GetMethod is not { IsAbstract: false, IsStatic: false } getter)
                return "the property has no getter of its own";

            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                return "the class cannot be instantiated";

            if (ReadsInstance(getter) is { } reads)
                return reads;

            var instance = RuntimeHelpers.GetUninitializedObject(type);

            // No constructor ran for it, so no finalizer may either: it would read an object nothing initialised.
            GC.SuppressFinalize(instance);
            var first = getter.Invoke(instance, null) as string;
            var second = getter.Invoke(instance, null) as string;

            if (first is null)
                return "the getter answers null";

            if (!string.Equals(first, second, StringComparison.Ordinal))
                return "the getter answers a different value each time";

            value = first;

            return null;
        }
        catch (Exception exception)
        {
            var thrown = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;

            return $"the getter cannot be read ({thrown.GetType().Name})";
        }
    }

    /// <summary>Why the getter may depend on the instance, or null when its code never loads it.</summary>
    private static string? ReadsInstance(MethodInfo getter)
    {
        if (getter.GetMethodBody()?.GetILAsByteArray() is not { Length: > 0 } code)
            return "the getter's code cannot be read";

        var (one, two) = Codes.Value;

        for (var position = 0; position < code.Length;)
        {
            OpCode opcode;

            if (code[position] == 0xFE)
            {
                if (position + 1 >= code.Length)
                    return "the getter's code cannot be read";

                opcode = two[code[position + 1]];
                position += 2;
            }
            else
            {
                opcode = one[code[position]];
                position += 1;
            }

            // An entry no instruction fills is code this reader does not know.
            if (opcode.Size == 0)
                return "the getter's code cannot be read";

            var operand = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => position + 4 <= code.Length ? 4 + (4 * BitConverter.ToInt32(code, position)) : int.MaxValue,
                _ => 4,
            };

            if (operand < 0 || operand > code.Length - position)
                return "the getter's code cannot be read";

            // The instance is argument zero of an instance method: loaded, or its address taken.
            var loadsInstance = opcode == OpCodes.Ldarg_0
                || ((opcode == OpCodes.Ldarg_S || opcode == OpCodes.Ldarga_S) && code[position] == 0)
                || ((opcode == OpCodes.Ldarg || opcode == OpCodes.Ldarga) && BitConverter.ToUInt16(code, position) == 0);

            if (loadsInstance)
                return "the getter reads the instance";

            position += operand;
        }

        return null;
    }

    /// <summary>The instructions by their one-byte code and by the second byte of a two-byte code.</summary>
    private static (OpCode[] One, OpCode[] Two) Table()
    {
        var one = new OpCode[256];
        var two = new OpCode[256];

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
                continue;

            var code = unchecked((ushort)opcode.Value);

            if (opcode.Size == 1)
                one[code & 0xFF] = opcode;
            else if (code >> 8 == 0xFE)
                two[code & 0xFF] = opcode;
        }

        return (one, two);
    }
}
