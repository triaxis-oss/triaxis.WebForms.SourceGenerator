// Compat shims for the vendored Mono WebForms front-end (NOT Mono source).
//
// The front-end references two members that live elsewhere in Mono's
// System.Web assembly and aren't available to a netstandard2.0 analyzer:
//   - System.Web.Util.Helpers.InvariantCulture
//   - System.Web.Compilation.HttpException (thrown on malformed markup)
// AspParser catches plain Exception around the tag scan, so a minimal
// Exception subclass preserves the error-recovery behavior. Defining these
// here keeps the vendored files byte-faithful to upstream for easy re-sync.

using System;
using System.Globalization;

namespace System.Web.Util
{
    internal static class Helpers
    {
        public static CultureInfo InvariantCulture => CultureInfo.InvariantCulture;
    }
}

namespace System.Web.Compilation
{
    internal sealed class HttpException : Exception
    {
        public HttpException(string message)
            : base(message)
        {
        }
    }
}
