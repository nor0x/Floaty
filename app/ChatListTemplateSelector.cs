namespace Floaty;

/// <summary>
/// Picks the right template for the shared message list: the chat bubble for <see cref="ChatMessageVm"/>
/// items, and the conversation-switcher row for <see cref="ConversationItemVm"/> items.
/// </summary>
public sealed class ChatListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MessageTemplate { get; set; }
    public DataTemplate? ConversationTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        item is ConversationItemVm ? ConversationTemplate! : MessageTemplate!;
}
