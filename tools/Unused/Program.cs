using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Unused
{
    // Usage: Unused <repo root>
    // Compiles src/ and tests/ together, collects every symbol that is referred to, and prints the members and types declared in src/ that
    // nothing in src/ refers to. Overrides, interface members, the messages Unity calls, the interface for mods (src/Api) and names that
    // appear in a string are marked or left out. Members that only the tests use are printed as "test".
    internal static class Program
    {
        private static readonly HashSet<string> UnityMessages = new HashSet<string>
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnDestroy", "OnEnable", "OnDisable", "OnApplicationQuit",
            "OnApplicationFocus", "OnApplicationPause", "OnPreRender", "OnPostRender", "OnRenderImage", "OnPreCull", "OnWillRenderObject",
            "OnRenderObject", "OnValidate", "Reset", "OnTriggerEnter", "OnCollisionEnter", "OnDrawGizmos", "OnBecameVisible", "OnBecameInvisible",
            "OnPointerClick", "OnPointerEnter", "OnPointerExit", "OnPointerDown", "OnPointerUp", "OnDrag", "OnBeginDrag", "OnEndDrag", "OnScroll",
            "OnSelect", "OnDeselect", "OnMove", "OnSubmit", "OnCancel", "OnUpdateSelected", "OnInitializePotentialDrag", "Main", "OnLoad", "OnSave",
        };

        // Unused <repo root> --delete <file of "path:line" entries>: removes the declarations that start at those lines, with the comment above.
        private static int Delete(string root, string listFile)
        {
            var byFile = new Dictionary<string, List<int>>();
            foreach (string entry in File.ReadAllLines(listFile))
            {
                string text = entry.Trim();
                if (text.Length == 0) continue;
                int colon = text.LastIndexOf(':');
                string file = text.Substring(0, colon);
                int line = int.Parse(text.Substring(colon + 1));
                List<int> lines;
                if (!byFile.TryGetValue(file, out lines)) byFile[file] = lines = new List<int>();
                lines.Add(line);
            }
            foreach (KeyValuePair<string, List<int>> pair in byFile)
            {
                string path = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                string source = File.ReadAllText(path);
                SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3, preprocessorSymbols: new[] { "DEVELOPMENT_BUILD" }));
                SyntaxNode rootNode = tree.GetRoot();
                var doomed = new List<SyntaxNode>();
                foreach (SyntaxNode node in rootNode.DescendantNodes())
                {
                    int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (!pair.Value.Contains(line)) continue;
                    if (node is MethodDeclarationSyntax || node is PropertyDeclarationSyntax || node is TypeDeclarationSyntax || node is ConstructorDeclarationSyntax
                        || node is EnumDeclarationSyntax || node is DelegateDeclarationSyntax)
                        doomed.Add(node);
                    else if (node is VariableDeclaratorSyntax)
                    {
                        var declaration = (VariableDeclarationSyntax)node.Parent;
                        doomed.Add(declaration.Variables.Count == 1 ? declaration.Parent : node);
                    }
                }
                // A declaration inside another one that goes is gone with it.
                doomed = doomed.Where(n => !doomed.Any(o => o != n && o.Span.Contains(n.Span))).Distinct().ToList();
                SyntaxNode result = rootNode.RemoveNodes(doomed, SyntaxRemoveOptions.KeepNoTrivia);
                File.WriteAllText(path, result.ToFullString());
                Console.WriteLine("deleted " + doomed.Count + " in " + pair.Key);
            }
            return 0;
        }

        private static int Main(string[] args)
        {
            if (args.Length > 2 && args[1] == "--delete") return Delete(Path.GetFullPath(args[0]), args[2]);
            string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            var parse = new CSharpParseOptions(LanguageVersion.CSharp7_3, preprocessorSymbols: new[] { "DEVELOPMENT_BUILD" });
            var srcTrees = new List<SyntaxTree>();
            var testTrees = new List<SyntaxTree>();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                srcTrees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file));
            }
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tests", "ReDefinition.Tests"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                testTrees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file));
            }
            var references = new List<MetadataReference>();
            string packages = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".nuget", "packages", "microsoft.netframework.referenceassemblies.net48");
            string refs = Directory.GetDirectories(packages).OrderBy(d => d).Last();
            string facade = Path.Combine(refs, "build", ".NETFramework", "v4.8");
            foreach (string dll in Directory.GetFiles(facade, "*.dll"))
                if (!dll.Contains("EnterpriseServices.Wrapper") && !dll.Contains("EnterpriseServices.Thunk")) references.Add(MetadataReference.CreateFromFile(dll));
            string managed = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\KSP_x64_Data\Managed";
            foreach (string dll in Directory.GetFiles(managed, "*.dll"))
            {
                string name = Path.GetFileName(dll);
                if (name.StartsWith("System") || name == "mscorlib.dll" || name.StartsWith("Mono.") || name.StartsWith("Microsoft.")) continue;
                references.Add(MetadataReference.CreateFromFile(dll));
            }
            references.Add(MetadataReference.CreateFromFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\GameData\000_Harmony\0Harmony.dll"));
            string testBin =Path.Combine(root, "tests", "ReDefinition.Tests", "bin", "Debug", "net48");
            foreach (string dll in Directory.GetFiles(testBin, "Microsoft.VisualStudio.TestPlatform.*.dll")) references.Add(MetadataReference.CreateFromFile(dll));

            var all = new List<SyntaxTree>(srcTrees);
            all.AddRange(testTrees);
            CSharpCompilation compilation = CSharpCompilation.Create("All", all, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            // Where the code does not compile, the uses in it are not seen and members look unused; the errors are listed first.
            int errors = 0;
            foreach (Diagnostic d in compilation.GetDiagnostics())
                if (d.Severity == DiagnosticSeverity.Error && errors++ < 15) Console.Error.WriteLine("compile error: " + d);
            Console.Error.WriteLine(errors + " compile errors");
            var usedBySrc = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var usedByTests = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var srcSet = new HashSet<SyntaxTree>(srcTrees);
            foreach (SyntaxTree tree in all)
            {
                SemanticModel model = compilation.GetSemanticModel(tree);
                HashSet<ISymbol> into = srcSet.Contains(tree) ? usedBySrc : usedByTests;
                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                {
                    if (node is SimpleNameSyntax || node is ObjectCreationExpressionSyntax || node is ConstructorInitializerSyntax
                        || node is ElementAccessExpressionSyntax || node is AttributeSyntax || node is BinaryExpressionSyntax
                        || node is PrefixUnaryExpressionSyntax || node is PostfixUnaryExpressionSyntax || node is AssignmentExpressionSyntax
                        || node is AwaitExpressionSyntax)
                    {
                        SymbolInfo info = model.GetSymbolInfo(node);
                        Add(into, info.Symbol);
                        foreach (ISymbol candidate in info.CandidateSymbols) Add(into, candidate);
                    }
                    ForEachStatementSyntax each = node as ForEachStatementSyntax;
                    if (each != null)
                    {
                        ForEachStatementInfo info = model.GetForEachStatementInfo(each);
                        Add(into, info.GetEnumeratorMethod); Add(into, info.MoveNextMethod); Add(into, info.CurrentProperty); Add(into, info.DisposeMethod);
                    }
                }
            }

            string stringsText = ReadStrings(root);
            var report = new List<string>();
            foreach (SyntaxTree tree in srcTrees)
            {
                SemanticModel model = compilation.GetSemanticModel(tree);
                string path = tree.FilePath.Substring(root.Length + 1).Replace('\\', '/');
                if (path.StartsWith("src/Api/")) continue;
                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                {
                    ISymbol symbol = null;
                    if (node is MethodDeclarationSyntax || node is PropertyDeclarationSyntax || node is TypeDeclarationSyntax
                        || node is EnumDeclarationSyntax || node is DelegateDeclarationSyntax || node is ConstructorDeclarationSyntax)
                        symbol = model.GetDeclaredSymbol(node);
                    else if (node is VariableDeclaratorSyntax && node.Parent != null && node.Parent.Parent is FieldDeclarationSyntax)
                        symbol = model.GetDeclaredSymbol(node);
                    else if (node is VariableDeclaratorSyntax && node.Parent != null && node.Parent.Parent is EventFieldDeclarationSyntax)
                        symbol = model.GetDeclaredSymbol(node);
                    if (symbol == null || symbol.IsImplicitlyDeclared) continue;
                    if (!Candidate(symbol)) continue;
                    ISymbol def = symbol.OriginalDefinition;
                    if (usedBySrc.Contains(def)) continue;
                    bool tests = usedByTests.Contains(def);
                    if (symbol is INamedTypeSymbol && TypeInUse((INamedTypeSymbol)symbol, usedBySrc)) continue;
                    IMethodSymbol method = symbol as IMethodSymbol;
                    if (method != null && method.MethodKind == MethodKind.Constructor && usedBySrc.Contains(symbol.ContainingType.OriginalDefinition)) continue;
                    string name = symbol.Name;
                    int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    string kind = name.Length > 3 && Regex.IsMatch(stringsText, "\\b" + Regex.Escape(name) + "\\b") ? "string  " : tests ? "test    " : "unused  ";
                    report.Add(kind + path + ":" + line + "  " + Describe(symbol));
                }
            }
            foreach (string line in report.Distinct().OrderBy(l => l, StringComparer.Ordinal)) Console.WriteLine(line);
            return 0;
        }

        private static void Add(HashSet<ISymbol> set, ISymbol symbol)
        {
            if (symbol == null) return;
            set.Add(symbol.OriginalDefinition);
            IMethodSymbol method = symbol as IMethodSymbol;
            if (method != null && method.ReducedFrom != null) set.Add(method.ReducedFrom.OriginalDefinition);
            if (method != null && method.PartialDefinitionPart != null) set.Add(method.PartialDefinitionPart.OriginalDefinition);
        }

        // A type counts as used where one of its nested types is.
        private static bool TypeInUse(INamedTypeSymbol type, HashSet<ISymbol> used)
        {
            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                if (used.Contains(nested.OriginalDefinition) || TypeInUse(nested, used)) return true;
            return false;
        }

        private static bool Candidate(ISymbol symbol)
        {
            if (symbol.IsOverride) return false;
            if (symbol.Name.StartsWith("<")) return false;
            if (symbol.ContainingType != null && symbol.ContainingType.TypeKind == TypeKind.Interface) return false;
            IMethodSymbol method = symbol as IMethodSymbol;
            if (method != null)
            {
                if (method.MethodKind != MethodKind.Ordinary && method.MethodKind != MethodKind.Constructor) return false;
                if (method.MethodKind == MethodKind.Constructor && method.Parameters.Length == 0) return false;
                if (method.ExplicitInterfaceImplementations.Length > 0 || method.IsExtern) return false;
                if (UnityMessages.Contains(method.Name)) return false;
                if (ImplementsInterface(method)) return false;
                foreach (AttributeData a in method.GetAttributes())
                    if (a.AttributeClass.Name.StartsWith("DllImport") || a.AttributeClass.Name.Contains("Test")) return false;
            }
            IPropertySymbol property = symbol as IPropertySymbol;
            if (property != null)
            {
                if (property.ExplicitInterfaceImplementations.Length > 0 || ImplementsInterface(property)) return false;
                if (property.IsIndexer) return false;
            }
            IFieldSymbol field = symbol as IFieldSymbol;
            if (field != null)
            {
                if (field.GetAttributes().Length > 0) return false;
                if (field.ContainingType.TypeKind == TypeKind.Enum) return false;
            }
            INamedTypeSymbol type = symbol as INamedTypeSymbol;
            if (type != null)
            {
                foreach (AttributeData a in type.GetAttributes())
                    if (a.AttributeClass.Name.StartsWith("KSPAddon") || a.AttributeClass.Name.StartsWith("KSPScenario") || a.AttributeClass.Name.Contains("Test")) return false;
                for (INamedTypeSymbol baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
                    if (baseType.Name == "ScenarioModule" || baseType.Name == "PartModule" || baseType.Name == "Attribute") return false;
                if (type.TypeKind == TypeKind.Enum) return false;
            }
            return true;
        }

        private static bool ImplementsInterface(ISymbol member)
        {
            INamedTypeSymbol containing = member.ContainingType;
            foreach (INamedTypeSymbol iface in containing.AllInterfaces)
                foreach (ISymbol implemented in iface.GetMembers())
                    if (SymbolEqualityComparer.Default.Equals(containing.FindImplementationForInterfaceMember(implemented), member)) return true;
            return false;
        }

        private static string Describe(ISymbol symbol)
        {
            string kind = symbol is INamedTypeSymbol ? "type" : symbol is IMethodSymbol ? "method" : symbol is IPropertySymbol ? "property" : "field";
            string owner = symbol.ContainingType != null && !(symbol is INamedTypeSymbol) ? symbol.ContainingType.Name + "." : "";
            string access = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
            return kind + " " + access + " " + owner + symbol.Name;
        }

        // The text of every string literal in src, and of the configs that name members by string.
        private static string ReadStrings(string root)
        {
            var text = new System.Text.StringBuilder();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(?:[^\"\\\\\\n]|\\\\.)*\"")) text.Append(m.Value).Append(' ');
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "GameData"), "*.cfg", SearchOption.AllDirectories)) text.Append(File.ReadAllText(file)).Append(' ');
            return text.ToString();
        }
    }
}
