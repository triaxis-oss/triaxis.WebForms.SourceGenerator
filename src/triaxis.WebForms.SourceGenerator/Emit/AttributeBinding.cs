namespace triaxis.WebForms.SourceGenerator.Emit
{
    internal enum AttributeBindingKind
    {
        /// Assign a strongly-typed property: <c>__ctrl.Name = valueExpr;</c>
        Property,

        /// Set via <c>IAttributeAccessor.SetAttribute</c> (only when the control
        /// type implements it — e.g. HtmlControls / WebControls).
        Attribute,

        /// Wire an event handler: <c>__ctrl.Event += this.Handler;</c>
        Event,

        /// Data-binding: <c>control.DataBinding += handler;</c> + a handler that
        /// sets the property from the <c>&lt;%# … %&gt;</c> expression.
        DataBinding,

        /// Nothing emitted (unsupported property type, missing handler, etc.).
        Skip,
    }

    internal readonly struct AttributeBinding
    {
        private AttributeBinding(AttributeBindingKind kind, string? propertyName, string? valueExpression, string? skipReason, string? bindField = null, string? attributeName = null)
        {
            Kind = kind;
            PropertyName = propertyName;
            ValueExpression = valueExpression;
            SkipReason = skipReason;
            BindField = bindField;
            AttributeName = attributeName;
        }

        public AttributeBindingKind Kind { get; }
        public string? PropertyName { get; }
        public string? ValueExpression { get; }
        public string? SkipReason { get; }

        /// <summary>For a two-way <c>&lt;%# Bind("Field") %&gt;</c> DataBinding: the
        /// data field name, used to build the template's ExtractValues method.
        /// Null for a one-way <c>Eval</c>.</summary>
        public string? BindField { get; }

        /// <summary>For a DataBinding that targets a plain markup attribute rather
        /// than a typed property (<c>&lt;label for="&lt;%# … %&gt;"&gt;</c>): the
        /// attribute name, set via <c>IAttributeAccessor.SetAttribute</c> in the
        /// handler. Null for a property DataBinding.</summary>
        public string? AttributeName { get; }

        public static AttributeBinding Property(string name, string valueExpression)
        {
            return new AttributeBinding(AttributeBindingKind.Property, name, valueExpression, null);
        }

        public static AttributeBinding Attribute()
        {
            return new AttributeBinding(AttributeBindingKind.Attribute, null, null, null);
        }

        public static AttributeBinding Event(string eventName, string handlerMethod)
        {
            return new AttributeBinding(AttributeBindingKind.Event, eventName, handlerMethod, null);
        }

        public static AttributeBinding DataBinding(string propertyName, string valueExpression, string? bindField = null)
        {
            return new AttributeBinding(AttributeBindingKind.DataBinding, propertyName, valueExpression, null, bindField);
        }

        /// <summary>A DataBinding that writes a markup attribute via
        /// <c>IAttributeAccessor.SetAttribute</c> (the bound member is not a typed
        /// property).</summary>
        public static AttributeBinding AttributeDataBinding(string attributeName, string valueExpression)
        {
            return new AttributeBinding(AttributeBindingKind.DataBinding, null, valueExpression, null, attributeName: attributeName);
        }

        public static AttributeBinding Skip(string reason)
        {
            return new AttributeBinding(AttributeBindingKind.Skip, null, null, reason);
        }
    }

    /// <summary>
    /// Decides how a markup attribute binds to a resolved control type: a typed
    /// property assignment, an <c>IAttributeAccessor</c> call, or skip. Backed by
    /// the compilation in the generator; trivial stand-ins are used in tests.
    /// </summary>
    /// <param name="controlTypeMetadataName">e.g. <c>Telerik.Web.UI.RadScriptManager</c>.</param>
    /// <param name="containerTypeMetadataName">The data-item container type in scope
    /// (from the enclosing template's <c>[TemplateContainer]</c>), or null at page
    /// scope — governs whether <c>Container</c>-relative data-binding can be emitted.</param>
    internal delegate AttributeBinding AttributeBinder(string controlTypeMetadataName, string attributeName, string attributeValue, string? containerTypeMetadataName);
}
