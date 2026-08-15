using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Avalonia.Ide.CompletionEngine.DnlibMetadataProvider;

/// <summary>
/// Extracts style classes defined by XAML class selectors from compiled assemblies.
/// Avalonia compiles <c>Selector="Button.primary"</c> into
/// <c>Selectors::OfType(Button); Selectors::Class("primary")</c> and a <c>^.accent</c> selector
/// inside a ControlTheme into <c>ControlTheme::set_TargetType(Button); Selectors::Nesting();
/// Selectors::Class("accent")</c>. Walking the IL of the populate/build methods recovers the
/// (target type, class name) pairs without needing the original XAML source.
/// </summary>
internal static class StyleClassExtractor
{
    /// <summary>Class selectors that target a pseudo class (e.g. <c>:pressed</c>) start with ':' and
    /// are not valid values of the <c>Classes</c> attribute.</summary>
    private static bool IsPseudoClass(string name) => name.Length > 0 && name[0] == ':';

    public static List<(string? TypeFullName, string ClassName)> Extract(AssemblyDef assembly)
    {
        var result = new List<(string?, string)>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in module.Types)
                WalkType(type, result);
        }
        return result;
    }

    private static void WalkType(TypeDef type, List<(string?, string)> result)
    {
        foreach (var method in type.Methods)
        {
            // Top level x:Class styles populate through !XamlIlPopulate; deferred resources
            // (ControlThemes and other resources) are built lazily in XamlClosure_N::Build_N methods;
            // AvaloniaResource-compiled files are built through CompiledAvaloniaXaml Build:/Populate:.
            if (method.HasBody
                && (method.Name == "!XamlIlPopulate"
                    || method.Name.StartsWith("Build_", System.StringComparison.Ordinal)
                    || method.Name.StartsWith("Build:", System.StringComparison.Ordinal)
                    || method.Name.StartsWith("Populate:", System.StringComparison.Ordinal)))
            {
                ExtractFromMethod(method, result);
            }
        }

        foreach (var nested in type.NestedTypes)
            WalkType(nested, result);
    }

    private static void ExtractFromMethod(MethodDef method, List<(string?, string)> result)
    {
        var instructions = method.Body.Instructions;

        // Type of the style selector currently being built (from Selectors::OfType).
        string? currentType = null;
        // Target type of the ControlTheme currently being built (for '^' selectors).
        string? controlThemeTargetType = null;

        for (var i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];

            if (instr.OpCode == OpCodes.Newobj
                && instr.Operand is IMethod newMethod
                && newMethod.DeclaringType?.FullName == "Avalonia.Styling.ControlTheme")
            {
                controlThemeTargetType = null;
            }

            if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt)
                continue;

            if (instr.Operand is not IMethod methodRef)
                continue;

            var declaringType = methodRef.DeclaringType?.FullName;

            if (declaringType == "Avalonia.Styling.Selectors")
            {
                if (methodRef.Name == "OfType")
                {
                    currentType = FindPrecedingLdtoken(instructions, i);
                }
                else if (methodRef.Name == "Class")
                {
                    var className = FindPrecedingLdstr(instructions, i);
                    if (className != null && !IsPseudoClass(className))
                    {
                        var target = currentType ?? controlThemeTargetType;
                        result.Add((target, className));
                    }
                }
            }
            else if (methodRef.Name == "set_Selector")
            {
                // The selector of the current style is complete; a following style builds its own.
                currentType = null;
            }
            else if (methodRef.Name == "set_TargetType" && declaringType == "Avalonia.Styling.ControlTheme")
            {
                controlThemeTargetType = FindPrecedingLdtoken(instructions, i);
            }
        }
    }

    /// <summary>Finds the <c>ldtoken</c> that feeds the <c>Type.GetTypeFromHandle</c> call directly
    /// preceding the instruction at <paramref name="index"/>.</summary>
    private static string? FindPrecedingLdtoken(IList<Instruction> instructions, int index)
    {
        var j = index - 1;
        if (j >= 0 && instructions[j].OpCode == OpCodes.Call
            && instructions[j].Operand is IMethod m && m.Name == "GetTypeFromHandle")
        {
            j--;
        }
        if (j >= 0 && instructions[j].OpCode == OpCodes.Ldtoken
            && instructions[j].Operand is ITypeDefOrRef typeRef)
        {
            return typeRef.FullName;
        }
        return null;
    }

    /// <summary>Finds the <c>ldstr</c> class name argument preceding the <c>Selectors::Class</c> call.</summary>
    private static string? FindPrecedingLdstr(IList<Instruction> instructions, int index)
    {
        for (var j = index - 1; j >= System.Math.Max(0, index - 8); --j)
        {
            var p = instructions[j];
            if (p.OpCode == OpCodes.Ldstr && p.Operand is string s)
                return s;
            if (p.OpCode == OpCodes.Call || p.OpCode == OpCodes.Callvirt || p.OpCode == OpCodes.Newobj)
                break;
        }
        return null;
    }
}