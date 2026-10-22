using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.RegularExpressions;
using Albumen.Infrastructure.Windows.Action;
using Xunit;

namespace Albumen.Infrastructure.Tests.Architecture;

public sealed class PermanentDeleteFallbackTests
{
    private static readonly string[] ProductionAssemblies =
        ["Albumen.Domain", "Albumen.Application", "Albumen.Analysis", "Albumen.App", "Albumen.Infrastructure.Onnx", "Albumen.Infrastructure.Sqlite", "Albumen.Infrastructure.Windows"];

    [Fact]
    public void GeneratedComContractsKeepEveryWindowsSdkVtableSlotAndPreservedHResult()
    {
        Check(typeof(IFileOperation), "947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8",
            ["Advise", "Unadvise", "SetOperationFlags", "SetProgressMessage", "SetProgressDialog", "SetProperties", "SetOwnerWindow", "ApplyPropertiesToItem", "ApplyPropertiesToItems", "RenameItem", "RenameItems", "MoveItem", "MoveItems", "CopyItem", "CopyItems", "DeleteItem", "DeleteItems", "NewItem", "PerformOperations", "GetAnyOperationsAborted"]);
        Check(typeof(IShellItem), "43826d1e-e718-42ee-bc55-a1e261c37bfe", ["BindToHandler", "GetParent", "GetDisplayName", "GetAttributes", "Compare"]);
        Check(typeof(IFileOperationProgressSink), "04b0f1a7-9490-44bc-96e1-4296a31252e2",
            ["StartOperations", "FinishOperations", "PreRenameItem", "PostRenameItem", "PreMoveItem", "PostMoveItem", "PreCopyItem", "PostCopyItem", "PreDeleteItem", "PostDeleteItem", "PreNewItem", "PostNewItem", "UpdateProgress", "ResetTimer", "PauseTimer", "ResumeTimer"]);
        Assert.NotNull(typeof(FileOperationProgressSink).GetCustomAttribute<GeneratedComClassAttribute>());
        Assert.NotNull(typeof(IFileOperation).Assembly.GetCustomAttribute<System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute>());
    }

    [Fact]
    public void DriverRejectsEveryFlagOutsideExactRecycleOnlyAllowlistBeforeComUse()
    {
        using var driver = new NativeFileOperationDriver();
        for (var bit = 0; bit < 32; bit++)
            Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetOperationFlags(0x00080414u ^ (1u << bit)));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetOperationFlags(0));
    }

    [Fact]
    public void ProductionSourceHasNoAlternateMutationOrSecondShellItemCall()
    {
        var root = RepositoryRoot();
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")).ToArray();
        var source = string.Join('\n', sourceFiles.Select(File.ReadAllText));
        Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", source);
        Assert.DoesNotMatch(@"\b(SHFileOperation\w*|DeleteFile\w*|RemoveDirectory\w*|MoveFile\w*)\s*\(", source);
        Assert.DoesNotMatch(@"\.\s*DeleteItems\s*\(", source);
        var actionSource = string.Join('\n', sourceFiles.Where(file => file.Contains("Albumen.Infrastructure.Windows" + Path.DirectorySeparatorChar + "Action", StringComparison.Ordinal)).Select(File.ReadAllText));
        Assert.Single(Regex.Matches(actionSource, @"operation!?(?:\.)DeleteItem\s*\(\s*itemPointer\s*,\s*0\s*\)"));
        Assert.Single(Regex.Matches(actionSource, @"FileOperationNative\.SHCreateItemFromParsingName\s*\(\s*originalPath\s*,"));
        Assert.Single(Regex.Matches(actionSource, @"operation!?(?:\.)Advise\s*\("));
        Assert.Contains("UniqueComInterfaceMarshaller<IFileOperation>", actionSource, StringComparison.Ordinal);
        Assert.Contains("UniqueComInterfaceMarshaller<IShellItem>", actionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionIlContainsOnlyOneIFileOperationDeleteItemAndNoBatchOrFallbackCall()
    {
        var assemblies = ProductionAssemblies.Select(name => Assembly.Load(new AssemblyName(name)));
        var calls = assemblies.SelectMany(assembly => assembly.GetTypes()).SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            .SelectMany(CalledMethods).ToArray();
        Assert.Single(calls, method => method.DeclaringType == typeof(IFileOperation) && method.Name == "DeleteItem");
        Assert.DoesNotContain(calls, IsForbiddenMutation);
    }

    internal static bool IsForbiddenMutation(MethodBase method) => method.Name == "DeleteItems"
        || (method.DeclaringType == typeof(File) || method.DeclaringType == typeof(Directory)) && method.Name is "Delete" or "Move" or "Replace"
        || method.DeclaringType is { } type && typeof(FileSystemInfo).IsAssignableFrom(type) && method.Name is "Delete" or "MoveTo" or "Replace"
        || Regex.IsMatch(method.Name, @"^(SHFileOperation|DeleteFile|RemoveDirectory|MoveFile)(W|A|ExW|ExA)?$");

    private static void Check(Type type, string guid, string[] names)
    {
        Assert.NotNull(type.GetCustomAttribute<GeneratedComInterfaceAttribute>());
        Assert.Equal(new Guid(guid), type.GUID);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(method => method.MetadataToken).ToArray();
        Assert.Equal(names, methods.Select(method => method.Name));
        Assert.All(methods, method =>
        {
            Assert.Equal(typeof(int), method.ReturnType);
            Assert.True((method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0);
        });
    }

    internal static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => unchecked((ushort)opcode.Value));
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort code = bytes[offset++];
            if (code == 0xfe) code = (ushort)(0xfe00 | bytes[offset++]);
            var opcode = opcodes[code];
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (target is not null) yield return target;
            }
            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Albumen.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Albumen source root not found.");
    }
}
