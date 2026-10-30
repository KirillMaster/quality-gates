namespace KirillMaster.QualityGates.Analyzers;

public static class ReadabilityDescriptors
{
    public const int MaxArrangeStatements = 15;
    public const int WhyCommentCognitiveThreshold = 10;
    public const int ComposerPercent = 80;
    public const int WorkerPercent = 20;
    public const int MinOwnStatements = 3;
    public const int MinLowStatements = 3;
    public const string TestPattern = @"^[A-Z]\w*_\w+_\w+$";
    public const string TestLogicExemptAssemblies = "ArchitectureTests,IntegrationTests";
    public const string PortNamespaceSegments = "Ports";
    public const string AllowedMockTypes = "Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Options.IOptions";
    public const string CoreNamespaceSegments = "Core,Domain,Application";
    public const string InputPortNamespace = "Ports.Input";
    public const string BoolPrefixes = "Is,Has,Can,Should,Are,Was,Will,Did,Does,Must,Allow,Allows,Supports,Contains,Exists,Equals,Matches,Try,Starts,Ends,Needs,Requires";
    public const string DigitSuffixAllowed = "Sha1,Sha256,Sha384,Sha512,Md5,Base64,Utf8,Utf16,Utf32,Int16,Int32,Int64,Vector2,Vector3,Ipv4,Ipv6,X509,Oauth2,Http2,Http3,Rs256,Es256,Crc32,Win32,S3,Ec2,B2b,B2c";
    public const string TempMarkers = "FIXME,HACK,XXX,temporary,temporarily,workaround,for now,remove after,remove once,remove when,quick fix,kludge";
    public const string DictionaryFileName = "glossary.txt";

    public static readonly DiagnosticDescriptor NoLogicInTests = Create("QG0170", "Logic in test", "Test '{0}' contains '{1}'; use [Theory] data or a helper instead of branching", "Testing");
    public static readonly DiagnosticDescriptor MockOwnPortsOnly = Create("QG0171", "Mock of a non-port type", "'{0}' is not an interface from an own Ports namespace; use a fake or stub at the real boundary", "Testing");
    public static readonly DiagnosticDescriptor ArrangeTooLong = Create("QG0172", "Arrange section is too long", "Test '{0}' has {1} arrange statements (max {2}); extract a builder or fixture", "Testing");
    public static readonly DiagnosticDescriptor TestNamePattern = Create("QG0173", "Test name does not describe behavior", "Test '{0}' does not match '{1}'", "Testing");
    public static readonly DiagnosticDescriptor UnknownWord = Create("QG0174", "Unknown word in identifier", "Identifier '{0}' contains the unknown word '{1}'; use a known word or add it to the glossary", "Naming");
    public static readonly DiagnosticDescriptor NoRegion = Create("QG0175", "#region directive", "#region hides structure; split the type instead", "Hygiene");
    public static readonly DiagnosticDescriptor AuthorByline = Create("QG0176", "Author byline comment", "Comment is an author byline or change log entry; version control keeps history", "Hygiene");
    public static readonly DiagnosticDescriptor SuppressionWithoutJustification = Create("QG0177", "Suppression without justification", "{0} must state a reason", "Hygiene");
    public static readonly DiagnosticDescriptor WhyCommentMissing = Create("QG0178", "Complex method without why-comment", "Method '{0}' has cognitive complexity {1} (threshold {2}) and no comment explaining why", "Hygiene");
    public static readonly DiagnosticDescriptor TempMarker = Create("QG0179", "Temporary marker", "'{0}' marks a temporary fix; fix the root cause", "Hygiene");
    public static readonly DiagnosticDescriptor BoolWithoutPrefix = Create("QG0180", "Boolean name without prefix", "Boolean '{0}' should start with Is/Has/Can/Should or similar", "Naming");
    public static readonly DiagnosticDescriptor DigitSuffix = Create("QG0181", "Digit-suffixed name", "Identifier '{0}' is distinguished only by a digit; name the difference", "Naming");
    public static readonly DiagnosticDescriptor SimilarNames = Create("QG0182", "Near-identical names", "'{0}' and '{1}' differ by one character in the same scope", "Naming");
    public static readonly DiagnosticDescriptor NameLiesAboutType = Create("QG0183", "Name lies about type", "'{0}' suggests {1} but its type is '{2}'", "Naming");
    public static readonly DiagnosticDescriptor MixedAbstraction = Create("QG0184", "Method mixes composition and low-level work", "Method '{0}' mixes {1} own-code steps with {2} low-level steps ({3}% composer)", "Abstraction");
    public static readonly DiagnosticDescriptor HandlerDoesWork = Create("QG0185", "Handler does low-level work", "Handler method '{0}' contains {1}; move it into a named domain method", "Abstraction");
    public static readonly DiagnosticDescriptor HelperAboveCaller = Create("QG0186", "Helper above its caller", "Helper '{0}' is declared above its caller '{1}'; place it below (stepdown)", "Abstraction");
    public static readonly DiagnosticDescriptor LawOfDemeter = Create("QG0187", "Law of Demeter", "Chain '{0}' reaches through {1} collaborators; ask the nearest one to do the work", "Abstraction");

    private static DiagnosticDescriptor Create(string id, string title, string message, string category) =>
        new(id, title, message, "QualityGates." + category, DiagnosticSeverity.Warning, true,
            helpLinkUri: "https://github.com/KirillMaster/quality-gates/blob/main/docs/analyzers.md#" + id.ToLowerInvariant());
}
