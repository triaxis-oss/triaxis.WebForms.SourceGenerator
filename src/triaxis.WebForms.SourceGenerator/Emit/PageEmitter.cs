using System.Collections.Generic;
using triaxis.WebForms.SourceGenerator.Model;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>Combines the page frame with the folded control tree into the
    /// complete generated source for one markup file.</summary>
    internal static class PageEmitter
    {
        public static string Emit(
            MarkupDirective directive,
            string virtualPath,
            ServerControlNode root,
            ControlTypeResolver resolver,
            string fileText,
            AttributeBinder binder,
            ChildClassifier classify,
            ControlContainerPredicate isControlContainer,
            System.Func<string, bool> isThemeable,
            System.Func<string, bool> isControl,
            System.Func<string, string> userControlBindingType,
            IReadOnlyDictionary<string, string> pageDefaults,
            out IReadOnlyList<string> diagnostics,
            System.Func<string, string, string?>? codeBehindField = null,
            System.Collections.Generic.IEnumerable<string>? usings = null,
            System.Func<string, bool>? codeBehindHasMember = null)
        {
            string pageType = string.IsNullOrWhiteSpace(directive.Inherits)
                ? directive.Kind switch
                {
                    MarkupKind.Control => "System.Web.UI.UserControl",
                    MarkupKind.Master => "System.Web.UI.MasterPage",
                    _ => "System.Web.UI.Page",
                }
                : directive.Inherits!;

            // ApplyStyleSheetSkin's argument: a Page is its own Page, but a
            // UserControl/MasterPage must pass its containing Page instead.
            string themeTarget = directive.Kind == MarkupKind.Page
                ? "(global::System.Web.UI.Page)(object)this"
                : "this.Page";

            var tree = new ControlTreeEmitter(resolver, fileText, binder, pageType, classify, isControlContainer,
                isThemeable, isControl, userControlBindingType, themeTarget, pageDefaults, codeBehindField, codeBehindHasMember);
            ControlTreeEmitter.Result result = tree.Emit(root, directive);
            diagnostics = result.Diagnostics;

            // ValidateRequestMode follows the effective ValidateRequest (page
            // directive over web.config <pages>, default true): false → Disabled.
            string? validateRequest =
                directive.Attributes.TryGetValue("ValidateRequest", out string? d) ? d
                : pageDefaults.TryGetValue("validateRequest", out string? c) ? c
                : null;
            bool requestValidationEnabled = !string.Equals(validateRequest, "false", System.StringComparison.OrdinalIgnoreCase);

            return PageFrameEmitter.Emit(directive, virtualPath, result.TreeBody, result.Methods, usings, result.ContentPlaceHolderIds, requestValidationEnabled);
        }
    }
}
