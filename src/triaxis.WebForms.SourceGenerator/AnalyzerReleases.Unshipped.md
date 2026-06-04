; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TWF003  | Triaxis.WebForms | Error | Resolved control type isn't assignable to the same-named codebehind field — the framework would silently drop the assignment, leaving the field null at runtime.
