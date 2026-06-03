// netstandard2.0 lacks IsExternalInit, which the C# compiler requires for
// init-only setters and record struct positional parameters. Define it
// internally so LangVersion=latest features compile against the older TFM.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
