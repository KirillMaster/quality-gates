namespace KirillMaster.QualityGates.Analyzers;

public static class ShapeDescriptors
{
    public const string PortSegments = "Ports";
    public const string CoreSegments = "Core,Domain,Application";
    public const string ModelSegments = "Model,Models,ValueObjects,Entities";
    public const string ContractSegments = "Contracts,Messages,Dtos";
    public const string AdapterSegments = "Adapters,Runner";
    public const string IdNamePattern = "^(id|ids)$|(Id|Ids|Guid|Uuid)$";
    public const string MoneyNamePattern = "^(amount|price|cost|total|balance|fee|fees|money|salary|charge)$|(Amount|Price|Cost|Total|Balance|Fee|Fees|Money|Salary|Charge)$";
    public const string TechnologyWords = "Sql,Postgres,Postgresql,Dynamo,DynamoDb,Db,Sqs,Sns,Http,Redis,Json,Ef,Grpc,S3,Dto,Mongo,Kafka,Rabbit";
    public const string PersistenceAttributes = "Column,Key,Table,NotMapped,ForeignKey,InverseProperty,DatabaseGenerated,Index,Owned,Keyless,JsonPropertyName,JsonIgnore,JsonProperty,JsonConverter,JsonInclude,JsonConstructor,DataMember,DataContract,XmlElement,XmlAttribute,XmlIgnore,BsonElement,BsonId,DynamoDBProperty,DynamoDBHashKey,DynamoDBTable";
    public const string PortTechnologyPrefixes = "System.Net.Http,System.Net.Sockets,System.Data,System.IO,System.Text.Json,System.Xml,Newtonsoft.Json,Npgsql,Microsoft.Data.SqlClient,System.Data.SqlClient,Amazon,Microsoft.AspNetCore,StackExchange.Redis,Grpc,MassTransit,RabbitMQ,Confluent.Kafka,MongoDB";
    public const string PortWhitelist = "System.Threading.Tasks.Task,System.Threading.Tasks.ValueTask,System.Threading.CancellationToken,System.Collections.Generic.IAsyncEnumerable,System.Collections.Generic.IEnumerable,System.Collections.Generic.IReadOnlyList,System.Collections.Generic.IReadOnlyCollection,System.Collections.Generic.IReadOnlyDictionary,System.Collections.Generic.KeyValuePair,System.Collections.Immutable.ImmutableArray,System.Collections.Immutable.ImmutableList,System.Collections.Immutable.ImmutableDictionary,System.Nullable,System.Guid,System.DateTime,System.DateTimeOffset,System.TimeSpan,System.DateOnly,System.TimeOnly,System.ValueTuple,System.Uri";
    public const string PortAllowedAssemblies = "Contracts,Results";
    public const string SealedSegments = "Core,Domain,Application,Adapters,Runner";
    public const string ResultTypeNames = "Result";
    public const string FactoryNames = "Create";
    public const string CqsExemptPrefixes = "Try,Pop,Dequeue,Next,Increment,Generate";
    public const string EnumSwitchExemptSegments = "Adapters,Mappers,Mapping,Runner";
    public const string TunableNamePattern = "(?i)(timeout|ttl|limit|max|min|threshold|retr(y|ies)|interval|delay|duration|expir|size|count|capacity|batch|period|attempt)";
    public const string UrlHostSuffixes = "com,net,org,io,ru,dev,cloud,local,internal,aws";

    public static readonly DiagnosticDescriptor PrimitiveInPort = Create("QG0130", "Primitive id or money in port", "Parameter '{0}' of port method '{1}' is a primitive {2}; use a value object", "Shape");
    public static readonly DiagnosticDescriptor PublicCoreType = Create("QG0131", "Public type in Core", "Type '{0}' is public but not part of a port signature; make it internal", "Shape");
    public static readonly DiagnosticDescriptor UnsealedClass = Create("QG0132", "Class is not sealed", "Class '{0}' has no derived types and no virtual members; seal it", "Shape");
    public static readonly DiagnosticDescriptor PublicSetterInModel = Create("QG0133", "Public setter in domain model", "Member '{0}' of model type '{1}' is publicly mutable; use init, a private setter or a behavior method", "Shape");
    public static readonly DiagnosticDescriptor HybridModelType = Create("QG0134", "Hybrid model type", "Model type '{0}' mixes public mutable properties with behavior; split data from logic", "Shape");
    public static readonly DiagnosticDescriptor LeakyPort = Create("QG0135", "Port leaks a technology collection or queryable", "Port method '{0}' exposes '{1}'; use read-only abstractions and materialized results", "Shape");
    public static readonly DiagnosticDescriptor OptionsWithoutValidateOnStart = Create("QG0136", "Options without ValidateOnStart", "'{0}' registers options without '.ValidateOnStart()'; use AddOptions<T>().Bind(...).Validate...().ValidateOnStart()", "Shape");
    public static readonly DiagnosticDescriptor PortWithoutCancellation = Create("QG0137", "Port method without CancellationToken", "Asynchronous port method '{0}' must take a CancellationToken", "Shape");
    public static readonly DiagnosticDescriptor EnumSwitchOutsideOwner = Create("QG0138", "Enum switch outside the owning feature", "Switch over enum '{0}' outside namespace '{1}' that declares it; move the behavior next to the enum", "Shape");
    public static readonly DiagnosticDescriptor ValueObjectShape = Create("QG0139", "Value object is not parse-don't-validate", "Value object '{0}' {1}", "Shape");
    public static readonly DiagnosticDescriptor ContractShape = Create("QG0140", "Contract is not an immutable record", "Contract '{0}' {1}", "Shape");
    public static readonly DiagnosticDescriptor TechnologyWord = Create("QG0141", "Technology word in Core/port name", "Name '{0}' contains the technology word '{1}'; name it after the domain concept", "Shape");
    public static readonly DiagnosticDescriptor PersistenceAttributeInModel = Create("QG0142", "Persistence/serialization attribute in model", "Attribute '{0}' on '{1}' ties the domain model to a technology; map in the adapter", "Shape");
    public static readonly DiagnosticDescriptor TechnologyTypeInPort = Create("QG0143", "Technology type in port signature", "Port method '{0}' exposes technology type '{1}'", "Shape");
    public static readonly DiagnosticDescriptor MoneyNotDecimal = Create("QG0144", "Money is not decimal", "Money member '{0}' is '{1}'; use decimal or a Money value object", "Shape");
    public static readonly DiagnosticDescriptor RoundWithoutMidpoint = Create("QG0145", "Rounding without MidpointRounding", "'{0}' must pass an explicit MidpointRounding mode", "Shape");
    public static readonly DiagnosticDescriptor PortTypeNotWhitelisted = Create("QG0146", "Type not allowed in port signature", "Port method '{0}' uses '{1}', which is not a primitive, value object, contract or whitelisted type", "Shape");
    public static readonly DiagnosticDescriptor CommandQuerySeparation = Create("QG0147", "Method both returns a value and mutates state", "Method '{0}' returns a value and mutates '{1}'; split into a command and a query", "Shape");
    public static readonly DiagnosticDescriptor MagicNumber = Create("QG0150", "Hardcoded number", "Numeric literal '{0}' is hardcoded; read it from configuration or mark an invariant constant with [Invariant(InvariantKind.X)]", "NoHardcode");
    public static readonly DiagnosticDescriptor TimeSpanLiteral = Create("QG0151", "Hardcoded time span", "'{0}' hardcodes a duration; read it from configuration or mark an invariant with [Invariant(InvariantKind.X)]", "NoHardcode");
    public static readonly DiagnosticDescriptor HardcodedUrl = Create("QG0152", "Hardcoded URL or host", "String literal '{0}' hardcodes an endpoint; read it from configuration or mark a protocol constant with [Invariant(InvariantKind.Protocol)]", "NoHardcode");

    private static DiagnosticDescriptor Create(string id, string title, string message, string category) =>
        new(id, title, message, "QualityGates." + category, DiagnosticSeverity.Warning, true,
            helpLinkUri: "https://github.com/KirillMaster/quality-gates/blob/main/docs/analyzers.md#" + id.ToLowerInvariant());
}
