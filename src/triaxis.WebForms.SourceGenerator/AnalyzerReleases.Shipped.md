; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TWF002  | Triaxis.WebForms | Warning | Property type bound via Parse(string) / ctor(string) fallback — consider attaching a [TypeConverter] that supplies an InstanceDescriptor.
