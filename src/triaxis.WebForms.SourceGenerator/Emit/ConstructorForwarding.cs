using System.Collections.Generic;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// How the generated type forwards constructor injection to its codebehind
    /// base. The ASP.NET compiler (4.7.2+) generates, for each public parameterized
    /// base constructor, a matching constructor that forwards its arguments to
    /// <c>base(...)</c> and runs the page's own initialization from a shared
    /// <c>__Init()</c> method — see <c>TemplateControlCodeDomTreeGenerator
    /// .AddConstructorToSource</c> / <c>BaseCodeDomTreeGenerator.SetInitMethod</c>
    /// in the reference source. The parameterless constructor is kept only when the
    /// base also exposes a public parameterless one; otherwise the generated class
    /// must not have one either (the base can't be default-constructed).
    /// </summary>
    internal sealed class ConstructorForwarding
    {
        public ConstructorForwarding(IReadOnlyList<ForwardedConstructor> constructors, bool baseHasParameterlessConstructor)
        {
            Constructors = constructors;
            BaseHasParameterlessConstructor = baseHasParameterlessConstructor;
        }

        public IReadOnlyList<ForwardedConstructor> Constructors { get; }

        public bool BaseHasParameterlessConstructor { get; }
    }

    /// <summary>One generated forwarding constructor: the fully-rendered parameter
    /// list (including attributes and defaults) and the argument names passed
    /// through to <c>base(...)</c>.</summary>
    internal sealed class ForwardedConstructor
    {
        public ForwardedConstructor(string parameterList, string baseArguments)
        {
            ParameterList = parameterList;
            BaseArguments = baseArguments;
        }

        public string ParameterList { get; }

        public string BaseArguments { get; }
    }
}
