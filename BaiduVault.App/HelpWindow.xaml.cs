using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Text;
using System.Text.RegularExpressions;

namespace BaiduVault.App;

/// <summary>内置使用说明查看窗口：把 markdown 渲染成可滚动的深色文档。</summary>
public partial class HelpWindow : Window
{
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush HeadBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
    private static readonly Brush QuietBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB0));
    private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C));
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x3A));
    private static readonly FontFamily Mono = new("Consolas, Microsoft YaHei UI");
    private static readonly Regex InlineRe = new(@"\*\*(.+?)\*\*|`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex BulletRe = new(@"^(\s*)[-*]\s+", RegexOptions.Compiled);
    private static readonly Regex NumberRe = new(@"^(\s*)(\d+\.)\s+", RegexOptions.Compiled);

    public HelpWindow()
    {
        InitializeComponent();
        RenderMarkdown(LoadDocument());
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static string LoadDocument()
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream("使用说明.md");
        if (stream == null) return "# 使用说明\n\n内置说明文档缺失，请查看程序目录下的使用说明。";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void RenderMarkdown(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```"))
            {
                i++;
                var sb = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    sb.AppendLine(lines[i++]);
                i++;
                DocPanel.Children.Add(CodeBlock(sb.ToString().TrimEnd()));
                continue;
            }

            if (line.TrimStart().StartsWith("|"))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                    rows.Add(lines[i++]);
                DocPanel.Children.Add(TableBlock(rows));
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0) { i++; continue; }

            if (trimmed.StartsWith("#### "))
            {
                DocPanel.Children.Add(Heading(trimmed[5..], 13.5, FontWeights.Bold, new Thickness(0, 14, 0, 6)));
            }
            else if (trimmed.StartsWith("### "))
            {
                DocPanel.Children.Add(Heading(trimmed[4..], 14.5, FontWeights.SemiBold, new Thickness(0, 16, 0, 7)));
            }
            else if (trimmed.StartsWith("## "))
            {
                DocPanel.Children.Add(Heading(trimmed[3..], 17, FontWeights.SemiBold, new Thickness(0, 20, 0, 8)));
            }
            else if (trimmed.StartsWith("# "))
            {
                DocPanel.Children.Add(Heading(trimmed[2..], 22, FontWeights.Bold, new Thickness(0, 2, 0, 10)));
            }
            else if (trimmed.StartsWith("> "))
            {
                var tb = NewText(trimmed[2..], 12.5, FontWeights.Normal, QuietBrush, new Thickness(0, 0, 0, 4));
                tb.FontStyle = FontStyles.Italic;
                DocPanel.Children.Add(tb);
            }
            else if (BulletRe.IsMatch(line))
            {
                var m = BulletRe.Match(line);
                var tb = Paragraph(m.Groups[1].Length * 7 + 20, 4);
                tb.Inlines.Add(new Run("•  ") { Foreground = HeadBrush });
                AddInlines(tb, line[(m.Index + m.Length)..]);
                DocPanel.Children.Add(tb);
            }
            else if (NumberRe.IsMatch(line))
            {
                var m = NumberRe.Match(line);
                var tb = Paragraph(m.Groups[1].Length * 7 + 20, 4);
                tb.Inlines.Add(new Run(m.Groups[2].Value + "  ") { Foreground = HeadBrush });
                AddInlines(tb, line[(m.Index + m.Length)..]);
                DocPanel.Children.Add(tb);
            }
            else
            {
                DocPanel.Children.Add(NewText(trimmed, 13, FontWeights.Normal, TextBrush, new Thickness(0, 0, 0, 5)));
            }
            i++;
        }
    }

    private static TextBlock NewText(string text, double size, FontWeight weight, Brush fg, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontSize = size, FontWeight = weight, Foreground = fg, Margin = margin,
            TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        AddInlines(tb, text);
        return tb;
    }

    private static TextBlock Paragraph(double indent, double bottomMargin)
    {
        return new TextBlock
        {
            Margin = new Thickness(indent, 0, 0, bottomMargin),
            TextWrapping = TextWrapping.Wrap, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = TextBrush, FontSize = 13,
        };
    }

    private static void AddInlines(TextBlock tb, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRe.Matches(text))
        {
            if (m.Index > pos) tb.Inlines.Add(new Run(text[pos..m.Index]));
            if (m.Groups[1].Success)
            {
                tb.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold, Foreground = HeadBrush });
            }
            else
            {
                tb.Inlines.Add(new Span(new Run(m.Groups[2].Value))
                {
                    FontFamily = Mono, Background = CodeBrush,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xD8, 0xA8)),
                });
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }

    private static TextBlock Heading(string text, double size, FontWeight weight, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontSize = size, FontWeight = weight, Foreground = HeadBrush, Margin = margin,
            TextWrapping = TextWrapping.Wrap,
        };
        AddInlines(tb, text);
        foreach (var inline in tb.Inlines) if (inline.Foreground == null) inline.Foreground = HeadBrush;
        return tb;
    }

    private static Border CodeBlock(string code)
    {
        var tb = new TextBlock
        {
            Text = code, FontFamily = Mono, FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xE4, 0xD6)),
            TextWrapping = TextWrapping.Wrap, LineHeight = 19,
        };
        return new Border
        {
            Background = CodeBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 4, 0, 10), Child = tb,
        };
    }

    private static Grid TableBlock(List<string> rows)
    {
        var parsed = new List<string[]>();
        foreach (var row in rows)
        {
            var cells = row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"))) continue;
            parsed.Add(cells);
        }
        var cols = parsed.Count == 0 ? 1 : parsed.Max(r => r.Length);

        var grid = new Grid { Margin = new Thickness(0, 5, 0, 12) };
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int r = 0; r < parsed.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cols; c++)
            {
                var text = c < parsed[r].Length ? parsed[r][c] : "";
                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19,
                    Foreground = r == 0 ? HeadBrush : TextBrush,
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                AddInlines(tb, text);
                var cell = new Border
                {
                    BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = r == 0 ? new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x36)) : Brushes.Transparent,
                    Padding = new Thickness(9, 6, 9, 6), Child = tb,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
