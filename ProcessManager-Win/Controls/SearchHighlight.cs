using System.Diagnostics.CodeAnalysis;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ProcessManager.Win.Services;

namespace ProcessManager.Win.Controls;

// {Binding Query} 按属性名反射取值，裁剪发布时必须保留公共属性。
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed class SearchHighlightState : DependencyObject
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(string), typeof(SearchHighlightState), new PropertyMetadata(""));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }
}

// 为 TextBlock 提供高亮附加属性；不修改原始文本或列表数据。
public sealed class SearchHighlight : DependencyObject
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(SearchHighlight), new PropertyMetadata("", OnQueryChanged));
    private static readonly DependencyProperty IsTrackingProperty = DependencyProperty.RegisterAttached(
        "IsTracking", typeof(bool), typeof(SearchHighlight), new PropertyMetadata(false));

    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);

    private static void OnQueryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock text) return;
        if (!(bool)text.GetValue(IsTrackingProperty))
        {
            text.SetValue(IsTrackingProperty, true);
            // 刷新或容器复用导致文本改变时，重新计算位置，防止高亮残留。
            text.RegisterPropertyChangedCallback(TextBlock.TextProperty,
                (target, _) => Update((TextBlock)target));
        }
        Update(text);
    }

    private static void Update(TextBlock text)
    {
        text.TextHighlighters.Clear();
        var matches = ProcessSearch.FindMatches(text.Text ?? "", ProcessSearch.ParseTerms(GetQuery(text)));
        if (matches.Count == 0) return;

        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(Colors.Gold),
            Foreground = new SolidColorBrush(Colors.Black)
        };
        foreach (var match in matches)
            highlighter.Ranges.Add(new TextRange { StartIndex = match.Start, Length = match.Length });
        text.TextHighlighters.Add(highlighter);
    }
}
