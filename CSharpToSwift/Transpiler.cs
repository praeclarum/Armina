namespace CSharpToSwift;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

class Transpiler
{
    readonly string projectFilePath;
    readonly string swiftPackageName;
    readonly string swiftPackageDir;
    readonly string sourcesDir;
    readonly MSBuildWorkspace workspace = MSBuildWorkspace.Create();
    public Transpiler(string projectFilePath)
    {
        this.projectFilePath = Path.GetFullPath(projectFilePath);
        this.swiftPackageName = Path.GetFileNameWithoutExtension(projectFilePath);
        this.swiftPackageDir = Path.Combine("/Users/fak/Work/CSharpToSwift", swiftPackageName);
        this.sourcesDir = Path.Combine(swiftPackageDir, "Sources", swiftPackageName);
    }
    static Transpiler()
    {
        Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
        var _ = typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions);
    }
    readonly Dictionary<string, int> errorCounts = new Dictionary<string, int> ();
    void Error(string message)
    {
        if (!errorCounts.ContainsKey(message))
            errorCounts[message] = 0;
        errorCounts[message]++; 
    }
    void Info(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} {message}");
        Console.ResetColor();
    }
    
    public async Task TranspileAsync()
    {
        Directory.CreateDirectory(sourcesDir);
        var projectFileName = Path.GetFileName(projectFilePath);
        Info($"Loading project {projectFileName}...");
        var project = await workspace.OpenProjectAsync(projectFilePath);
        var projectLoadErrors = workspace.Diagnostics.Where(x => x.Kind == WorkspaceDiagnosticKind.Failure);
        foreach (var d in projectLoadErrors) {
            Error(d.Message);
        }
        if (projectLoadErrors.Any()) {
            return;
        }
        Info($"Analyzing project {project.Name}...");
        var compilation = await project.GetCompilationAsync();
        if (compilation is null) {
            Error("Failed to get compilation");
            return;
        }
        if (compilation.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error)) {
            foreach (var d in compilation.GetDiagnostics()) {
                Error(d.ToString());
            }
            return;
        }
        
        Info($"Transpiling...");
        var types = new List<(MemberDeclarationSyntax Syntax, SemanticModel Model)>();
        await GetTypeDeclarationsAsync (compilation, types);

        // var outputDir = System.IO.Path.GetDirectoryName(projectFilePath);
        TextWriter NewSwiftWriter(string swiftName) {
            var fileName = $"{swiftName}.swift";
            var filePath = System.IO.Path.Combine(sourcesDir, fileName);
            return new System.IO.StreamWriter(filePath);
        }
        using (var pw = new StreamWriter(Path.Combine(swiftPackageDir, "Package.swift"))) {
            pw.WriteLine($"// swift-tools-version: 5.6");
            pw.WriteLine();
            pw.WriteLine($"import PackageDescription");
            pw.WriteLine();
            pw.WriteLine($"let package = Package(");
            pw.WriteLine($"    name: \"{swiftPackageName}\",");
            pw.WriteLine($"    products: [");
            pw.WriteLine($"        .library(name: \"{swiftPackageName}\",");
            pw.WriteLine($"                 targets: [\"{swiftPackageName}\"])");
            pw.WriteLine($"    ],");
            pw.WriteLine($"    dependencies: [");
            pw.WriteLine($"    ],");
            pw.WriteLine($"    targets: [");
            pw.WriteLine($"        .target(name: \"{swiftPackageName}\",");
            pw.WriteLine($"                dependencies: []),");
            pw.WriteLine($"    ]");
            pw.WriteLine($")");
        }
        var swift = new StringWriter();
        swift.WriteLine("// This file was generated by CSharpToSwift");
        foreach (var (node, model) in types) {
            var symbol = (INamedTypeSymbol)model.GetDeclaredSymbol(node)!;
            var swiftName = GetSwiftTypeName(symbol);
            switch (node.Kind ()) {
                case SyntaxKind.ClassDeclaration:
                    var c = (ClassDeclarationSyntax)node;
                    using (var cw = NewSwiftWriter(swiftName)) {
                        TranspileClass(swiftName, c, symbol, model, cw);
                    }
                    break;
                case SyntaxKind.StructDeclaration:
                    var s = (StructDeclarationSyntax)node;
                    using (var sw = NewSwiftWriter(swiftName)) {
                        TranspileStruct(swiftName, s, symbol, model, sw);
                    }
                    break;
                case SyntaxKind.InterfaceDeclaration:
                    var i = (InterfaceDeclarationSyntax)node;
                    break;
                case SyntaxKind.EnumDeclaration:
                    var e = (EnumDeclarationSyntax)node;
                    break;
                case SyntaxKind.DelegateDeclaration:
                    var d = (DelegateDeclarationSyntax)node;
                    break;
            }
        }
        // Show errors sorted by count
        foreach (var kvp in errorCounts.OrderByDescending(x => x.Value)) {
            var count = kvp.Value;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} {kvp.Key} ({count}x)");
            Console.ResetColor();
        }
        Info("Done.");
    }

    void TranspileClass(string swiftName, ClassDeclarationSyntax node, INamedTypeSymbol symbol, SemanticModel model, TextWriter w)
    {
        w.Write($"class {swiftName}");
        var head = " : ";
        if (symbol.BaseType is {} baseType && !(baseType.Name == "Object" && baseType.ContainingNamespace.Name == "System")) {
            var baseSwiftName = GetSwiftTypeName(baseType);
            w.Write($"{head}{baseSwiftName}");
            head = ", ";
        }
        foreach (var i in symbol.Interfaces) {
            var baseSwiftName = GetSwiftTypeName(i);
            w.Write($"{head}{baseSwiftName}");
            head = ", ";
        }
        w.WriteLine($" {{");
        foreach (var member in node.Members) {
            TranspileClassOrStructMember(member, swiftName, node, symbol, model, w);
        }
        w.WriteLine($"}}");
    }

    void TranspileStruct(string swiftName, StructDeclarationSyntax node, INamedTypeSymbol symbol, SemanticModel model, TextWriter w)
    {
        w.Write($"struct {swiftName}");
        var head = " : ";
        foreach (var i in symbol.Interfaces) {
            var baseSwiftName = GetSwiftTypeName(i);
            w.Write($"{head}{baseSwiftName}");
            head = ", ";
        }
        w.WriteLine($" {{");
        foreach (var member in node.Members) {
            TranspileClassOrStructMember(member, swiftName, node, symbol, model, w);
        }
        w.WriteLine($"}}");
    }

    void TranspileClassOrStructMember(MemberDeclarationSyntax member, string typeName, TypeDeclarationSyntax node, INamedTypeSymbol symbol, SemanticModel model, TextWriter w)
    {
        switch (member.Kind ()) {
            // case SyntaxKind.ConstructorDeclaration:
            //     var ctor = (ConstructorDeclarationSyntax)member;
            //     TranspileConstructor(ctor, symbol, w);
            //     break;
            // case SyntaxKind.PropertyDeclaration:
            //     var prop = (PropertyDeclarationSyntax)member;
            //     TranspileProperty(prop, symbol, w);
            //     break;
            case SyntaxKind.MethodDeclaration:
                TranspileMethod((MethodDeclarationSyntax)member, symbol, model, w);
                break;
            // case SyntaxKind.EventDeclaration:
            //     var evt = (EventDeclarationSyntax)member;
            //     TranspileEvent(evt, symbol, w);
            //     break;
            // case SyntaxKind.IndexerDeclaration:
            //     var idx = (IndexerDeclarationSyntax)member;
            //     TranspileIndexer(idx, symbol, w);
            //     break;
            // case SyntaxKind.EventFieldDeclaration:
            //     var evtField = (EventFieldDeclarationSyntax)member;
            //     TranspileEventField(evtField, symbol, w);
            //     break;
            case SyntaxKind.FieldDeclaration:
                TranspileField((FieldDeclarationSyntax)member, symbol, model, w);
                break;
            // case SyntaxKind.ConstantFieldDeclaration:
            //     var constField = (ConstantFieldDeclarationSyntax)member;
            //     TranspileConstantField(constField, symbol, w);
            //     break;
            // case SyntaxKind.EnumMemberDeclaration:
            //     var enumMember = (EnumMemberDeclarationSyntax)member;
            //     TranspileEnumMember(enumMember, symbol, w);
            //     break;
            // case SyntaxKind.EventAccessorDeclaration:
            //     var evtAccessor = (EventAccessorDeclarationSyntax)member;
            //     TranspileEventAccessor(evtAccessor, symbol, w);
            //     break;
            default:
                // Error($"Unhandled member kind {member.Kind()}");
                break;
        }
    }

    void TranspileField(FieldDeclarationSyntax field, INamedTypeSymbol containerTypeSymbol, SemanticModel model, TextWriter w)
    {
        var docs = GetDocs(field);
        var type = model.GetSymbolInfo(field.Declaration.Type).Symbol;
        
        var ftypeName = GetSwiftTypeName(type);
        var isReadOnly = field.Modifiers.Any(x => x.IsKind(SyntaxKind.ReadOnlyKeyword));
        var isStatic = field.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword));
        var decl = isReadOnly ? (isStatic ? "static let" : "let") : (isStatic ? "static var" : "var");
        
        foreach (var v in field.Declaration.Variables)
        {
            var fieldSymbol = model.GetDeclaredSymbol(v);
            var acc = GetAcc(fieldSymbol);
            var vn = v.Identifier.ToString();
            var init = TranspileExpression(v.Initializer?.Value);
            if (!isReadOnly)
                init = GetDefaultValue(type);
            var typeSuffix = init == "nil" ? "?" : "";
            if (init is not null)
                init = " = " + init;
            if (docs.Length > 0)
                w.WriteLine($"    /// {docs}");
            w.WriteLine($"    {acc}{decl} {vn}: {ftypeName}{typeSuffix}{init}");
        }
    }

    void TranspileMethod(MethodDeclarationSyntax method, INamedTypeSymbol containerTypeSymbol, SemanticModel model, TextWriter w)
    {
        var docs = GetDocs(method);
        if (docs.Length > 0)
            w.WriteLine($"    /// {docs}");
        var returnType = model.GetSymbolInfo(method.ReturnType).Symbol;
        var isVoid = IsTypeVoid(returnType);
        var returnTypeCode = isVoid ? "" : $" -> {GetSwiftTypeName(returnType)}";
        var methodSymbol = model.GetDeclaredSymbol(method);
        var acc = GetAcc(methodSymbol);
        var isStatic = method.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword));
        var isOverride = method.Modifiers.Any(x => x.IsKind(SyntaxKind.OverrideKeyword));
        var isSealed = method.Modifiers.Any(x => x.IsKind(SyntaxKind.SealedKeyword));
        var isAbstract = method.Modifiers.Any(x => x.IsKind(SyntaxKind.AbstractKeyword));
        var isVirtual = method.Modifiers.Any(x => x.IsKind(SyntaxKind.VirtualKeyword));
        if (isAbstract) {
            Error("Abstract methods are not supported");
        }
        var slotType = isStatic ? "static " : (isOverride ? "override " : (isAbstract ? "/*abstract*/ " : (isVirtual ? "" : "final ")));
        w.Write($"    {acc}{slotType}func {method.Identifier.ToString()}(");
        var head = "";
        foreach (var p in method.ParameterList.Parameters)
        {
            var ptypeSymbol = model.GetSymbolInfo(p.Type).Symbol;
            var ptypeName = GetSwiftTypeName(ptypeSymbol);
            var pname = p.Identifier.ToString();
            w.Write($"{head}{pname}: {ptypeName}");
            head = ", ";
        }
        w.WriteLine($"){returnTypeCode} {{");
        w.WriteLine($"    }}");
    }

    static string GetDocs(CSharpSyntaxNode field)
    {
        var lines =
            field.GetLeadingTrivia()
            .Where(x => x.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) || x.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .SelectMany(x => x.ToFullString().Split('\n'))
            .Select(x =>
                x
                .Replace("<summary>", "")
                .Replace("</summary>", "")
                .Replace("<b>", "**")
                .Replace("</b>", "**")
                .Replace("///", "")
                .Replace("\t", " ")
                .Trim())
            .Where(x => x.Length > 0);
        return string.Join(" ", lines);
    }

    static string GetAcc(ISymbol? symbol)
    {
        if (symbol is null)
            return "";
        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Private => "private ",
            Accessibility.Protected => "",
            Accessibility.Internal => "internal ",
            Accessibility.Public => "",
            _ => "",
        };
    }

    string GetSwiftTypeName(ISymbol? s)
    {
        if (s == null) {
            return "AnyObject";
        }
        else if (s is IArrayTypeSymbol ats) {
            return $"[{GetSwiftTypeName(ats.ElementType)}]";
        }
        else {
            var name = s.Name;
            switch (name) {
                case nameof(System.Boolean):
                    return "Bool";
                case nameof(System.Byte):
                    return "UInt8";
                case nameof(System.Char):
                    return "Character";
                case nameof(System.IntPtr):
                    return "Int";
                case "Object":
                    return "AnyObject";
                case "Single":
                    return "Float";
                default:
                    if (string.IsNullOrEmpty(name)) {
                        Error($"Symbol {s} : {s.GetType()} has no name");
                        return "AnyObject";
                    }
                    return name;
            }
        }
    }

    bool IsTypeVoid(ISymbol? returnType)
    {
        return returnType is null || (returnType.Name == "Void" && returnType.ContainingNamespace.Name == "System");
    }

    string GetDefaultValue(ISymbol? type)
    {
        if (type == null) {
            return "nil";
        }
        switch (type.Kind) {
            case SymbolKind.ArrayType:
                return "[]";
            case SymbolKind.PointerType:
                return "nil";
            case SymbolKind.DynamicType:
                return "nil";
            case SymbolKind.TypeParameter:
                return "nil";
            case SymbolKind.ErrorType:
                return "nil";
            case SymbolKind.NamedType:
                var ntype = (INamedTypeSymbol)type;
                switch (type.Name) {
                    case nameof(System.Boolean):
                        return "false";
                    case nameof(System.Byte):
                        return "0";
                    case nameof(System.Double):
                        return "0.0";
                    case nameof(System.Single):
                        return "0.0";
                    case nameof(System.Int16):
                        return "0";
                    case nameof(System.Int32):
                        return "0";
                    case nameof(System.Int64):
                        return "0";
                    case nameof(System.IntPtr):
                        return "0";
                    case nameof(System.UInt16):
                        return "0";
                    case nameof(System.UInt32):
                        return "0";
                    case nameof(System.UInt64):
                        return "0";
                    case nameof(System.UIntPtr):
                        return "0";
                    default:
                        if (ntype.IsReferenceType) {
                            return "nil";
                        }
                        else {
                            Error($"Unhandled default value for named type: {type.Name}");
                            return $"0/*NT:{type.Name}*/";
                        }
                }
            default:
                Error($"Unhandled default value for type {type.Kind}");
                return $"nil/*T:{type.Kind}*/";
        }
    }

    string? TranspileExpression(ExpressionSyntax? value)
    {
        if (value is null) {
            return null;
        }
        switch (value.Kind ()) {
            case SyntaxKind.FalseLiteralExpression:
                return "false";
            case SyntaxKind.TrueLiteralExpression:
                return "true";
            case SyntaxKind.NumericLiteralExpression:
                var nlit = (LiteralExpressionSyntax)value;
                return nlit.Token.ValueText;
            case SyntaxKind.StringLiteralExpression:
                var slit = (LiteralExpressionSyntax)value;
                {
                    var stext = slit.GetText().ToString();
                    if (stext.Length > 0 && stext[0] == '@') {
                        stext = "\"\"\"\n" + slit.Token.ValueText + "\n\"\"\"";
                    }
                    return stext;
                }
            case SyntaxKind.SimpleMemberAccessExpression:
                var sma = (MemberAccessExpressionSyntax)value;
                return $"{TranspileExpression(sma.Expression)}.{sma.Name.ToString()}";
            case SyntaxKind.IdentifierName:
                var id = (IdentifierNameSyntax)value;
                return id.ToString();
            case SyntaxKind.InvocationExpression:
                var inv = (InvocationExpressionSyntax)value;
                var args = inv.ArgumentList.Arguments.Select(a => TranspileExpression(a.Expression)).ToArray();
                return $"{TranspileExpression(inv.Expression)}({string.Join(", ", args)})";
            // case SyntaxKind.ObjectCreationExpression:
            //     var obj = (ObjectCreationExpressionSyntax)value;
            //     var args2 = obj.ArgumentList.Arguments.Select(a => TranspileExpression(a.Expression)).ToArray();
            //     return $"{obj.Type.ToString()}({string.Join(", ", args2)})";
            case SyntaxKind.CastExpression:
                var cast = (CastExpressionSyntax)value;
                return $"{TranspileExpression(cast.Expression)} as {cast.Type}";
            case SyntaxKind.NullLiteralExpression:
                return "nil";
            case SyntaxKind.ThisExpression:
                return "self";
            case SyntaxKind.ParenthesizedExpression:
                var paren = (ParenthesizedExpressionSyntax)value;
                return $"({TranspileExpression(paren.Expression)})";
            default:
                Error($"Unhandled expression kind {value.Kind()}");
                return $"nil/*E:{value.Kind()}*/";
        }
    }

    async Task GetTypeDeclarationsAsync(Compilation compilation, List<(MemberDeclarationSyntax Syntax, SemanticModel Symbol)> types)
    {
        foreach (var s in compilation.SyntaxTrees.OfType<CSharpSyntaxTree>()) {
            // Info($"Transpiling {s.FilePath}...");
            var m = compilation.GetSemanticModel(s);
            GetTypeDeclarations(await s.GetRootAsync().ConfigureAwait(false), m, compilation, types);
        }
    }

    void GetTypeDeclarations(CSharpSyntaxNode node, SemanticModel model, Compilation compilation, List<(MemberDeclarationSyntax Syntax, SemanticModel Symbol)> types)
    {
        switch (node.Kind ()) {
            case SyntaxKind.ClassDeclaration:
                var c = (ClassDeclarationSyntax)node;
                if (model.GetDeclaredSymbol(c) is INamedTypeSymbol ctype) {
                    Info($"Found class {ctype.ContainingNamespace}.{ctype.Name}");
                    types.Add((c, model));
                }
                break;
            case SyntaxKind.StructDeclaration:
                var s = (StructDeclarationSyntax)node;
                if (model.GetDeclaredSymbol(s) is INamedTypeSymbol stype) {
                    Info($"Found struct {stype.ContainingNamespace}.{stype.Name}");
                    types.Add((s, model));
                }
                break;
            case SyntaxKind.InterfaceDeclaration:
                var i = (InterfaceDeclarationSyntax)node;
                if (model.GetDeclaredSymbol(i) is INamedTypeSymbol itype) {
                    Info($"Found interface {itype.ContainingNamespace}.{itype.Name} {itype.GetType()}");
                    types.Add((i, model));
                }
                break;
            case SyntaxKind.EnumDeclaration:
                var e = (EnumDeclarationSyntax)node;
                if (model.GetDeclaredSymbol(e) is INamedTypeSymbol etype) {
                    Info($"Found enum {etype.ContainingNamespace}.{etype.Name}");
                    types.Add((e, model));
                }
                break;
            case SyntaxKind.DelegateDeclaration:
                var d = (DelegateDeclarationSyntax)node;
                if (model.GetDeclaredSymbol(d) is INamedTypeSymbol dtype) {
                    Info($"Found delegate {dtype.ContainingNamespace}.{dtype.Name}");
                    types.Add((d, model));
                }
                break;
            case SyntaxKind.NamespaceDeclaration:
                var n = (NamespaceDeclarationSyntax)node;
                foreach (var m in n.Members) {
                    GetTypeDeclarations(m, model, compilation, types);
                }
                break;
            case SyntaxKind.CompilationUnit:
                var cu = (CompilationUnitSyntax)node;
                foreach (var m in cu.Members) {
                    GetTypeDeclarations(m, model, compilation, types);
                }
                break;
            default:
                break;
        }
    }
}
