using System.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace ICSharpCode.CodeConverter.Common;

internal static class VisualBasicModuleReferenceUpdater
{
    public static async Task<ConversionResult[]> UpdateAsync(IReadOnlyCollection<Project> projectsToConvert, ConversionOptions options, CancellationToken cancellationToken)
    {
        var solution = projectsToConvert.First().Solution;
        var convertedIds = new HashSet<ProjectId>(projectsToConvert.Select(project => project.Id));
        var convertedAssemblies = new HashSet<AssemblyIdentity>();
        foreach (var convertedProject in projectsToConvert) {
            var compilation = await convertedProject.GetCompilationAsync(cancellationToken);
            if (compilation == null) throw new InvalidOperationException($"Cannot analyze project '{convertedProject.Name}'.");
            convertedAssemblies.Add(compilation.Assembly.Identity);
        }
        var documentsByPath = new Dictionary<string, (string Original, string Updated, Encoding Encoding)>(StringComparer.OrdinalIgnoreCase);
        var convertedPaths = new HashSet<string>(projectsToConvert.SelectMany(project => project.Documents)
            .Where(document => document.FilePath != null).Select(document => Path.GetFullPath(document.FilePath)), StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.VisualBasic && !convertedIds.Contains(project.Id))) {
            foreach (var document in project.Documents.Where(document => document.FilePath != null)) {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null) throw new InvalidOperationException($"Cannot analyze VB document '{document.FilePath}'.");

                var replacements = new Dictionary<SyntaxNode, (SyntaxNode Replacement, SyntaxAnnotation Annotation, ISymbol Symbol)>();
                foreach (var name in root.DescendantNodes().OfType<VBSyntax.SimpleNameSyntax>()) {
                    var symbolInfo = semanticModel.GetSymbolInfo(name, cancellationToken);
                    if (symbolInfo.Symbol == null && symbolInfo.CandidateSymbols.Any(candidate => candidate.IsModuleMember() &&
                        convertedAssemblies.Contains(candidate.ContainingAssembly.Identity))) {
                        throw new InvalidOperationException($"Cannot resolve module reference '{name}' in '{document.FilePath}'.");
                    }
                    var symbol = symbolInfo.Symbol;
                    if (!symbol.IsModuleMember() || symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension }) continue;
                    var containingProject = solution.GetProject(symbol.ContainingAssembly, cancellationToken);
                    if (containingProject == null && convertedAssemblies.Contains(symbol.ContainingAssembly.Identity)) {
                        throw new InvalidOperationException($"Cannot determine the project for module reference '{name}' in '{document.FilePath}'.");
                    }
                    if (containingProject == null || !convertedIds.Contains(containingProject.Id)) continue;

                    if (symbol.ContainingType.GetMembers().Any(member => member.Name == symbol.ContainingType.Name) ||
                        options.RootNamespaceOverride != null && containingProject.CompilationOptions is VisualBasicCompilationOptions vbOptions &&
                        options.RootNamespaceOverride != vbOptions.RootNamespace) {
                        throw new InvalidOperationException($"Cannot safely qualify module reference '{name}' in '{document.FilePath}' because its declaration may be renamed during conversion.");
                    }

                    SyntaxNode nodeToReplace = name;
                    if (name.Parent is VBSyntax.MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name) {
                        if (memberAccess.Expression == null || semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol is not INamespaceSymbol) continue;
                        nodeToReplace = memberAccess;
                    }
                    if (name.Parent is VBSyntax.QualifiedNameSyntax qualifiedAccess && qualifiedAccess.Right == name) {
                        if (semanticModel.GetSymbolInfo(qualifiedAccess.Left, cancellationToken).Symbol is not INamespaceSymbol) continue;
                        nodeToReplace = qualifiedAccess;
                    }

                    if (name.Ancestors().Any(node => node is VBSyntax.ImportsStatementSyntax or VBSyntax.InferredFieldInitializerSyntax or VBSyntax.HandlesClauseSyntax) ||
                        nodeToReplace != name && nodeToReplace.DescendantTrivia().Any(trivia => trivia.SpanStart >= nodeToReplace.SpanStart && trivia.Span.End <= nodeToReplace.Span.End &&
                            !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))) {
                        throw new InvalidOperationException($"Cannot safely qualify module reference '{name}' in '{document.FilePath}'.");
                    }

                    var moduleName = Microsoft.CodeAnalysis.VisualBasic.SymbolDisplay.ToDisplayString(symbol.ContainingType,
                        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));
                    SyntaxNode qualifiedName = symbol is INamedTypeSymbol
                        ? SyntaxFactory.QualifiedName(SyntaxFactory.ParseName(moduleName), name.WithoutTrivia())
                        : SyntaxFactory.SimpleMemberAccessExpression(SyntaxFactory.ParseExpression(moduleName), name.WithoutTrivia());
                    var annotation = new SyntaxAnnotation();
                    replacements[nodeToReplace] = (qualifiedName.WithTriviaFrom(nodeToReplace).WithAdditionalAnnotations(annotation), annotation, symbol);
                }

                if (replacements.Keys.Any(node => node.Ancestors().Any(replacements.ContainsKey))) {
                    throw new InvalidOperationException($"Cannot safely qualify overlapping module references in '{document.FilePath}'.");
                }
                var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original].Replacement);
                if (replacements.Count > 0) {
                    var newDocument = document.WithSyntaxRoot(newRoot);
                    var newSemanticModel = await newDocument.GetSemanticModelAsync(cancellationToken);
                    var attachedRoot = await newDocument.GetSyntaxRootAsync(cancellationToken);
                    foreach (var replacement in replacements.Values) {
                        var rewrittenNode = attachedRoot.GetAnnotatedNodes(replacement.Annotation).Single();
                        var rewrittenSymbol = newSemanticModel?.GetSymbolInfo(rewrittenNode, cancellationToken).Symbol;
                        if (!SymbolEqualityComparer.Default.Equals(replacement.Symbol, rewrittenSymbol)) {
                            throw new InvalidOperationException($"Qualification changes the binding of module reference '{rewrittenNode}' in '{document.FilePath}'.");
                        }
                    }
                }
                var path = Path.GetFullPath(document.FilePath);
                var sourceText = await document.GetTextAsync(cancellationToken);
                var originalText = sourceText.ToString();
                var updatedText = newRoot.ToFullString();
                if (documentsByPath.TryGetValue(path, out var previous) && (previous.Original != originalText || previous.Updated != updatedText || !Equals(previous.Encoding, sourceText.Encoding))) {
                    throw new InvalidOperationException($"Linked VB document '{path}' requires conflicting changes.");
                }
                documentsByPath[path] = (originalText, updatedText, sourceText.Encoding);
            }
        }

        return documentsByPath.Where(entry => entry.Value.Original != entry.Value.Updated).Select(entry => {
            if (convertedPaths.Contains(entry.Key)) throw new InvalidOperationException($"VB document '{entry.Key}' is also part of a converted project.");
            return new ConversionResult(entry.Value.Updated) { SourcePathOrNull = entry.Key, TargetPathOrNull = entry.Key, OutputEncoding = entry.Value.Encoding };
        }).ToArray();
    }
}