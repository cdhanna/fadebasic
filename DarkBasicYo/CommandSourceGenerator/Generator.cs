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
    {nameof(CommandInfo.sig)} = ""{descriptor.Sig}"",
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
    {nameof(CommandArgInfo.isOptional)} = {arg.IsOptional.ToString().ToLowerInvariant()},
    {nameof(CommandArgInfo.isRef)} = {arg.IsRef.ToString().ToLowerInvariant()},
    {nameof(CommandArgInfo.isParams)} = {arg.IsParams.ToString().ToLowerInvariant()},
    {nameof(CommandArgInfo.typeCode)} = {arg.TypeCode}
}}";
        }
        

        static string GetMethodSource(CommandDescriptor descriptor)
        {

            var flippedParameterList = descriptor.Parameters.ToList();
            flippedParameterList.Reverse();

            var sb = new StringBuilder();

            // for (var i = 0; i < flippedParameterList.Count; i++)
            // {
            //     var parameter = flippedParameterList[i];
            //     
            //     parameter.IsParams
            //     
            // }
            
            
            return $@"// method {descriptor.CallName}
public static void {descriptor.MethodName}({nameof(VirtualMachine)} {VM})
{{
    // declare and assign all method inputs...
    {string.Join("\n", flippedParameterList.Select(GetParameterDeclSource))}

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
                       
                    
                        break;
                    default:
                        throw new Exception("unhandled return type, " + descriptor.ReturnType);
                }

                
                // take care of ref parameters...
                foreach (var parameter in descriptor.Parameters)
                {
                    if (!parameter.IsRef) continue;

                    switch (parameter.TypeName)
                    {
                        case "object":
                            break;
                        case "int":
                            sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.HandleValue)}<{parameter.TypeName}>(" +
                                          $"{VM}, " +
                                          $"{parameter.Name}, " +
                                          $"{parameter.TypeCode}, " +
                                          $"{parameter.VariableStateName}, " +
                                          $"{parameter.VariableAddress}" +
                                          $");");
                            break;
                        case "string":
                            sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.HandleValueString)}(" +
                                          $"{VM}, " +
                                          $"{parameter.Name}, " +
                                          $"{parameter.TypeCode}, " +
                                          $"{parameter.VariableStateName}, " +
                                          $"{parameter.VariableAddress}" +
                                          $");");
                            break;
                    }

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
        
        static string GetParameterDeclSource(ArgDescriptor descriptor)
        {
            var sb = new StringBuilder();
         
            sb.AppendLine($"// handle {descriptor.Name}");

            try
            {
                if (descriptor.IsOptional)
                {
                    sb.AppendLine($"// OPTIONAL EXPR: " + descriptor.OptionalExpr.ToString());
                }

                if (descriptor.IsVm)
                {
                    // need to cast the vm to the right type, and hope it works!
                    sb.AppendLine(
                        $"var {descriptor.Name} = ({descriptor.TypeName}){VM};");

                    return sb.ToString();
                }

                if (descriptor.IsParams)
                {
                    var iVar = "__i";
                    // we'll read until the end. First, we need to know how many doodads there are!

                    sb.AppendLine(
                        $"{nameof(VmUtil)}.{nameof(VmUtil.ReadAsInt)}(ref {VM}.{nameof(VirtualMachine.stack)}, out var {descriptor.ParamsLengthVariableName});");
                    sb.AppendLine(
                        $"var {descriptor.Name} = new {descriptor.TypeName.Replace("[]", "")}[{descriptor.ParamsLengthVariableName}];");
                    sb.AppendLine($"for (var {iVar} = 0; {iVar} < {descriptor.ParamsLengthVariableName}; {iVar}++) ");
                    sb.AppendLine("{");
                    var forSrc = GenerateVariableReadSource(
                        typeName: descriptor.TypeName.Replace("[]", ""),
                        optionalStr: "default",
                        name: $"{descriptor.Name}[{iVar}]",
                        stateVariable: "_",
                        addrVariable: "_"
                    );
                    sb.AppendLine($"{forSrc}");
                    sb.AppendLine("}");
                    return sb.ToString();
                }

                var optStr = (descriptor.IsOptional ? descriptor.OptionalExpr.ToString() : "default");
                var src = GenerateVariableReadSource(descriptor.TypeName, optStr,
                    "var " + descriptor.Name,
                    "var " + descriptor.VariableStateName,
                    "var " + descriptor.VariableAddress);
                sb.AppendLine(src);
                // switch (descriptor.TypeName)
                // {
                //     case "object":
                //         sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.ReadValueAny)}(" +
                //                       $"{VM}, " +
                //                       $"{(descriptor.IsOptional ? descriptor.OptionalExpr.ToString() : "default")}, " +
                //                       $"out var {descriptor.Name}," +
                //                       $"out var {descriptor.VariableStateName}," +
                //                       $"out var {descriptor.VariableAddress}" +
                //                       $");");
                //         break;
                //     case "int":
                //         sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.ReadValue)}<{descriptor.TypeName}>(" +
                //                       $"{VM}, " +
                //                       $"{(descriptor.IsOptional ? descriptor.OptionalExpr.ToString() : "default")}, " +
                //                       $"out var {descriptor.Name}," +
                //                       $"out var {descriptor.VariableStateName}," +
                //                       $"out var {descriptor.VariableAddress}" +
                //                       $");");
                //         break;
                //     case ("string"):
                //         sb.AppendLine($"{nameof(VmUtil)}.{nameof(VmUtil.ReadValueString)}(" +
                //                       $"{VM}, " +
                //                       $"{(descriptor.IsOptional ? descriptor.OptionalExpr.ToString() : "default")}, " +
                //                       $"out var {descriptor.Name}," +
                //                       $"out var {descriptor.VariableStateName}," +
                //                       $"out var {descriptor.VariableAddress}" +
                //                       $");");
                //         break;
                // }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"// could not generate. reason=[{ex.Message}]");
            }

            return sb.ToString();
        }

        public static void Test(out int x)
        {
            x = 2;
        }
        static string GenerateVariableReadSource(string typeName, string optionalStr, string name, string stateVariable, string addrVariable)
        {
            // var n = new int[3];
            switch (typeName)
            {
                case "object":
                    return ($"{nameof(VmUtil)}.{nameof(VmUtil.ReadValueAny)}(" +
                                  $"{VM}, " +
                                  $"{optionalStr}, " +
                                  $"out {name}," +
                                  $"out {stateVariable}," +
                                  $"out {addrVariable}" +
                                  $");");
                    break;
                case "int":
                    return ($"{nameof(VmUtil)}.{nameof(VmUtil.ReadValue)}<{typeName}>(" +
                                  $"{VM}, " +
                                  $"{optionalStr}, " +
                                  $"out {name}," +
                                  $"out {stateVariable}," +
                                  $"out {addrVariable}" +
                                  $");");
                    break;
                case ("string"):
                    return ($"{nameof(VmUtil)}.{nameof(VmUtil.ReadValueString)}(" +
                                  $"{VM}, " +
                                  $"{optionalStr}, " +
                                  $"out {name}," +
                                  $"out {stateVariable}," +
                                  $"out {addrVariable}" +
                                  $");");
                    break;
                default:
                    throw new NotImplementedException("that type isn't supported for reading " + typeName);
            }
        }
    }

    public class CommandDescriptor
    {
        public ClassDeclarationSyntax classSyntax;
        public MethodDeclarationSyntax methodSyntax;
        public AttributeSyntax attributeSyntax;
        public string TargetClassName => $"{((NamespaceDeclarationSyntax)classSyntax.Parent).Name}.{classSyntax.Identifier.ToString()}";
        public string TargetMethodName => methodSyntax.Identifier.ToString();
        public string MethodName => $"Call_{TargetMethodName}_{Sig}";
        public string NamespaceName => $"{((NamespaceDeclarationSyntax)classSyntax.Parent).Name}";
        public string CallName => attributeSyntax.ArgumentList.Arguments[0].Expression.ToString();
        public string Sig => ReturnType + "R" + string.Join("", Parameters.Select(x => x.TypeCode + (x.IsRef ? "O":"")));

        public string ReturnType => methodSyntax.ReturnType.ToString();
        
        
        public List<ArgDescriptor> Parameters =>
            methodSyntax.ParameterList.Parameters.Select(p => new ArgDescriptor(this, p)).ToList();

        
        public static void Call_Test_voidR()
        {
        
        }

    }
    public class ArgDescriptor
    {
        private readonly CommandDescriptor _owner;
        private readonly ParameterSyntax _parameter;

        public string Name => _parameter.Identifier.ToString();
        public string VariableStateName => _parameter.Identifier.ToString() + "VarState";
        public string VariableAddress => _parameter.Identifier.ToString() + "VarAddr";
        public string ParamsLengthVariableName => _parameter.Identifier + "VarParamsLength";

        public string TypeName => _parameter.Type.ToString();
        public bool IsRef => _parameter.Modifiers.Any(SyntaxKind.RefKeyword);
        public bool IsParams => _parameter.Modifiers.Any(SyntaxKind.ParamsKeyword);

        public bool IsVm => _parameter.AttributeLists.Any(x => x.Attributes.Any(attribute =>
        {
            // ParamAtt
            var isCommandAttr = attribute.Name.ToString() == nameof(FromVmAttribute) || attribute.Name.ToString() == nameof(FromVmAttribute).Substring(0, nameof(FromVmAttribute).Length - "Attribute".Length);
            return isCommandAttr;
        }));
        
        public bool IsOptional => _parameter.Default != null;

        public ExpressionSyntax OptionalExpr => _parameter.Default.Value;
        public byte TypeCode
        {
            get
            {
                if (IsVm) return TypeCodes.VM;

                var tn = TypeName;
                if (IsParams)
                {
                    tn = tn.Replace("[]", "");
                }
                
                return tn switch
                {
                    "int" => TypeCodes.INT,
                    "string" => TypeCodes.STRING,
                    "object" => TypeCodes.ANY,
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