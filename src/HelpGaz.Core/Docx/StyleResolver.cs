using System.Xml.Linq;

namespace HelpGaz.Core.Docx;

/// <summary>
/// Вычисляет итоговое оформление по цепочке docDefaults → стиль абзаца → стиль символа → прямое форматирование.
/// Сравнивать нужно итог, а не XML: Word при сохранении может перенести свойство между уровнями, не меняя вид документа.
/// </summary>
internal sealed class StyleResolver
{
    private static readonly XNamespace W = DocxReader.W;

    private sealed record Style(string? BasedOn, XElement? RunProps, XElement? ParaProps);

    private sealed class RunState
    {
        public string? HAnsi, Ascii, Underline;
        public int? Size;
        public bool? Bold, Italic;

        public void Apply(XElement? rPr)
        {
            if (rPr is null) return;
            var fonts = rPr.Element(W + "rFonts");
            if (fonts is not null)
            {
                var h = Theme(fonts, "hAnsiTheme") ?? (string?)fonts.Attribute(W + "hAnsi");
                var a = Theme(fonts, "asciiTheme") ?? (string?)fonts.Attribute(W + "ascii");
                if (h is not null) HAnsi = h;
                if (a is not null) Ascii = a;
            }
            var sz = (string?)rPr.Element(W + "sz")?.Attribute(W + "val");
            if (int.TryParse(sz, out var size)) Size = size;
            Bold = Toggle(rPr.Element(W + "b")) ?? Bold;
            Italic = Toggle(rPr.Element(W + "i")) ?? Italic;
            var u = rPr.Element(W + "u");
            if (u is not null) Underline = (string?)u.Attribute(W + "val") ?? "single";
        }

        private static string? Theme(XElement fonts, string attr)
        {
            var v = (string?)fonts.Attribute(W + attr);
            return v is null ? null : "theme:" + v;
        }

        public CharFormat ToFormat() => new(HAnsi ?? Ascii ?? "", Size ?? 20, Bold ?? false, Italic ?? false, Underline ?? "none");
    }

    private sealed class ParaState
    {
        public string? Align, LineRule;
        public int? Left, Right, FirstLine, Before, After, Line;

        public void Apply(XElement? pPr)
        {
            if (pPr is null) return;
            var jc = (string?)pPr.Element(W + "jc")?.Attribute(W + "val");
            if (jc is not null) Align = jc switch { "start" => "left", "end" => "right", _ => jc };

            var ind = pPr.Element(W + "ind");
            if (ind is not null)
            {
                Left = Int(ind, "left") ?? Int(ind, "start") ?? Left;
                Right = Int(ind, "right") ?? Int(ind, "end") ?? Right;
                var first = Int(ind, "firstLine");
                var hanging = Int(ind, "hanging");
                if (hanging is not null) FirstLine = -hanging;
                else if (first is not null) FirstLine = first;
            }

            var sp = pPr.Element(W + "spacing");
            if (sp is not null)
            {
                Before = Int(sp, "before") ?? Before;
                After = Int(sp, "after") ?? After;
                Line = Int(sp, "line") ?? Line;
                LineRule = (string?)sp.Attribute(W + "lineRule") ?? LineRule;
            }
        }

        private static int? Int(XElement e, string attr) =>
            int.TryParse((string?)e.Attribute(W + attr), out var v) ? v : null;

        public ParaFormat ToFormat() => new(Align ?? "left", Left ?? 0, Right ?? 0, FirstLine ?? 0, Before ?? 0, After ?? 0, Line ?? 240, LineRule ?? "auto");
    }

    private readonly Dictionary<string, Style> _styles = new();
    private readonly XElement? _defaultRun;
    private readonly XElement? _defaultPara;
    private readonly string? _defaultParaStyle;
    private readonly Dictionary<CharFormat, CharFormat> _interned = new();

    public StyleResolver(XDocument? styles)
    {
        var root = styles?.Root;
        if (root is null) return;

        var defaults = root.Element(W + "docDefaults");
        _defaultRun = defaults?.Element(W + "rPrDefault")?.Element(W + "rPr");
        _defaultPara = defaults?.Element(W + "pPrDefault")?.Element(W + "pPr");

        foreach (var s in root.Elements(W + "style"))
        {
            var id = (string?)s.Attribute(W + "styleId");
            if (id is null) continue;
            _styles[id] = new Style(
                (string?)s.Element(W + "basedOn")?.Attribute(W + "val"),
                s.Element(W + "rPr"),
                s.Element(W + "pPr"));

            if ((string?)s.Attribute(W + "type") == "paragraph" && IsOn((string?)s.Attribute(W + "default")))
                _defaultParaStyle = id;
        }
    }

    private static bool IsOn(string? v) => v is "1" or "true" or "on";

    private static bool? Toggle(XElement? e)
    {
        if (e is null) return null;
        var v = (string?)e.Attribute(W + "val");
        return v is null || IsOn(v);
    }

    /// <summary>Цепочка от корневого стиля к указанному. Защищена от зацикленных basedOn.</summary>
    private List<Style> Chain(string? id)
    {
        var chain = new List<Style>();
        var seen = new HashSet<string>();
        while (id is not null && seen.Add(id) && _styles.TryGetValue(id, out var s))
        {
            chain.Insert(0, s);
            id = s.BasedOn;
        }
        return chain;
    }

    public string? ParaStyleId(XElement? pPr) =>
        (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val") ?? _defaultParaStyle;

    public ParaFormat Paragraph(string? styleId, XElement? pPr)
    {
        var st = new ParaState();
        st.Apply(_defaultPara);
        foreach (var s in Chain(styleId)) st.Apply(s.ParaProps);
        st.Apply(pPr);
        return st.ToFormat();
    }

    public CharFormat Run(string? paraStyleId, XElement? rPr)
    {
        var st = new RunState();
        st.Apply(_defaultRun);
        foreach (var s in Chain(paraStyleId)) st.Apply(s.RunProps);
        var charStyle = (string?)rPr?.Element(W + "rStyle")?.Attribute(W + "val");
        foreach (var s in Chain(charStyle)) st.Apply(s.RunProps);
        st.Apply(rPr);

        var f = st.ToFormat();
        if (_interned.TryGetValue(f, out var existing)) return existing;
        _interned[f] = f;
        return f;
    }
}
