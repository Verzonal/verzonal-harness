using Dsh.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dsh.App.Views;

/// <summary>
/// Picks the card for one conversation row.
/// </summary>
/// <remarks>
/// The row types are a closed set the projection produces, so an unrecognized value
/// means the projection grew an arm this view has not been taught. Falling back to
/// the plain user card keeps such a row visible rather than blank.
/// </remarks>
public sealed partial class ConversationTemplateSelector : DataTemplateSelector
{
    /// <summary>A person's prompt.</summary>
    public DataTemplate? User { get; set; }

    /// <summary>Context a plugin injected, drawn collapsed.</summary>
    public DataTemplate? Context { get; set; }

    /// <summary>The model's answer for one step.</summary>
    public DataTemplate? Assistant { get; set; }

    /// <summary>One tool call and its result.</summary>
    public DataTemplate? Tool { get; set; }

    /// <summary>A turn that ended badly.</summary>
    public DataTemplate? Failure { get; set; }

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        UserNode { IsContext: true } => Context,
        UserNode => User,
        AssistantNode => Assistant,
        ToolNode => Tool,
        TurnFailureNode => Failure,
        _ => User,
    };

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
