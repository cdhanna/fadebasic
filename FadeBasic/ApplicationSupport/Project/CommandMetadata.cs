namespace FadeBasic.ApplicationSupport.Project;

public class CommandMetadata
{
    public List<string> classDocStrings = new List<string>();
    public List<ProjectCommandMetadata> commands = new List<ProjectCommandMetadata>();
}

public class ProjectCommandParameterMetedata
{
    public bool isOptional;
    public bool isParams;
    public bool isRaw;
    public bool isRef;
    public bool isVm;
    public int typeCode;
    public string typeName;
}

public class ProjectCommandMetadata
{
    public string docString;

    public string methodName;

    public string returnType;

    public int returnTypeCode;

    public string callName;

    public string sig;

    public int methodIndex;

    public List<ProjectCommandParameterMetedata> parameters = new List<ProjectCommandParameterMetedata>();
// ""MethodName"": ""Call_HiAna_voidR0"",
// ""ReturnType"": ""void"",
// ""ReturnTypeCode"": ""8"",
// ""CallName"": """"ana"""",
// ""Sig"": ""voidR0"",
}