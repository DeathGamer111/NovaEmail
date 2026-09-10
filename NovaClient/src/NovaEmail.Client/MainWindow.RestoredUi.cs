using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using NovaEmail.Intelligence;
using NovaEmail.Rendering;
using NovaEmail.Storage;
using Windows.UI.ViewManagement;

namespace NovaEmail.Client;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<MailItem> _archive = [];
    private readonly ObservableCollection<MailItem> _trash = [];
    private readonly ObservableCollection<MailItem> _junk = [];
    private readonly ThemePreferenceStore _themePreferenceStore = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaEmail",
            "theme.json"));
    private readonly MailboxPresentationStateStore _mailboxStateStore = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaEmail",
            "mailbox-state.json"));
    private MailboxPresentationState _mailboxState = MailboxPresentationState.Empty;
    private bool _initializingTheme = true;
    private bool _inlineReplyAll;
    private bool _updatingCompactMailboxSelector;

    private void InitializeRestoredUi()
    {
        _mailboxState = _mailboxStateStore.Load().State;
        foreach (var label in _mailboxState.Labels)
            AddUserLabelNavigationItem(label);
        RefreshCompactMailboxSelector();
        ThemeSelector.Items.Clear();
        foreach (var definition in ApplicationThemeCatalog.All)
        {
            ThemeSelector.Items.Add(new ComboBoxItem
            {
                Content = definition.DisplayName,
                Tag = definition.Id,
            });
        }

        var preference = _themePreferenceStore.Load();
        var selected = ApplicationThemeCatalog.Get(preference.Selection);
        ThemeSelector.SelectedItem = ThemeSelector.Items.OfType<ComboBoxItem>().Single(item =>
            string.Equals(item.Tag?.ToString(), selected.Id, StringComparison.Ordinal));
        _initializingTheme = false;
        ApplySelectedTheme();
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingTheme || !TryGetSelectedTheme(out var selected)) return;
        try
        {
            _themePreferenceStore.Save(selected.Selection);
            ApplySelectedTheme();
            ShowStatus($"Theme changed to {selected.DisplayName}.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            ApplySelectedTheme();
            ShowStatus($"The theme is active for this session, but the preference could not be saved: {exception.Message}",
                InfoBarSeverity.Warning);
        }
    }

    private bool TryGetSelectedTheme(out ApplicationThemeDefinition definition) =>
        ApplicationThemeCatalog.TryGetById(
            (ThemeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out definition);

    private void ApplySelectedTheme()
    {
        if (!TryGetSelectedTheme(out var selected)) return;
        var resolved = ApplicationThemeCatalog.Resolve(
            selected.Selection,
            DateOnly.FromDateTime(DateTime.Now),
            new AccessibilitySettings().HighContrast);
        RootLayout.RequestedTheme = resolved.Effective.BaseTheme switch
        {
            ApplicationThemeBase.Light => ElementTheme.Light,
            ApplicationThemeBase.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var palette = resolved.Effective.Palette ?? ReadSystemPalette();
        var page = NewThemeBrush(palette.PageBackground);
        var surface = NewThemeBrush(palette.Surface);
        var accent = NewThemeBrush(palette.Banner);
        RootLayout.Background = page;
        MainCommandBar.Background = surface;
        MailboxActionBar.Background = surface;
        FolderPane.Background = surface;
        StatusBar.Background = surface;
        if (Application.Current.Resources["NovaAccentBrush"] is SolidColorBrush accentBrush)
            accentBrush.Color = accent.Color;
        if (Application.Current.Resources["NovaAccentSoftBrush"] is SolidColorBrush softBrush)
            softBrush.Color = surface.Color;
    }

    private static ApplicationThemePalette ReadSystemPalette()
    {
        var settings = new UISettings();
        var background = ToThemeColor(settings.GetColorValue(UIColorType.Background));
        var foreground = ToThemeColor(settings.GetColorValue(UIColorType.Foreground));
        var accent = ToThemeColor(settings.GetColorValue(UIColorType.Accent));
        var bannerText = foreground.ContrastRatio(accent) >= background.ContrastRatio(accent)
            ? foreground
            : background;
        return new ApplicationThemePalette(background, background, accent, bannerText, foreground);
    }

    private static SolidColorBrush NewThemeBrush(ThemeColor color) => new(
        Windows.UI.Color.FromArgb(255, color.Red, color.Green, color.Blue));

    private static ThemeColor ToThemeColor(Windows.UI.Color color) =>
        new(color.R, color.G, color.B);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowFolder("Settings");

    private void CalendarButton_Click(object sender, RoutedEventArgs e) => ShowFolder("Calendar");

    private void ManageContactsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseContactsPane();
        ShowFolder("Contacts");
    }

    private void ShowComposeWorkspace()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ContactsView.Visibility = Visibility.Collapsed;
        CalendarView.Visibility = Visibility.Collapsed;
        MessageDetailView.Visibility = Visibility.Collapsed;
        MessageListColumn.Visibility = Visibility.Visible;
        Grid.SetColumn(ContentColumn, 2);
        Grid.SetColumnSpan(ContentColumn, 1);
        ComposeLayer.Visibility = Visibility.Visible;
    }

    private void CloseComposer()
    {
        var previouslySelected = _selectedMessage;
        ComposeLayer.Visibility = Visibility.Collapsed;
        var folder = _activeFolder is "Settings" or "Contacts" or "Calendar" ? "Inbox" : _activeFolder;
        ShowFolder(folder);
        if (previouslySelected is null) return;
        var matchingItem = MessageList.Items.OfType<MailItem>().FirstOrDefault(item =>
            item.Id.Equals(previouslySelected.Id, StringComparison.Ordinal));
        if (matchingItem is not null) MessageList.SelectedItem = matchingItem;
    }

    private string GetComposeBodyText()
    {
        ComposeBodyInput.Document.GetText(TextGetOptions.NoHidden, out var text);
        return text.TrimEnd('\r');
    }

    private string GetComposeRtf()
    {
        ComposeBodyInput.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        return rtf;
    }

    private void SetComposeBodyText(string? text) =>
        ComposeBodyInput.Document.SetText(TextSetOptions.None, text ?? string.Empty);

    private void OpenInlineReply(bool replyAll)
    {
        if (_selectedMessage is null) return;
        _inlineReplyAll = replyAll;
        InlineReplyHeading.Text = replyAll ? "Reply All" : "Reply";
        InlineReplyRecipients.Text = replyAll
            ? $"To {_selectedMessage.Sender}; Cc {_selectedMessage.Recipients}"
            : $"To {_selectedMessage.Sender}";
        InlineReplyBody.Text = string.Empty;
        InlineReplyComposer.Visibility = Visibility.Visible;
        InlineReplyBody.Focus(FocusState.Programmatic);
    }

    private void ReplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null)
        {
            ShowStatus("Select a message before replying.", InfoBarSeverity.Warning);
            return;
        }
        OpenInlineReply(replyAll: true);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null)
        {
            ShowStatus("Select a message before forwarding.", InfoBarSeverity.Warning);
            return;
        }
        var selected = _selectedMessage;
        OpenComposer();
        ComposeHeading.Text = "Forward message";
        ComposeSubjectBox.Text = selected.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
            ? selected.Subject
            : $"Fwd: {selected.Subject}";
        SetComposeBodyText(
            $"\r\n\r\n---------- Forwarded message ----------\r\nFrom: {selected.Sender}\r\n" +
            $"Date: {selected.Timestamp.LocalDateTime:g}\r\nTo: {selected.Recipients}\r\nSubject: {selected.Subject}\r\n\r\n{selected.Body}");
    }

    private void CancelInlineReplyButton_Click(object sender, RoutedEventArgs e) =>
        InlineReplyComposer.Visibility = Visibility.Collapsed;

    private void ExpandInlineReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null) return;
        var body = InlineReplyBody.Text;
        var replyAll = _inlineReplyAll;
        var selected = _selectedMessage;
        OpenComposer();
        ComposeHeading.Text = replyAll ? "Reply All" : "Reply";
        ComposeToBox.Text = ExtractMailboxOrFallback(selected.Sender);
        ComposeCcBox.Text = replyAll ? selected.Recipients : string.Empty;
        ComposeSubjectBox.Text = selected.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? selected.Subject
            : $"Re: {selected.Subject}";
        SetComposeBodyText(body +
            $"\r\n\r\nOn {selected.Timestamp.LocalDateTime:g}, {selected.Sender} wrote:\r\n> " +
            selected.Body.Replace("\n", "\n> ", StringComparison.Ordinal));
    }

    private void StageInlineReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null) return;
        var selected = _selectedMessage;
        _activeDraftId = null;
        _composeAttachments.Clear();
        ComposeToBox.Text = ExtractMailboxOrFallback(selected.Sender);
        ComposeCcBox.Text = _inlineReplyAll ? selected.Recipients : string.Empty;
        ComposeBccBox.Text = string.Empty;
        ComposeSubjectBox.Text = selected.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? selected.Subject
            : $"Re: {selected.Subject}";
        SetComposeBodyText(InlineReplyBody.Text +
            $"\r\n\r\nOn {selected.Timestamp.LocalDateTime:g}, {selected.Sender} wrote:\r\n> " +
            selected.Body.Replace("\n", "\n> ", StringComparison.Ordinal));
        ComposeHtmlInput.Text = string.Empty;
        ComposePrioritySelector.SelectedIndex = 0;
        ComposeReadReceiptCheckBox.IsChecked = false;
        StageOutboxButton_Click(sender, e);
    }

    private async void SuggestRepliesButton_Click(object sender, RoutedEventArgs e)
    {
        var replies = await RequestSuggestedRepliesAsync();
        if (replies is null) return;
        AiResultText.Text = string.Join("\n\n", replies.Select(reply => $"{reply.Label}\n{reply.Body}"));
        AiResultText.Visibility = Visibility.Visible;
    }

    private async void DraftInlineReplyWithAiButton_Click(object sender, RoutedEventArgs e)
    {
        var replies = await RequestSuggestedRepliesAsync();
        if (replies is null) return;
        InlineReplyBody.Text = replies[0].Body;
        InlineReplyBody.Focus(FocusState.Programmatic);
    }

    private async Task<IReadOnlyList<SuggestedReply>?> RequestSuggestedRepliesAsync()
    {
        if (_selectedMessage is null)
        {
            ShowStatus("Select a message first.", InfoBarSeverity.Warning);
            return null;
        }
        if (AiMessageConsentCheckBox.IsChecked is not true)
        {
            ShowStatus("Check the AI consent box for this request first.", InfoBarSeverity.Warning);
            return null;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var coordinator = CreateAiCoordinator(out var transport);
            using (transport)
            {
                var replies = await coordinator.SuggestRepliesAsync(
                    EnabledAiProfile(),
                    ToAiSource(_selectedMessage),
                    new EmailIntelligenceConsent(
                        _selectedMessage.Id,
                        EmailIntelligenceOperation.SuggestReplies,
                        now,
                        AllowMessageContentProcessing: true),
                    now);
                AiMessageConsentCheckBox.IsChecked = false;
                ShowStatus("AI reply suggestions are ready for review. Nothing was sent.", InfoBarSeverity.Success);
                return replies;
            }
        }
        catch (Exception exception)
        {
            ShowStatus($"AI reply suggestions failed safely: {exception.Message}", InfoBarSeverity.Error);
            return null;
        }
    }

    private MailItem[] SelectedMessages() =>
        MessageList.SelectedItems.OfType<MailItem>().ToArray();

    private void RestorePersistedMailboxState(ObservableCollection<MailItem> source)
    {
        foreach (var message in source.ToArray())
        {
            if (!_mailboxState.Messages.TryGetValue(message.Id, out var state)) continue;
            message.IsRead = state.IsRead;
            message.IsFollowUp = state.IsFollowUp;
            var destination = ResolveLocalMailboxCollection(state.Folder);
            if (destination is null || ReferenceEquals(destination, source)) continue;
            source.Remove(message);
            if (!destination.Any(existing => existing.Id.Equals(message.Id, StringComparison.Ordinal)))
            {
                message.Folder = state.Folder;
                destination.Add(message);
            }
        }
    }

    private ObservableCollection<MailItem>? ResolveLocalMailboxCollection(string folder) => folder switch
    {
        "Inbox" => _inbox,
        "Drafts" => _drafts,
        "Outbox" => _outbox,
        "Sent" => _sent,
        "Archive" => _archive,
        "Trash" => _trash,
        "Junk" => _junk,
        _ => null,
    };

    private bool TryPersistMessageStates(IEnumerable<MailItem> changedMessages, out string? diagnostic)
    {
        var messages = new Dictionary<string, MailboxMessagePresentationState>(
            _mailboxState.Messages, StringComparer.Ordinal);
        foreach (var message in changedMessages)
        {
            var persistedFolder = ResolveLocalMailboxCollection(message.Folder) is null
                ? string.Empty
                : message.Folder;
            messages[message.Id] = new MailboxMessagePresentationState(
                persistedFolder, message.IsRead, message.IsFollowUp);
        }

        _mailboxState = _mailboxState with { Messages = messages };
        try
        {
            _mailboxStateStore.Save(_mailboxState);
            diagnostic = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    private void UpdateMessageActionState()
    {
        var count = MessageList.SelectedItems.Count;
        MessageSelectionCountText.Text = count == 0 ? string.Empty : $"{count} selected";
        var hasSelection = count > 0;
        ReplyButton.IsEnabled = count == 1;
        ReplyAllButton.IsEnabled = count == 1;
        ForwardButton.IsEnabled = count == 1;
        SummarizeWithAssistantButton.IsEnabled = count == 1;
        SuggestRepliesWithAssistantButton.IsEnabled = count == 1;
        ArchiveSelectedMessagesButton.IsEnabled = hasSelection && _activeFolder != "Archive";
        DeleteSelectedMessagesButton.IsEnabled = hasSelection && _activeFolder != "Trash";
        RestoreSelectedMessagesButton.IsEnabled = hasSelection && _activeFolder == "Trash";
        BlockSelectedMessagesButton.IsEnabled = hasSelection && _activeFolder != "Junk";
        MarkSelectedReadButton.IsEnabled = hasSelection;
        MarkSelectedUnreadButton.IsEnabled = hasSelection;
    }

    private ObservableCollection<MailItem>? FindMessageCollection(MailItem message)
    {
        ObservableCollection<MailItem>[] candidates =
            [_inbox, _drafts, _outbox, _sent, _archive, _trash, _junk, _remoteFolderMessages];
        return candidates.FirstOrDefault(collection => collection.Contains(message));
    }

    private void RefreshActiveMessageView()
    {
        if (_activeFolder.StartsWith("label:", StringComparison.Ordinal))
            ShowLabel(_activeFolder);
        else
            ShowFolder(_activeFolder);
    }

    private void MoveSelectedMessages(ObservableCollection<MailItem> destination, string folder)
    {
        var selected = SelectedMessages();
        if (selected.Length == 0) return;
        var moved = 0;
        foreach (var message in selected)
        {
            var source = FindMessageCollection(message);
            if (source is null || ReferenceEquals(source, destination)) continue;
            source.Remove(message);
            message.Folder = folder;
            destination.Insert(0, message);
            moved++;
        }
        if (moved == 0) return;
        var persisted = TryPersistMessageStates(selected, out var diagnostic);
        RefreshActiveMessageView();
        ShowStatus(
            persisted
                ? $"Moved {moved} message{(moved == 1 ? string.Empty : "s")} to {folder} locally."
                : $"Moved {moved} message{(moved == 1 ? string.Empty : "s")} for this session, but the state could not be saved: {diagnostic}",
            persisted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void ArchiveSelectedMessagesButton_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedMessages(_archive, "Archive");

    private void DeleteSelectedMessagesButton_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedMessages(_trash, "Trash");

    private void BlockSelectedMessagesButton_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedMessages(_junk, "Junk");

    private void RestoreSelectedMessagesButton_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedMessages(_inbox, "Inbox");

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) => MessageList.SelectAll();

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e) => MessageList.SelectedItems.Clear();

    private void MarkSelectedReadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMessages();
        foreach (var message in selected) message.IsRead = true;
        var persisted = TryPersistMessageStates(selected, out var diagnostic);
        RefreshActiveMessageView();
        ShowStatus(
            persisted
                ? "Selected messages were marked read locally."
                : $"Messages were marked read for this session, but the state could not be saved: {diagnostic}",
            persisted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void MarkSelectedUnreadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMessages();
        foreach (var message in selected) message.IsRead = false;
        var persisted = TryPersistMessageStates(selected, out var diagnostic);
        RefreshActiveMessageView();
        ShowStatus(
            persisted
                ? "Selected messages were marked unread locally."
                : $"Messages were marked unread for this session, but the state could not be saved: {diagnostic}",
            persisted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void AddLabelButton_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Label name", PlaceholderText = "For example: Customer follow-up", MaxLength = 60 };
        var filter = new TextBox { Header = "Filter text", PlaceholderText = "Messages containing this text", MaxLength = 160 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(name);
        content.Children.Add(filter);
        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Create Label",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        if (_mailboxState.Labels.Count >= MailboxPresentationStateStore.MaximumLabels)
        {
            ShowStatus("The local label limit has been reached.", InfoBarSeverity.Warning);
            return;
        }

        var label = new MailboxLabelDefinition(
            Guid.NewGuid().ToString("N"), name.Text.Trim(), filter.Text.Trim());
        _mailboxState = _mailboxState with { Labels = _mailboxState.Labels.Append(label).ToArray() };
        AddUserLabelNavigationItem(label);
        try
        {
            _mailboxStateStore.Save(_mailboxState);
            ShowStatus($"Created local label {label.DisplayName}.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException)
        {
            ShowStatus($"Created label {label.DisplayName} for this session, but it could not be saved: {exception.Message}",
                InfoBarSeverity.Warning);
        }
    }

    private void AddUserLabelNavigationItem(MailboxLabelDefinition label) =>
        LabelNavigationList.Items.Add(new ListViewItem
        {
            Content = label.DisplayName,
            Tag = "label:user:" + label.Id,
        });

    private void LabelNavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LabelNavigationList.SelectedItem is not ListViewItem { Tag: string tag }) return;
        FolderNavigation.SelectedItem = null;
        ShowLabel(tag);
    }

    private void ShowLabel(string tag)
    {
        var all = _inbox.Concat(_drafts).Concat(_outbox).Concat(_sent).Concat(_archive).Concat(_trash).Concat(_junk);
        var matches = tag switch
        {
            "label:unread" => all.Where(message => !message.IsRead),
            "label:attachments" => all.Where(message => message.HasAttachments),
            "label:followup" => all.Where(message => message.IsFollowUp),
            _ when tag.StartsWith("label:user:", StringComparison.Ordinal) =>
                all.Where(message => MatchesUserLabel(message, tag)),
            _ => [],
        };
        _activeFolder = tag;
        FolderTitle.Text = LabelNavigationList.Items.OfType<ListViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))?
            .Content?.ToString() ?? "Label";
        SettingsView.Visibility = Visibility.Collapsed;
        ContactsView.Visibility = Visibility.Collapsed;
        CalendarView.Visibility = Visibility.Collapsed;
        ComposeLayer.Visibility = Visibility.Collapsed;
        MessageDetailView.Visibility = Visibility.Visible;
        MessageListColumn.Visibility = Visibility.Visible;
        Grid.SetColumn(ContentColumn, 2);
        Grid.SetColumnSpan(ContentColumn, 1);
        ApplyFilter(matches, SearchBox.Text);
    }

    private bool MatchesUserLabel(MailItem message, string tag)
    {
        var labelId = tag["label:user:".Length..];
        var filter = _mailboxState.Labels.FirstOrDefault(label =>
            label.Id.Equals(labelId, StringComparison.Ordinal))?.FilterText;
        return filter is not null && (string.IsNullOrWhiteSpace(filter) ||
            message.Sender.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            message.Subject.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            message.Body.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshCompactMailboxSelector()
    {
        var selectedTag = (CompactMailboxSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        _updatingCompactMailboxSelector = true;
        try
        {
            CompactMailboxSelector.Items.Clear();
            foreach (var folder in FolderNavigation.Items.OfType<ListViewItem>())
            {
                if (folder.Tag is not string tag) continue;
                CompactMailboxSelector.Items.Add(new ComboBoxItem
                {
                    Content = CompactFolderDisplayName(folder, tag),
                    Tag = tag,
                });
            }

            CompactMailboxSelector.SelectedItem = CompactMailboxSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedTag ?? _activeFolder,
                    StringComparison.Ordinal));
        }
        finally
        {
            _updatingCompactMailboxSelector = false;
        }
    }

    private static string CompactFolderDisplayName(ListViewItem item, string tag) =>
        item.Content is string displayName ? displayName : tag;

    private void SelectCompactMailbox(string folder)
    {
        var wasUpdating = _updatingCompactMailboxSelector;
        _updatingCompactMailboxSelector = true;
        try
        {
            CompactMailboxSelector.SelectedItem = CompactMailboxSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, folder, StringComparison.Ordinal));
        }
        finally
        {
            _updatingCompactMailboxSelector = wasUpdating;
        }
    }

    private void CompactMailboxSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCompactMailboxSelector ||
            CompactMailboxSelector.SelectedItem is not ComboBoxItem { Tag: string folder }) return;
        SelectFolder(folder);
    }

    private void ContactsPaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsPane.Visibility == Visibility.Visible)
        {
            CloseContactsPane();
            return;
        }
        ApplyContactsPaneFilter(ContactsPaneSearchBox.Text);
        ContactsColumn.Width = new GridLength(340);
        ContactsPane.Visibility = Visibility.Visible;
    }

    private void CloseContactsPaneButton_Click(object sender, RoutedEventArgs e) => CloseContactsPane();

    private void CloseContactsPane()
    {
        ContactsPane.Visibility = Visibility.Collapsed;
        ContactsColumn.Width = new GridLength(0);
    }

    private void ContactsPaneSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyContactsPaneFilter(ContactsPaneSearchBox.Text);

    private void ApplyContactsPaneFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        ContactsPaneList.ItemsSource = string.IsNullOrEmpty(normalized)
            ? _contacts.ToArray()
            : _contacts.Where(contact =>
                contact.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                contact.EmailAddress.Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void ContactsPaneList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ContactsPaneList.SelectedItem is not ContactItem contact) return;
        if (ComposeLayer.Visibility != Visibility.Visible) OpenComposer();
        ComposeToBox.Text = AppendAddress(ComposeToBox.Text, contact.EmailAddress);
        ComposeToBox.Focus(FocusState.Programmatic);
    }

    private static string AppendAddress(string? existing, string address) =>
        string.IsNullOrWhiteSpace(existing) ? address : existing.TrimEnd() + "; " + address;

    private void ComposeBoldButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.CharacterFormat;
        format.Bold = FormatEffect.Toggle;
        selection.CharacterFormat = format;
        ComposeBodyInput.Focus(FocusState.Programmatic);
    }

    private void ComposeItalicButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.CharacterFormat;
        format.Italic = FormatEffect.Toggle;
        selection.CharacterFormat = format;
        ComposeBodyInput.Focus(FocusState.Programmatic);
    }

    private void ComposeUnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        selection.CharacterFormat = format;
        ComposeBodyInput.Focus(FocusState.Programmatic);
    }

    private void ComposeFontSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ComposeFontSelector.SelectedItem is not ComboBoxItem { Tag: string font }) return;
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.CharacterFormat;
        format.Name = font;
        selection.CharacterFormat = format;
    }

    private void ComposeFontSizeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ComposeFontSizeSelector.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !float.TryParse(value, out var size)) return;
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.CharacterFormat;
        format.Size = size;
        selection.CharacterFormat = format;
    }

    private void ComposeTextColorItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string value })
        {
            var selection = ComposeBodyInput.Document.Selection;
            var format = selection.CharacterFormat;
            format.ForegroundColor = ParseColor(value);
            selection.CharacterFormat = format;
        }
    }

    private void ComposeHighlightColorItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string value })
        {
            var selection = ComposeBodyInput.Document.Selection;
            var format = selection.CharacterFormat;
            format.BackgroundColor = ParseColor(value);
            selection.CharacterFormat = format;
        }
    }

    private static Windows.UI.Color ParseColor(string value)
    {
        var argb = Convert.ToUInt32(value[1..], 16);
        return Windows.UI.Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
    }

    private void ComposeAlignmentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string value }) return;
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.ParagraphFormat;
        format.Alignment = value switch
        {
            "Center" => ParagraphAlignment.Center,
            "Right" => ParagraphAlignment.Right,
            _ => ParagraphAlignment.Left,
        };
        selection.ParagraphFormat = format;
    }

    private void ComposeNumberingButton_Click(object sender, RoutedEventArgs e) =>
        SetComposeListType(MarkerType.Arabic);

    private void ComposeBulletsButton_Click(object sender, RoutedEventArgs e) =>
        SetComposeListType(MarkerType.Bullet);

    private void SetComposeListType(MarkerType listType)
    {
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.ParagraphFormat;
        format.ListType = format.ListType == listType ? MarkerType.None : listType;
        if (listType == MarkerType.Arabic) format.ListStyle = MarkerStyle.Period;
        selection.ParagraphFormat = format;
    }

    private void ComposeIndentLeftButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustComposeIndent(-24);
    }

    private void ComposeIndentRightButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustComposeIndent(24);
    }

    private void AdjustComposeIndent(float change)
    {
        var selection = ComposeBodyInput.Document.Selection;
        var format = selection.ParagraphFormat;
        format.SetIndents(
            format.FirstLineIndent,
            Math.Max(0, format.LeftIndent + change),
            Math.Max(0, format.RightIndent));
        selection.ParagraphFormat = format;
    }
}
