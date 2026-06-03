namespace triaxis.WebForms.SourceGenerator.Emit
{
    internal enum ChildPlacementKind
    {
        /// <c>((IParserAccessor)parent).AddParsedSubObject(child)</c> — control
        /// containers (Page, form, Panel, …).
        ParsedSubObject,

        /// <c>parent.&lt;Member&gt;.Add(child)</c> — the child is an item of the
        /// parent's default collection (e.g. a TextBoxSetting in InputSettings).
        CollectionItem,

        /// The child is a property element wrapping a collection
        /// (<c>&lt;Scripts&gt;</c>); add *its* children to <c>parent.&lt;Member&gt;</c>.
        CollectionElement,

        /// The child is a property element wrapping a complex object
        /// (<c>&lt;CompositeScript&gt;</c>); populate <c>parent.&lt;Member&gt;</c>'s
        /// children, using <see cref="ChildPlacement.MemberTypeMetadata"/>.
        ObjectElement,

        /// The child is an <c>ITemplate</c>-typed property element
        /// (<c>&lt;ItemTemplate&gt;</c>); compile its content into a
        /// <c>CompiledTemplateBuilder</c> assigned to <c>parent.&lt;Member&gt;</c>.
        Template,

        /// Unsupported (scalar property element, unknown child, …) — nothing emitted.
        Skip,
    }

    internal readonly struct ChildPlacement
    {
        private ChildPlacement(ChildPlacementKind kind, string? member, string? memberTypeMetadata, string? reason, bool singleInstance = false, bool twoWay = false)
        {
            Kind = kind;
            Member = member;
            MemberTypeMetadata = memberTypeMetadata;
            Reason = reason;
            SingleInstance = singleInstance;
            TwoWay = twoWay;
        }

        public ChildPlacementKind Kind { get; }
        public string? Member { get; }
        public string? MemberTypeMetadata { get; }
        public string? Reason { get; }

        /// <summary>For <see cref="ChildPlacementKind.Template"/>: the template is
        /// <c>[TemplateInstance(Single)]</c> (e.g. UpdatePanel.ContentTemplate),
        /// so controls inside bind to page fields rather than per-item locals.</summary>
        public bool SingleInstance { get; }

        /// <summary>For <see cref="ChildPlacementKind.Template"/>: the template
        /// property is <c>[TemplateContainer(..., BindingDirection.TwoWay)]</c>, so
        /// it takes an <c>IBindableTemplate</c> (CompiledBindableTemplateBuilder).</summary>
        public bool TwoWay { get; }

        public static ChildPlacement ParsedSubObject() => new(ChildPlacementKind.ParsedSubObject, null, null, null);
        public static ChildPlacement CollectionItem(string member) => new(ChildPlacementKind.CollectionItem, member, null, null);
        public static ChildPlacement CollectionElement(string member) => new(ChildPlacementKind.CollectionElement, member, null, null);
        public static ChildPlacement ObjectElement(string member, string memberTypeMetadata) => new(ChildPlacementKind.ObjectElement, member, memberTypeMetadata, null);
        public static ChildPlacement Template(string member, string? containerTypeMetadata, bool singleInstance, bool twoWay) => new(ChildPlacementKind.Template, member, containerTypeMetadata, null, singleInstance, twoWay);
        public static ChildPlacement Skip(string reason) => new(ChildPlacementKind.Skip, null, null, reason);
    }

    /// <summary>Classifies how a child element binds under a parent control type.
    /// Backed by the compilation in the generator.</summary>
    internal delegate ChildPlacement ChildClassifier(string parentTypeMetadataName, string childLocalName, bool childIsServerControl);

    /// <summary>True when the parent accepts literal text + parsed controls
    /// (a <c>[ParseChildren(false)]</c> control implementing IParserAccessor).</summary>
    internal delegate bool ControlContainerPredicate(string parentTypeMetadataName);
}
