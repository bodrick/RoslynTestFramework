using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using FluentAssertions;
using JetBrains.Annotations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

// ReSharper disable once CheckNamespace
namespace RoslynTestFramework
{
    public abstract class AnalysisTestFixture
    {
        [NotNull]
        private static readonly DocumentFactory DocumentFactory = new DocumentFactory();

        [NotNull]
        protected abstract string DiagnosticId { get; }

        [NotNull]
        protected abstract DiagnosticAnalyzer CreateAnalyzer();

        [NotNull]
        protected virtual CodeFixProvider CreateFixProvider()
        {
            throw new NotImplementedException();
        }

        protected void AssertDiagnostics([NotNull] AnalyzerTestContext context, [NotNull] [ItemNotNull] params string[] messages)
        {
            FrameworkGuard.NotNull(context, nameof(context));
            FrameworkGuard.NotNull(messages, nameof(messages));

            RunDiagnostics(context, messages);
        }

        [NotNull]
        private AnalysisResult RunDiagnostics([NotNull] AnalyzerTestContext context,
            [NotNull] [ItemNotNull] params string[] messages)
        {
            AnalysisResult result = GetAnalysisResult(context, messages);

            VerifyDiagnosticCount(result, context.DiagnosticsCaptureMode);
            VerifyDiagnostics(result, context);

            return result;
        }

        [NotNull]
        private AnalysisResult GetAnalysisResult([NotNull] AnalyzerTestContext context, [NotNull] [ItemNotNull] string[] messages)
        {
            Document document = DocumentFactory.ToDocument(context.SourceCode, context);

            IList<Diagnostic> diagnostics = GetSortedAnalyzerDiagnostics(document, context);
            return new AnalysisResult(diagnostics, context.SourceSpans, messages);
        }

        [NotNull]
        [ItemNotNull]
        private IList<Diagnostic> GetSortedAnalyzerDiagnostics([NotNull] Document document, [NotNull] AnalyzerTestContext context)
        {
            IEnumerable<Diagnostic> diagnostics = EnumerateDiagnosticsForDocument(document,
                context.ValidationMode, context.DiagnosticsCaptureMode, context.Options).Where(d => d.Id == DiagnosticId);

            if (context.DiagnosticsCaptureMode == DiagnosticsCaptureMode.RequireInSourceTree)
            {
                diagnostics = diagnostics.OrderBy(d => d.Location.SourceSpan);
            }

            return diagnostics.ToImmutableArray();
        }

        [NotNull]
        [ItemNotNull]
        private IEnumerable<Diagnostic> EnumerateDiagnosticsForDocument([NotNull] Document document,
            TestValidationMode validationMode, DiagnosticsCaptureMode diagnosticsCaptureMode, [NotNull] AnalyzerOptions options)
        {
            CompilationWithAnalyzers compilationWithAnalyzers = GetCompilationWithAnalyzers(document, validationMode, options);

            SyntaxTree tree = document.GetSyntaxTreeAsync().Result;

            return EnumerateAnalyzerDiagnostics(compilationWithAnalyzers, tree, diagnosticsCaptureMode);
        }

        [NotNull]
        private CompilationWithAnalyzers GetCompilationWithAnalyzers([NotNull] Document document,
            TestValidationMode validationMode, [NotNull] AnalyzerOptions options)
        {
            ImmutableArray<DiagnosticAnalyzer> analyzers = ImmutableArray.Create(CreateAnalyzer());
            Compilation compilation = document.Project.GetCompilationAsync().Result;

            ImmutableArray<Diagnostic> compilerDiagnostics = compilation.GetDiagnostics(CancellationToken.None);
            if (validationMode != TestValidationMode.AllowCompileErrors)
            {
                ValidateCompileErrors(compilerDiagnostics);
            }

            return compilation.WithAnalyzers(analyzers, options);
        }

        private void ValidateCompileErrors([ItemNotNull] ImmutableArray<Diagnostic> compilerDiagnostics)
        {
            Diagnostic[] compilerErrors = compilerDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            compilerErrors.Should().BeEmpty("test should not have compile errors");
        }

        [NotNull]
        [ItemNotNull]
        private static IEnumerable<Diagnostic> EnumerateAnalyzerDiagnostics(
            [NotNull] CompilationWithAnalyzers compilationWithAnalyzers, [NotNull] SyntaxTree tree,
            DiagnosticsCaptureMode diagnosticsCaptureMode)
        {
            foreach (Diagnostic analyzerDiagnostic in compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().Result)
            {
                Location location = analyzerDiagnostic.Location;

                if (diagnosticsCaptureMode == DiagnosticsCaptureMode.AllowOutsideSourceTree ||
                    LocationIsInSourceTree(location, tree))
                {
                    yield return analyzerDiagnostic;
                }
            }
        }

        private static bool LocationIsInSourceTree([NotNull] Location location, [CanBeNull] SyntaxTree tree)
        {
            return location.IsInSource && location.SourceTree == tree;
        }

        private static void VerifyDiagnosticCount([NotNull] AnalysisResult result, DiagnosticsCaptureMode captureMode)
        {
            if (captureMode == DiagnosticsCaptureMode.RequireInSourceTree)
            {
                result.Diagnostics.Should().HaveSameCount(result.Spans);
            }

            result.Diagnostics.Should().HaveSameCount(result.Messages);
        }

        private static void VerifyDiagnostics([NotNull] AnalysisResult result, [NotNull] AnalyzerTestContext context)
        {
            for (int index = 0; index < result.Diagnostics.Count; index++)
            {
                Diagnostic diagnostic = result.Diagnostics[index];

                if (context.DiagnosticsCaptureMode == DiagnosticsCaptureMode.RequireInSourceTree)
                {
                    VerifyDiagnosticLocation(diagnostic, result.Spans[index]);
                }

                diagnostic.GetMessage().Should().Be(result.Messages[index]);
            }
        }

        private static void VerifyDiagnosticLocation([NotNull] Diagnostic diagnostic, TextSpan span)
        {
            diagnostic.Location.IsInSource.Should().BeTrue();
            diagnostic.Location.SourceSpan.Should().Be(span);
        }

        protected void AssertDiagnosticsWithCodeFixes([NotNull] FixProviderTestContext context,
            [NotNull] [ItemNotNull] params string[] messages)
        {
            FrameworkGuard.NotNull(context, nameof(context));
            FrameworkGuard.NotNull(messages, nameof(messages));

            AnalysisResult analysisResult = RunDiagnostics(context.AnalyzerTestContext, messages);

            if (context.IgnoreWhitespaceDifferences)
            {
                ICollection<string> expectedCode = context.ExpectedCode
                    .Select(e => DocumentFactory.FormatSourceCode(e, context.AnalyzerTestContext)).ToArray();
                context = context.WithExpectedCode(expectedCode);
            }

            CodeFixProvider fixProvider = CreateFixProvider();

            Diagnostic firstDiagnostic = analysisResult.Diagnostics.FirstOrDefault();
            if (firstDiagnostic != null)
            {
                RunCodeFixes(context, firstDiagnostic, fixProvider);
            }
        }

        private void RunCodeFixes([NotNull] FixProviderTestContext context, [NotNull] Diagnostic diagnostic,
            [NotNull] CodeFixProvider fixProvider)
        {
            for (int index = 0; index < context.ExpectedCode.Count; index++)
            {
                Document document =
                    DocumentFactory.ToDocument(context.AnalyzerTestContext.SourceCode, context.AnalyzerTestContext);

                ImmutableArray<CodeAction> codeFixes = GetCodeFixesForDiagnostic(diagnostic, document, fixProvider);
                codeFixes.Should().HaveSameCount(context.ExpectedCode);

                VerifyCodeAction(codeFixes[index], document, context.ExpectedCode[index], context.IgnoreWhitespaceDifferences);
            }
        }

        [ItemNotNull]
        private ImmutableArray<CodeAction> GetCodeFixesForDiagnostic([NotNull] Diagnostic diagnostic, [NotNull] Document document,
            [NotNull] CodeFixProvider fixProvider)
        {
            ImmutableArray<CodeAction>.Builder builder = ImmutableArray.CreateBuilder<CodeAction>();

            var fixContext = new CodeFixContext(document, diagnostic, RegisterCodeFix, CancellationToken.None);
            fixProvider.RegisterCodeFixesAsync(fixContext).Wait();

            return builder.ToImmutable();

            void RegisterCodeFix(CodeAction codeAction, ImmutableArray<Diagnostic> _)
            {
                builder.Add(codeAction);
            }
        }

        private static void VerifyCodeAction([NotNull] CodeAction codeAction, [NotNull] Document document,
            [NotNull] string expectedCode, bool formatOutputDocument)
        {
            FrameworkGuard.NotNull(codeAction, nameof(codeAction));
            FrameworkGuard.NotNull(expectedCode, nameof(expectedCode));

            ImmutableArray<CodeActionOperation> operations = codeAction.GetOperationsAsync(CancellationToken.None).Result;

            operations.Should().HaveCount(1);

            VerifyOperationText(document, operations.Single(), expectedCode, formatOutputDocument);
        }

        private static void VerifyOperationText([NotNull] Document sourceDocument, [NotNull] CodeActionOperation operation,
            [NotNull] string expectedCode, bool formatOutputDocument)
        {
            Workspace workspace = sourceDocument.Project.Solution.Workspace;
            operation.Apply(workspace, CancellationToken.None);

            Document newDocument = workspace.CurrentSolution.GetDocument(sourceDocument.Id);

            string actualCode = formatOutputDocument
                ? DocumentFactory.FormatDocument(newDocument)
                : newDocument.GetTextAsync().Result.ToString();

            actualCode.Should().Be(expectedCode);
        }

        private sealed class AnalysisResult
        {
            [NotNull]
            [ItemNotNull]
            public IList<Diagnostic> Diagnostics { get; }

            [NotNull]
            public IList<TextSpan> Spans { get; }

            [NotNull]
            [ItemNotNull]
            public IList<string> Messages { get; }

            public AnalysisResult([NotNull] [ItemNotNull] IList<Diagnostic> diagnostics, [NotNull] IList<TextSpan> spans,
                [NotNull] [ItemNotNull] IList<string> messages)
            {
                FrameworkGuard.NotNull(diagnostics, nameof(diagnostics));
                FrameworkGuard.NotNull(spans, nameof(spans));
                FrameworkGuard.NotNull(messages, nameof(messages));

                Diagnostics = diagnostics;
                Spans = spans;
                Messages = messages;
            }
        }
    }
}
