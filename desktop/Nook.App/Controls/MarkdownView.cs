using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nook.App.Controls;

/// <summary>
/// Small markdown renderer like the website's: ``` code blocks (with Copy), `inline code`, **bold**,
/// *italic*, # headings and lists. Model text is only ever shown as text (never parsed as markup).
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    static readonly Regex InlineRx = new(@"(`[^`\n]+`|\*\*[^*\n]+\*\*|(?<![\w*])\*[^*\n]+\*(?![\w*]))", RegexOptions.Compiled);
    bool _queued;

    public MarkdownView() => Spacing = 8;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty || _queued) return;
        // Coalesce streaming updates: re-render at most once per dispatcher pass.
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Render(); }, DispatcherPriority.Background);
    }

    void Render()
    {
        Children.Clear();
        var text = (Text ?? "").Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var para = new List<string>();
        void Flush()
        {
            if (para.Count > 0) Children.Add(Paragraph(para));
            para.Clear();
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                Flush();
                var lang = line.Trim()[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
                Children.Add(CodeBlock(lang, string.Join("\n", code)));
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (line.StartsWith('#'))
            {
                Flush();
                var h = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, FontSize = 16 };
                AddInlines(h, line.TrimStart('#').Trim());
                Children.Add(h);
                continue;
            }
            para.Add(line);
        }
        Flush();
    }

    static Control Paragraph(List<string> lines)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var m = Regex.Match(l, @"^(\s*)[-*+] (.*)$");
            if (m.Success) l = m.Groups[1].Value + "  •  " + m.Groups[2].Value;
            if (i > 0) tb.Inlines!.Add(new LineBreak());
            AddInlines(tb, l);
        }
        return tb;
    }

    static void AddInlines(SelectableTextBlock tb, string text)
    {
        tb.Inlines ??= new InlineCollection();
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) tb.Inlines.Add(new Run(text[pos..m.Index]));
            var v = m.Value;
            if (v.StartsWith('`'))
            {
                var run = new Run(v[1..^1]) { FontFamily = Mono };
                run.Bind(TextElement.BackgroundProperty, run.GetResourceObservable("Surface2Brush"));
                tb.Inlines.Add(run);
            }
            else if (v.StartsWith("**")) tb.Inlines.Add(new Run(v[2..^2]) { FontWeight = FontWeight.SemiBold });
            else tb.Inlines.Add(new Run(v[1..^1]) { FontStyle = FontStyle.Italic });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }

    static FontFamily Mono => Application.Current?.TryFindResource("MonoFont", out var f) == true && f is FontFamily ff
        ? ff : FontFamily.Default;

    static Control CodeBlock(string lang, string code)
    {
        var copy = new Button { Content = "Copy", Classes = { "ghost", "small" } };
        copy.Click += async (_, _) =>
        {
            var clip = TopLevel.GetTopLevel(copy)?.Clipboard;
            if (clip != null) await clip.SetTextAsync(code);
            copy.Content = "Copied";
        };
        var head = new DockPanel { Margin = new Thickness(12, 2, 4, 2) };
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(copy);
        head.Children.Add(new TextBlock
        {
            Text = lang.Length > 0 ? lang : "code", FontFamily = Mono, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" },
        });
        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = code, FontFamily = Mono, FontSize = 13, Margin = new Thickness(14, 10) },
        };
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder.Bind(Border.BorderBrushProperty, headBorder.GetResourceObservable("LineBrush"));
        var panel = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top);
        panel.Children.Add(headBorder);
        panel.Children.Add(body);
        var border = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = panel, ClipToBounds = true };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("CodeBgBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("LineBrush"));
        return border;
    }
}
