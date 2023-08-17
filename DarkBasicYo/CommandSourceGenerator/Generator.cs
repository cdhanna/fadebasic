using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DarkBasicYo.Virtual;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DarkBasicYo.SourceGenerators
{

    [Generator]
    public class CommandSourceGenerator : ISourceGenerator
    {
        public static List<string> Logs { get; } = new List<string>();

        public static void Print(string msg) => Logs.Add("//\t" + msg);
        
        public static void FlushLogs(GeneratorExecutionContext context)
        {
            context.AddSource($"GeneratedLogs.g.cs", $@"
//autogen
namespace GeneratedLogs 
{{
    public class LogsLogs 
    {{
        {string.Join("\n", Logs)}
    }}
}}
");
            // context.AddSource($"logs.g.cs", SourceText.From(string.Join("\n", Logs), Encoding.UTF8));
        }
        
        private const string VM = "__vm";
        
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new Receiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            try
            {
                var receiver = context.SyntaxReceiver as Receiver;

                foreach (var kvp in receiver.classNameToCommandList)
                {
                    var fileName = $"{kvp.Key.Item1}CallUtil.g.cs";
                    var source = ToSource(kvp.Key.Item1, kvp.Key.Item2, kvp.Value);

                    context.AddSource(fileName, source);
                }


                context.AddSource($"TunaTester.g.cs", $@"
//autogen
namespace TunaTester 
{{
    public class TunaTest 
    {{
// {receiver.methodNames}
        // {receiver.classNameToCommandList.Count}
    }}
}}
");
            }
            catch (Exception ex)
            {
                Print(ex.Message);
            }
            finally
            {
                FlushLogs(context);
            }
        }

        static string ToSource(string name, string namespaceStr, List<CommandDescriptor> descriptor)
        {
            // {string.Join("\n", descriptor.Select(GetMethodSource))}
            return $@"// generated file
using DarkBasicYo.Virtual;
using System.Runtime.InteropServices;
using System;

namespace {namespaceStr}
{{
    public partial class {name} : {nameof(IMethodSource)}
    {{
        public int Count => {descriptor.Count};

        public {nameof(CommandInfo)}[] Commands {{ get; }} = new {nameof(CommandInfo)}[]
        {{
            {string.Join("\n,", descriptor.Select(GetInfoSource))}
        }};


        {string.Join("\n", descriptor.Select(GetMethodSource))}
    }}
}}
";
        }

        // static string GetRunMethodSource(List<CommandDescriptor> descriptors)
        // {
        //     var sb = new StringBuilder();
        //
        //     sb.AppendLine($"public bool {nameof(IMethodSource.TryRun)}({nameof(VirtualMachine)} {VM}, int methodIndex)");
        //     sb.AppendLine("{");
        //     {
        //         sb.AppendLine("\tswitch(methodIndex)");
        //         sb.AppendLine("\t{");
        //         {
        //             for (var i = 0; i < descriptors.Count; i++)
        //             {
        //                 sb.AppendLine($"\t\tcase {i}:");
        //                 sb.AppendLine($"\t\t{descriptors[i].MethodName}({VM});");
        //                 sb.AppendLine("\t\treturn true;");
        //             }
        //             sb.AppendLine("\t\tdefault: return false;");
        //         }
        //         sb.AppendLine("\t}");
        //     }
        //     sb.AppendLine("}");
        //     
        //     return sb.ToString();
        // }
        
        
        static string GetInfoSource(CommandDescriptor descriptor, int index)
        {
            return $@"new {nameof(CommandInfo)}()
{{
    {nameof(CommandInfo.name)} = {descriptor.CallName},
    {nameof(CommandInfo.methodIndex)} = {index},
    {nameof(CommandInfo.executor)} = {descriptor.MethodName},
    {nameof(CommandInfo.args)} = new {nameof(CommandArgInfo)}[] 
    {{
        {string.Join("\n,", descriptor.Parameters.Select((x, xi) => GetArgInfoSource(descriptor, x, xi)))}
    }}
}}
";
        }

        static string GetArgInfoSource(CommandDescriptor command, ArgDescriptor arg, int index)
        {
            return $@"new {nameof(CommandArgInfo)}() 
{{
    {nameof(CommandArgInfo.isVmArg)} = {arg.IsVm.ToString().ToLowerInvariant()},
    {nameof(CommandArgInfo.isOptional)} = false,
    {nameof(CommandArgInfo.isRef)} = {arg.IsRef.ToString().ToLowerInvariant()},
    {nameof(CommandArgInfo.xyz)} = 33,
    {nameof(CommandArgInfo.typeCode)} = {arg.TypeCode}
}}";
        }
        

        static string GetMethodSource(CommandDescriptor descriptor)
        {

            
            return $@"// method {descriptor.CallName}
public static void {descriptor.MethodName}({nameof(VirtualMachine)} {VM})
{{
    // declare and assign all method inputs...
    {string.Join("\n", descriptor.Parameters.Select(GetParameterDeclSource))}

    // invoke the method Gosh Darn! Test it!
    {GetReturnSource(descriptor)}
}}
";
        }

        static string GetReturnSource(CommandDescriptor descriptor)
        {
            try
            {
                var sb = new StringBuilder();
                
                var result = "__result";
                var resultSpan = "__resultSpan";
                switch (descriptor.ReturnType)
                {
                    case "void":
                        sb.AppendLine(GetInvokeSource(descriptor));
                        break;
                    case "int":
                        sb.AppendLine($"var {result} = {GetInvokeSource(descriptor)}");

                        sb.AppendLine($"ReadOnlySpan<byte> {resultSpan} = {nameof(BitConverter)}.{nameof(BitConverter.GetBytes)}({result});");

                        sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.PushSpan)}(ref {VM}.{nameof(VirtualMachine.stack)}, {resultSpan}, {TypeCodes.INT});");
                        break;
                    case "string":
                        sb.AppendLine($"var {result} = {GetInvokeSource(descriptor)}");

                        var resultStrPtr = "__resultStrPtr";
                        var ptrIntBytes = "__ptrIntBytes";
                        sb.AppendLine($"{nameof(VmConverter)}.{nameof(VmConverter.FromStringSpan)}({result}, out var {resultSpan});");
                        sb.AppendLine($"{VM}.{nameof(VirtualMachine.heap)}.{nameof(VirtualMachine.heap.Allocate)}({resultSpan}.Length, out var {resultStrPtr});");
                        sb.AppendLine($"{VM}.{nameof(VirtualMachine.heap)}.{nameof(VirtualMachine.heap.WriteSpan)}({resultStrPtr}, {resultSpan}.Length, {resultSpan});");
                        sb.AppendLine($"var {ptrIntBytes} = {nameof(BitConverter)}.{nameof(BitConverter.GetBytes)}({resultStrPtr});");
                        sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.PushSpan)}(ref {VM}.{nameof(VirtualMachine.stack)}, {ptrIntBytes}, {TypeCodes.STRING});");
                        // v.heap.Allocate();
                    // case TypeCodes.STRING:
                    //     var resultStr = (string)result;
                    // VmConverter.FromString(resultStr, out bytes);
                    // machine.heap.Allocate(bytes.Length, out var resultStrPtr);
                    //     machine.heap.Write(resultStrPtr, bytes.Length, bytes);
                    //     var ptrIntBytes = BitConverter.GetBytes(resultStrPtr);
                    //     VmUtil.PushSpan(ref machine.stack, ptrIntBytes, TypeCodes.STRING);
                    //     break;
                    
                        
                    
                        break;
                    default:
                        throw new Exception("unhandled return type, " + descriptor.ReturnType);
                }

                
                // take care of ref parameters...
                foreach (var parameter in descriptor.Parameters)
                {
                    if (!parameter.IsRef) continue;
                    
                    // ah, this is a ref parameter, so we need to put it back in its register...
                    // 
                    sb.AppendLine($"{VM}.{nameof(VirtualMachine.dataRegisters)}[{GetRegistryVarName(parameter)}] =  {nameof(BitConverter)}.{nameof(BitConverter.ToUInt32)}({nameof(BitConverter)}.{nameof(BitConverter.GetBytes)}({parameter.Name}), 0);");
                }


                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"// could not generate return expression. reason=[{ex.Message}]";
            }
        }
        
        static string GetInvokeSource(CommandDescriptor descriptor)
        {
            return $@"{descriptor.TargetClassName}.{descriptor.TargetMethodName}({string.Join(", ", descriptor.Parameters.Select(p => GetInvokeParameterSource(descriptor, p)))});";
        }

        static string GetInvokeParameterSource(CommandDescriptor descriptor, ArgDescriptor arg)
        {
            if (arg.IsRef)
            {
                return $"ref {arg.Name}";
            }
            else return arg.Name;
        }

        static string GetRegistrySpanName(ArgDescriptor descriptor)
        {
            return $"{descriptor.Name}RegAddrSpan";
        }
        static string GetRegistryVarName(ArgDescriptor descriptor)
        {
            return $"{descriptor.Name}RegAddr";
        }
        static string GetStrPointerVarName(ArgDescriptor descriptor)
        {
            return $"{descriptor.Name}StrPtr";
        }
        static string GetStrSizeVarName(ArgDescriptor descriptor)
        {
            return $"{descriptor.Name}StrSize";
        }
        
        static string GetParameterDeclSource(ArgDescriptor descriptor)
        {
            var sb = new StringBuilder();
         
            sb.AppendLine($"// handle {descriptor.Name}");

            // var typeName = descriptor.TypeName;
            // if (descriptor.IsRef)
            // {
            //     // we'll expect to see a ptr
            // }
            
            
            var spanName = $"{descriptor.Name}Span";
            var typeCode = $"{descriptor.Name}Tc";

            if (descriptor.IsVm)
            {
                // need to cast the vm to the right type, and hope it works!
                sb.AppendLine(
                    $"var {descriptor.Name} = ({descriptor.TypeName}){VM};");

                return sb.ToString();
            }
            
            // for heap vs reg pointers, at compile time, we don't know.
            // so, we'll need to check the stack itself

            VirtualMachine v = null;
            // VmUtil.ReadValue<int>(v, out var x, out var s, out var a);
            // VmUtil.ReadValue<string>(v, out var x2, out var s2, out var a2);
            try
            {
                switch (descriptor.TypeName, descriptor.IsRef)
                {
                    case ("string", _):
                        sb.AppendLine(
                            $"{nameof(VmUtil)}.{nameof(VmUtil.ReadSpan)}(ref {VM}.{nameof(VirtualMachine.stack)}, out var {typeCode}, out var {spanName});"
                        );
                        sb.AppendLine(
                            $"var {GetStrPointerVarName(descriptor)} = {nameof(MemoryMarshal)}.{nameof(MemoryMarshal.Read)}<int>({spanName});");
                        sb.AppendLine($"{descriptor.TypeName} {descriptor.Name} = default;");
                        sb.AppendLine($"if ({VM}.{nameof(VirtualMachine.heap)}.{nameof(VmHeap.TryGetAllocationSize)}({GetStrPointerVarName(descriptor)}, out var {GetStrSizeVarName(descriptor)})) {{");
                        sb.AppendLine($"\t{VM}.{nameof(VirtualMachine.heap)}.{nameof(VmHeap.ReadSpan)}({GetStrPointerVarName(descriptor)}, {GetStrSizeVarName(descriptor)}, out {spanName});");
                        sb.AppendLine($"\t{descriptor.Name} = {nameof(VmConverter)}.{nameof(VmConverter.ToStringSpan)}({spanName});");
                        sb.AppendLine("} else throw new Exception(\"runtime exception! expected to find a string pointer\");");
                        
                        break;
                    
                    
                    case ("int", true):
                        sb.AppendLine(
                            $"{nameof(VmUtil)}.{nameof(VmUtil.ReadSpan)}(ref {VM}.{nameof(VirtualMachine.stack)}, out var {typeCode}, out var {GetRegistrySpanName(descriptor)});"
                        );
                        
                        sb.AppendLine(
                            $"var {GetRegistryVarName(descriptor)} =  {GetRegistrySpanName(descriptor)}[0];");
                        
                        sb.AppendLine($"var {spanName} = new ReadOnlySpan<byte>({nameof(BitConverter)}.{nameof(BitConverter.GetBytes)}({VM}.{nameof(VirtualMachine.dataRegisters)}[{GetRegistryVarName(descriptor)}]));");
                        
                        sb.AppendLine(
                            $"{nameof(VmUtil)}.{nameof(VmUtil.CastInlineSpan)}({spanName}, {VM}.{nameof(VirtualMachine.typeRegisters)}[{GetRegistryVarName(descriptor)}], {TypeCodes.INT}, ref {spanName});"
                        );
                        sb.AppendLine(
                            $"var {descriptor.Name} = {nameof(MemoryMarshal)}.{nameof(MemoryMarshal.Read)}<int>({spanName});");
                        
                        break;
                    case ("int" ,false):
                        
                        sb.AppendLine(
                            $"{nameof(VmUtil)}.{nameof(VmUtil.ReadSpan)}(ref {VM}.{nameof(VirtualMachine.stack)}, out var {typeCode}, out var {spanName});"
                            );
                        sb.AppendLine(
                            $"{nameof(VmUtil)}.{nameof(VmUtil.CastInlineSpan)}({spanName}, {typeCode}, {TypeCodes.INT}, ref {spanName});"
                            );
                        sb.AppendLine(
                            $"var {descriptor.Name} = {nameof(MemoryMarshal)}.{nameof(MemoryMarshal.Read)}<int>({spanName});");
                        break;
                    default:
                        throw new Exception("unhandled type, " + descriptor.TypeName);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"// could not generate. reason=[{ex.Message}]");
            }

            return sb.ToString();
        }
    }

    public class CommandDescriptor
    {
        public ClassDeclarationSyntax classSyntax;
        public MethodDeclarationSyntax methodSyntax;
        public AttributeSyntax attributeSyntax;
        public string TargetClassName => $"{((NamespaceDeclarationSyntax)classSyntax.Parent).Name}.{classSyntax.Identifier.ToString()}";
        public string TargetMethodName => methodSyntax.Identifier.ToString();
        public string MethodName => $"Call2_{TargetMethodName}";
        public string NamespaceName => $"{((NamespaceDeclarationSyntax)classSyntax.Parent).Name}";
        public string CallName => attributeSyntax.ArgumentList.Arguments[0].Expression.ToString();

        public string ReturnType => methodSyntax.ReturnType.ToString();
        
        
        public List<ArgDescriptor> Parameters =>
            methodSyntax.ParameterList.Parameters.Select(p => new ArgDescriptor(this, p)).ToList();

    }

    public class ArgDescriptor
    {
        private readonly CommandDescriptor _owner;
        private readonly ParameterSyntax _parameter;

        public string Name => _parameter.Identifier.ToString();
        public string VariableStateName => _parameter.Identifier.ToString() + "VarState";
        public string TypeName => _parameter.Type.ToString();
        public bool IsRef => _parameter.Modifiers.Any(SyntaxKind.RefKeyword);

        public bool IsVm => _parameter.AttributeLists.Any(x => x.Attributes.Any(attribute =>
        {
            var isCommandAttr = attribute.Name.ToString() == nameof(FromVmAttribute) || attribute.Name.ToString() == nameof(FromVmAttribute).Substring(0, nameof(FromVmAttribute).Length - "Attribute".Length);
            return isCommandAttr;
        }));

        public byte TypeCode
        {
            get
            {
                if (IsVm) return TypeCodes.VM;
                
                return TypeName switch
                {
                    "int" => TypeCodes.INT,
                    "string" => TypeCodes.STRING,
                    nameof(VirtualMachine) => TypeCodes.VM,
                    _ => throw new Exception($"Type=[{TypeName}] is not a valid arg type")
                };
            }
        }

        public ArgDescriptor(CommandDescriptor owner, ParameterSyntax parameter)
        {
            _owner = owner;
            _parameter = parameter;
        }
    }
    
    public class Receiver : ISyntaxReceiver
    {
        public string methodNames;

        public List<CommandDescriptor> commands = new List<CommandDescriptor>();

        public Dictionary<(string, string), List<CommandDescriptor>> classNameToCommandList => commands
            .GroupBy(x => $"{x.classSyntax.Identifier.Text}")
            .ToDictionary(x => (x.Key, x.FirstOrDefault()?.NamespaceName), x => x.ToList());

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            switch (syntaxNode)
            {
                case MethodDeclarationSyntax methodSyntax:
                    
                    if (!TryGetCommandAttribute(methodSyntax, out var attributeSyntax, out var classSyntax))
                        return;
                    
                    // ah, so we know we have a method!
                    methodNames += classSyntax.Identifier.ToString() + " . " + methodSyntax.Identifier.ToString();
                    commands.Add(new CommandDescriptor
                    {
                        methodSyntax = methodSyntax,
                        classSyntax = classSyntax,
                        attributeSyntax = attributeSyntax
                    });
                    break;
            }

            
        }

        static bool TryGetCommandAttribute(MethodDeclarationSyntax methodSyntax, out AttributeSyntax commandAttributeSyntax, out ClassDeclarationSyntax classSyntax)
        {
            commandAttributeSyntax = null;
            classSyntax = null;
            
            //the method must be static!
            var isStatic = methodSyntax.Modifiers.Any(SyntaxKind.StaticKeyword);
            if (!isStatic)
            {
                return false;
            }

            
            foreach (var attributeList in methodSyntax.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var isCommandAttr = attribute.Name.ToString() == nameof(DarkBasicCommandAttribute) || attribute.Name.ToString() == nameof(DarkBasicCommandAttribute).Substring(0, nameof(DarkBasicCommandAttribute).Length - "Attribute".Length);
                    
                    if (!isCommandAttr) continue;
                    commandAttributeSyntax = attribute;

                    
                    // check that the method is coming from the right type of family
                    switch (methodSyntax.Parent)
                    {
                        case ClassDeclarationSyntax classSyn:
                            
                            // the class must be static!
                            var isPartial = classSyn.Modifiers.Any(SyntaxKind.PartialKeyword);
                            if (!isPartial)
                            {
                                continue;
                            }

                            classSyntax = classSyn;
                            
                            break;
                        default:
                            continue;
                    }
                    
                    // get the arguments, and assert that are of valid types
                    
                    
                    
                    return true;
                }
            }

            return false;
        }
    }
}