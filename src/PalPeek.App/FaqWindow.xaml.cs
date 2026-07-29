using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Media = System.Windows.Media;

namespace PalPeek;

public partial class FaqWindow : System.Windows.Controls.UserControl
{
    public FaqWindow()
    {
        InitializeComponent();
        DocumentView.Document = LoadDocument();
    }

    private static FlowDocument LoadDocument()
    {
        var document = new FlowDocument
        {
            FontFamily = new Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(244, 246, 250)),
            Background = Media.Brushes.Transparent,
            PagePadding = new Thickness(0),
            LineHeight = 24
        };
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "faq.md");
        if (!File.Exists(path))
        {
            document.Blocks.Add(new Paragraph(new Run("常见问题文档不存在。")));
            return document;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                document.Blocks.Add(new Paragraph(new Run(line[2..]))
                {
                    FontSize = 21,
                    FontWeight = FontWeights.Bold,
                    Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(53, 230, 196)),
                    Margin = new Thickness(0, 0, 0, 14)
                });
            }
            else if (line.StartsWith("Q. ", StringComparison.OrdinalIgnoreCase))
            {
                document.Blocks.Add(new Paragraph(new Run(line))
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 16,
                    Margin = new Thickness(0, 16, 0, 5)
                });
            }
            else
            {
                document.Blocks.Add(new Paragraph(new Run(line))
                {
                    Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(190, 198, 211)),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
        }
        return document;
    }

}
