using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MimeKit;
using MimeKit.Utils;
using NovaEmail.Assistant;
using NovaEmail.Intelligence;
using NovaEmail.Mail;
using NovaEmail.Rendering;
using NovaEmail.Safety;
using NovaEmail.Storage;
using NovaEmail.Sync;

namespace NovaEmail.Client;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<MailItem> _inbox = [];
    private readonly ObservableCollection<MailItem> _drafts = [];
    private readonly ObservableCollection<MailItem> _outbox = [];
    private readonly ObservableCollection<MailItem> _sent = [];
    private readonly HashSet<string> _synchronizedInboxIds = new(StringComparer.Ordinal);
    private readonly List<ContactItem> _contacts = [];
    private readonly List<CalendarItem> _calendarEvents = [];
    private readonly WindowsCredentialVault _mailVault = new();
    private readonly WindowsSecretVault _secretVault = new();
    private ModernMailStore? _store;
    private ClientSettings _settings = ClientSettings.Default;
    private MailItem? _selectedMessage;
    private string _activeFolder = "Inbox";
    private string? _activeDraftId;
    private string? _editingContactId;
    private string? _editingCalendarEventId;
    private bool _uiReady;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarRegion);
            SeedInbox();
            ResetContactEditor();
            ResetCalendarEditor();
            _uiReady = true;
            FolderNavigation.SelectedIndex = 0;
            Activated += MainWindow_Activated;
        }
        catch (Exception exception)
        {
            App.LogStartupFailure(exception);
            throw;
        }
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await ClientSettingsStore.LoadAsync();
            _store = ModernMailStore.CreateLocalStore();
            await _store.InitializeAsync();
            await LoadPersistedItemsAsync();
            await LoadContactsAndCalendarAsync();
            await LoadSettingsAsync();
            await LoadSynchronizedInboxAsync();
            ShowFolder("Inbox");
            MessageList.SelectedIndex = 0;
            ShowStatus("Local demo ready. Send stages messages in Outbox; no external mail is submitted.");
        }
        catch (Exception exception)
        {
            ShowStatus($"Nova Email opened with in-memory demo data. Local storage could not start ({exception.GetType().Name}).",
                InfoBarSeverity.Warning);
            ShowFolder("Inbox");
        }
    }

    private void SeedInbox()
    {
        var now = DateTimeOffset.Now;
        _inbox.Add(new MailItem
        {
            Id = "demo-welcome",
            Folder = "Inbox",
            Sender = "Nova Email Team",
            Recipients = "demo@novaemail.local",
            Subject = "Welcome to your private Nova Email demo",
            Preview = "Compose, reply, search, stage mail in Outbox, and try consent-gated AI.",
            Body = "Welcome to Nova Email.\n\nThis local demo is intentionally safe: clicking Stage in Outbox stores a complete MIME message on this computer but does not contact an SMTP server. Configure an IMAP/SMTP account in Settings when you are ready to test connectivity.\n\nAI actions are also explicit. Nova Email sends message content to the configured assistant only after you check the consent box for that request.",
            Timestamp = now.AddMinutes(-18),
        });
        _inbox.Add(new MailItem
        {
            Id = "demo-planning",
            Folder = "Inbox",
            Sender = "Morgan Lee",
            Recipients = "demo@novaemail.local",
            Subject = "Planning session — Thursday at 2 PM",
            Preview = "Could you review the agenda and confirm the planning session?",
            Body = "Hi,\n\nCould you review the draft agenda and confirm our planning session for Thursday at 2:00 PM? I would especially like your thoughts on the launch checklist and the customer feedback section.\n\nThanks,\nMorgan",
            Timestamp = now.AddHours(-2),
        });
        _inbox.Add(new MailItem
        {
            Id = "demo-design",
            Folder = "Inbox",
            Sender = "Alex Rivera",
            Recipients = "demo@novaemail.local",
            Subject = "Updated interface notes",
            Preview = "The new three-pane layout feels much easier to scan.",
            Body = "The new three-pane layout feels much easier to scan. I left a few notes about keyboard navigation and responsive sizing, but the overall direction is solid.\n\nNo action is urgent; take a look when convenient.",
            Timestamp = now.AddDays(-1),
        });
        _inbox.Add(new MailItem
        {
            Id = "demo-receipt",
            Folder = "Inbox",
            Sender = "Northwind Books",
            Recipients = "demo@novaemail.local",
            Subject = "Your sample order receipt",
            Preview = "This synthetic receipt demonstrates transactional mail rendering.",
            Body = "Thank you for your sample order.\n\nOrder: NV-1042\nStatus: Demonstration only\nTotal: $0.00\n\nThis message contains no real purchase or account information.",
            Timestamp = now.AddDays(-3),
        });
    }

    private async Task LoadPersistedItemsAsync()
    {
        if (_store is null) return;
        foreach (var draft in await _store.ReadLatestDraftsAsync())
        {
            _drafts.Add(new MailItem
            {
                Id = draft.DraftId,
                Folder = "Drafts",
                Sender = draft.Sender,
                Recipients = draft.Recipients,
                Subject = string.IsNullOrWhiteSpace(draft.Subject) ? "(No subject)" : draft.Subject,
                Preview = draft.BodySnippet,
                Body = draft.BodySnippet,
                Timestamp = draft.SavedUtc,
            });
        }

        foreach (var operation in await _store.ReadOutboundOperationSummariesAsync())
        {
            try
            {
                var mime = await SafeMimeParser.ParseAsync(await _store.ReadOutboundMimeAsync(operation.OperationId));
                _outbox.Add(new MailItem
                {
                    Id = operation.OperationId,
                    Folder = "Outbox",
                    Sender = mime.From.ToString(),
                    Recipients = mime.To.ToString(),
                    Subject = string.IsNullOrWhiteSpace(mime.Subject) ? "(No subject)" : mime.Subject,
                    Preview = mime.TextBody ?? "Queued MIME message",
                    Body = mime.TextBody ?? mime.HtmlBody ?? "Queued MIME message",
                    Timestamp = operation.EventUtc,
                });
            }
            catch
            {
                // A corrupt queued item remains protected by the store and is not rendered.
            }
        }
    }

    private async Task LoadSettingsAsync()
    {
        AiEndpointBox.Text = _settings.AiEndpoint;
        AiModelBox.Text = _settings.AiModel;
        if (_store is null) return;
        var profiles = await _store.ReadLatestMailAccountProfilesAsync();
        var profile = SelectActiveProfile(profiles);
        if (profile is null)
        {
            EmailAddressBox.Text = _settings.SenderAddress;
            AccountUserBox.Text = _settings.SenderAddress;
            return;
        }
        DisplayNameBox.Text = profile.DisplayName;
        EmailAddressBox.Text = profile.SenderAddress;
        ImapHostBox.Text = profile.ReceiveHost;
        ImapPortBox.Value = profile.ReceivePort;
        SmtpHostBox.Text = profile.SmtpHost;
        SmtpPortBox.Value = profile.SmtpPort;
        AccountUserBox.Text = profile.CredentialUserName;
        SelectTls(ImapTlsBox, profile.ReceiveTlsMode);
        SelectTls(SmtpTlsBox, profile.SmtpTlsMode);
    }

    private async Task LoadSynchronizedInboxAsync(string? preferredFolderId = null)
    {
        if (_store is null) return;
        var profiles = await _store.ReadLatestMailAccountProfilesAsync();
        var profile = SelectActiveProfile(profiles);
        if (profile is null) return;
        var folders = await _store.ReadStoredFolderSummariesAsync();
        var inboxFolder = folders.FirstOrDefault(folder =>
                folder.AccountId.Equals(profile.AccountId, StringComparison.Ordinal) &&
                !folder.IsTombstoned &&
                folder.FolderId.Equals(preferredFolderId, StringComparison.Ordinal)) ??
            folders.FirstOrDefault(folder =>
                folder.AccountId.Equals(profile.AccountId, StringComparison.Ordinal) &&
                !folder.IsTombstoned &&
                folder.RemoteFullName?.Equals("INBOX", StringComparison.OrdinalIgnoreCase) is true);
        if (inboxFolder is null) return;

        foreach (var existing in _inbox.Where(item => _synchronizedInboxIds.Contains(item.Id)).ToArray())
            _inbox.Remove(existing);
        _synchronizedInboxIds.Clear();

        var page = await _store.ListStoredFolderMessagesPageAsync(inboxFolder.FolderId, pageSize: 500);
        foreach (var stored in page.Messages)
        {
            try
            {
                var mime = await SafeMimeParser.ParseAsync(await _store.ReadStoredMimeAsync(stored.MessageId));
                var body = !string.IsNullOrWhiteSpace(mime.TextBody)
                    ? mime.TextBody
                    : SafeHtmlRenderer.CreateReadableTextFallback(mime.HtmlBody);
                if (string.IsNullOrWhiteSpace(body)) body = stored.Snippet;
                var timestamp = mime.Date == DateTimeOffset.MinValue
                    ? stored.SentUtc ?? DateTimeOffset.Now
                    : mime.Date;
                _inbox.Add(new MailItem
                {
                    Id = stored.MessageId,
                    Folder = "Inbox",
                    Sender = string.IsNullOrWhiteSpace(stored.Sender) ? mime.From.ToString() : stored.Sender,
                    Recipients = string.IsNullOrWhiteSpace(stored.Recipients) ? mime.To.ToString() : stored.Recipients,
                    Subject = string.IsNullOrWhiteSpace(stored.Subject) ? "(No subject)" : stored.Subject,
                    Preview = CreatePreview(body),
                    Body = body,
                    Timestamp = timestamp,
                });
                _synchronizedInboxIds.Add(stored.MessageId);
            }
            catch
            {
                // Corrupt or unsupported MIME remains isolated in the store and is not rendered.
            }
        }
    }

    private static string CreatePreview(string value)
    {
        var singleLine = string.Join(' ', value.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= 180 ? singleLine : singleLine[..177] + "…";
    }

    private StoredMailAccountProfile? SelectActiveProfile(
        IReadOnlyList<StoredMailAccountProfile> profiles) =>
        profiles.FirstOrDefault(profile =>
            profile.SenderAddress.Equals(_settings.SenderAddress, StringComparison.OrdinalIgnoreCase)) ??
        profiles.OrderByDescending(profile => profile.SavedUtc).FirstOrDefault();

    private static string BuildAccountId(string senderAddress) =>
        "mail-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(senderAddress.ToLowerInvariant())))[..20];

    private async Task LoadContactsAndCalendarAsync()
    {
        if (_store is null) return;

        _contacts.Clear();
        _contacts.AddRange((await _store.ReadLatestContactsAsync(maximumCount: 500))
            .Select(ContactItem.FromStored));

        var rangeStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var rangeEnd = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        _calendarEvents.Clear();
        _calendarEvents.AddRange((await _store.ReadLocalCalendarAgendaAsync(
                rangeStart, rangeEnd, maximumCount: 500))
            .Select(CalendarItem.FromStored));
    }

    private void FolderNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (FolderNavigation.SelectedItem is ListViewItem item && item.Tag is string folder)
            ShowFolder(folder);
    }

    private void ShowFolder(string folder)
    {
        _activeFolder = folder;
        FolderTitle.Text = folder;
        var settings = folder == "Settings";
        var contacts = folder == "Contacts";
        var calendar = folder == "Calendar";
        var fullWidthView = settings || contacts || calendar;
        SettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ContactsView.Visibility = contacts ? Visibility.Visible : Visibility.Collapsed;
        CalendarView.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        MessageDetailView.Visibility = fullWidthView ? Visibility.Collapsed : Visibility.Visible;
        MessageListColumn.Visibility = fullWidthView ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ContentColumn, fullWidthView ? 1 : 2);
        Grid.SetColumnSpan(ContentColumn, fullWidthView ? 2 : 1);
        SearchBox.IsEnabled = !settings;
        SearchBox.PlaceholderText = contacts
            ? "Search contacts"
            : calendar
                ? "Search calendar"
                : "Search mail";
        if (settings) return;
        if (contacts)
        {
            ApplyContactFilter(SearchBox.Text);
            return;
        }
        if (calendar)
        {
            ApplyCalendarFilter(SearchBox.Text);
            return;
        }

        var source = folder switch
        {
            "Inbox" => _inbox,
            "Drafts" => _drafts,
            "Outbox" => _outbox,
            "Sent" => _sent,
            _ => [],
        };
        ApplyFilter(source, SearchBox.Text);
        AiMessageConsentCheckBox.Visibility = Visibility.Visible;
    }

    private void ApplyFilter(IEnumerable<MailItem> source, string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var items = string.IsNullOrEmpty(normalized)
            ? source.ToArray()
            : source.Where(item =>
                item.Sender.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Subject.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Body.Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        MessageList.ItemsSource = items;
        FolderCount.Text = items.Length == 1 ? "1 item" : $"{items.Length} items";
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ShowFolder(_activeFolder);
    }

    private void ApplyContactFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var items = string.IsNullOrEmpty(normalized)
            ? _contacts.ToArray()
            : _contacts.Where(contact =>
                contact.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                contact.EmailAddress.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                contact.Notes.Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        ContactList.ItemsSource = items;
        ContactCountText.Text = items.Length == 1 ? "1 contact" : $"{items.Length} contacts";
    }

    private void ApplyCalendarFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var items = string.IsNullOrEmpty(normalized)
            ? _calendarEvents.ToArray()
            : _calendarEvents.Where(calendarEvent =>
                calendarEvent.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                calendarEvent.Location.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                calendarEvent.Notes.Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        CalendarList.ItemsSource = items;
        CalendarCountText.Text = items.Length == 1 ? "1 event" : $"{items.Length} events";
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not MailItem item) return;
        _selectedMessage = item;
        DetailSubject.Text = item.Subject;
        DetailSender.Text = item.Sender;
        DetailRecipients.Text = $"To {item.Recipients} · {item.Timestamp.LocalDateTime:g}";
        DetailInitial.Text = item.SenderInitial;
        DetailBody.Text = item.Body;
        AiResultText.Visibility = Visibility.Collapsed;
        AiMessageConsentCheckBox.IsChecked = false;
    }

    private void NewContactButton_Click(object sender, RoutedEventArgs e)
    {
        ContactList.SelectedItem = null;
        ResetContactEditor();
        ContactNameBox.Focus(FocusState.Programmatic);
    }

    private void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactList.SelectedItem is not ContactItem contact) return;
        _editingContactId = contact.ContactId;
        ContactEditorTitle.Text = contact.DisplayName;
        ContactNameBox.Text = contact.DisplayName;
        ContactEmailBox.Text = contact.EmailAddress;
        ContactNotesBox.Text = contact.Notes;
        SaveContactButton.IsEnabled = !contact.IsGroup;
        DeleteContactButton.IsEnabled = true;
        ContactEditorHint.Text = contact.IsGroup
            ? "Contact groups can be used for recipient resolution but are read-only in this demo editor."
            : $"Revision {contact.Revision} · stored only on this computer.";
    }

    private async void SaveContactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            ShowStatus("Local storage is unavailable; the contact was not saved.", InfoBarSeverity.Error);
            return;
        }
        try
        {
            var name = ContactNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("Enter a contact name.");
            var address = MailboxAddress.Parse(ContactEmailBox.Text.Trim()).Address;
            var saved = await _store.SaveContactRevisionAsync(new ContactRevisionInput(
                _editingContactId,
                name,
                address,
                ContactNotesBox.Text ?? string.Empty,
                IsGroup: false,
                GroupMembers: []));
            await LoadContactsAndCalendarAsync();
            ApplyContactFilter(SearchBox.Text);
            ContactList.SelectedItem = _contacts.FirstOrDefault(contact =>
                string.Equals(contact.ContactId, saved.ContactId, StringComparison.Ordinal));
            ShowStatus("Contact saved locally.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Contact was not saved: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void DeleteContactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || string.IsNullOrWhiteSpace(_editingContactId)) return;
        try
        {
            await _store.TombstoneContactAsync(_editingContactId);
            await LoadContactsAndCalendarAsync();
            ApplyContactFilter(SearchBox.Text);
            ContactList.SelectedItem = null;
            ResetContactEditor();
            ShowStatus("Contact removed from the active contact book.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Contact was not removed: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ResetContactEditor()
    {
        _editingContactId = null;
        ContactEditorTitle.Text = "New contact";
        ContactEditorHint.Text = "Contacts stay on this computer and are available when composing mail.";
        ContactNameBox.Text = string.Empty;
        ContactEmailBox.Text = string.Empty;
        ContactNotesBox.Text = string.Empty;
        SaveContactButton.IsEnabled = true;
        DeleteContactButton.IsEnabled = false;
    }

    private void NewCalendarEventButton_Click(object sender, RoutedEventArgs e)
    {
        CalendarList.SelectedItem = null;
        ResetCalendarEditor();
        CalendarTitleBox.Focus(FocusState.Programmatic);
    }

    private void CalendarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalendarList.SelectedItem is not CalendarItem calendarEvent) return;
        _editingCalendarEventId = calendarEvent.EventId;
        CalendarEditorTitle.Text = calendarEvent.Title;
        CalendarTitleBox.Text = calendarEvent.Title;
        CalendarLocationBox.Text = calendarEvent.Location;
        CalendarNotesBox.Text = calendarEvent.Notes;
        CalendarAllDayCheckBox.IsChecked = calendarEvent.AllDay;
        CalendarStartDatePicker.Date = ToDatePickerDate(calendarEvent.StartLocal);
        CalendarStartTimePicker.Time = calendarEvent.StartLocal.TimeOfDay;
        var displayedEnd = calendarEvent.AllDay
            ? calendarEvent.EndLocal.AddDays(-1)
            : calendarEvent.EndLocal;
        CalendarEndDatePicker.Date = ToDatePickerDate(displayedEnd);
        CalendarEndTimePicker.Time = calendarEvent.EndLocal.TimeOfDay;
        SetCalendarTimeControls();
        var editable = calendarEvent.SourceKind == CalendarEventSourceKind.LocalManual;
        SaveCalendarEventButton.IsEnabled = editable;
        DeleteCalendarEventButton.IsEnabled = editable;
        CalendarEditorHint.Text = editable
            ? "Local event · stored only on this computer."
            : $"{calendarEvent.SourceKind} event · read-only in the local editor.";
    }

    private void CalendarAllDayCheckBox_Click(object sender, RoutedEventArgs e) =>
        SetCalendarTimeControls();

    private async void SaveCalendarEventButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            ShowStatus("Local storage is unavailable; the event was not saved.", InfoBarSeverity.Error);
            return;
        }
        try
        {
            var title = CalendarTitleBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
                throw new InvalidDataException("Enter an event title.");
            var allDay = CalendarAllDayCheckBox.IsChecked is true;
            var start = allDay
                ? ToUnspecifiedDate(CalendarStartDatePicker.Date.Date)
                : ToUnspecifiedDate(CalendarStartDatePicker.Date.Date + CalendarStartTimePicker.Time);
            var end = allDay
                ? ToUnspecifiedDate(CalendarEndDatePicker.Date.Date.AddDays(1))
                : ToUnspecifiedDate(CalendarEndDatePicker.Date.Date + CalendarEndTimePicker.Time);
            var saved = await _store.SaveLocalCalendarEventRevisionAsync(
                new LocalCalendarEventRevisionInput(
                    _editingCalendarEventId,
                    title,
                    start,
                    end,
                    allDay,
                    CalendarLocationBox.Text ?? string.Empty,
                    CalendarNotesBox.Text ?? string.Empty,
                    CalendarEventSourceKind.LocalManual));
            await LoadContactsAndCalendarAsync();
            ApplyCalendarFilter(SearchBox.Text);
            CalendarList.SelectedItem = _calendarEvents.FirstOrDefault(calendarEvent =>
                string.Equals(calendarEvent.EventId, saved.Event.EventId, StringComparison.Ordinal));
            ShowStatus("Calendar event saved locally.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Calendar event was not saved: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void DeleteCalendarEventButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || string.IsNullOrWhiteSpace(_editingCalendarEventId)) return;
        try
        {
            await _store.TombstoneLocalCalendarEventAsync(_editingCalendarEventId);
            await LoadContactsAndCalendarAsync();
            ApplyCalendarFilter(SearchBox.Text);
            CalendarList.SelectedItem = null;
            ResetCalendarEditor();
            ShowStatus("Calendar event removed from the active agenda.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Calendar event was not removed: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ResetCalendarEditor()
    {
        _editingCalendarEventId = null;
        CalendarEditorTitle.Text = "New event";
        CalendarEditorHint.Text = "Local events stay on this computer.";
        CalendarTitleBox.Text = string.Empty;
        CalendarLocationBox.Text = string.Empty;
        CalendarNotesBox.Text = string.Empty;
        CalendarAllDayCheckBox.IsChecked = false;
        var now = DateTime.Now;
        var start = new DateTime(
            now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
        var end = start.AddHours(1);
        CalendarStartDatePicker.Date = ToDatePickerDate(start);
        CalendarStartTimePicker.Time = start.TimeOfDay;
        CalendarEndDatePicker.Date = ToDatePickerDate(end);
        CalendarEndTimePicker.Time = end.TimeOfDay;
        SaveCalendarEventButton.IsEnabled = true;
        DeleteCalendarEventButton.IsEnabled = false;
        SetCalendarTimeControls();
    }

    private void SetCalendarTimeControls()
    {
        var enabled = CalendarAllDayCheckBox.IsChecked is not true;
        CalendarStartTimePicker.IsEnabled = enabled;
        CalendarEndTimePicker.IsEnabled = enabled;
    }

    private static DateTimeOffset ToDatePickerDate(DateTime value)
    {
        var unspecified = ToUnspecifiedDate(value);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    private static DateTime ToUnspecifiedDate(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private void ComposeButton_Click(object sender, RoutedEventArgs e) => OpenComposer();

    private void ReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null)
        {
            ShowStatus("Select a message before replying.", InfoBarSeverity.Warning);
            return;
        }
        OpenComposer();
        ComposeHeading.Text = $"Reply to {_selectedMessage.Sender}";
        ComposeToBox.Text = ExtractMailboxOrFallback(_selectedMessage.Sender);
        ComposeSubjectBox.Text = _selectedMessage.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? _selectedMessage.Subject
            : $"Re: {_selectedMessage.Subject}";
        ComposeBodyBox.Text = $"\r\n\r\nOn {_selectedMessage.Timestamp.LocalDateTime:g}, {_selectedMessage.Sender} wrote:\r\n> " +
                              _selectedMessage.Body.Replace("\n", "\n> ", StringComparison.Ordinal);
    }

    private void OpenComposer()
    {
        _activeDraftId = null;
        ComposeHeading.Text = "New message";
        ComposeToBox.Text = string.Empty;
        ComposeCcBox.Text = string.Empty;
        ComposeSubjectBox.Text = string.Empty;
        ComposeBodyBox.Text = string.Empty;
        AiDraftConsentCheckBox.IsChecked = false;
        ComposeLayer.Visibility = Visibility.Visible;
        ComposeToBox.Focus(FocusState.Programmatic);
    }

    private void CloseComposeButton_Click(object sender, RoutedEventArgs e) =>
        ComposeLayer.Visibility = Visibility.Collapsed;

    private async void SaveDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            ShowStatus("Local storage is unavailable; the draft was not saved.", InfoBarSeverity.Error);
            return;
        }
        try
        {
            var message = BuildDraftMime(requireRecipient: false);
            var rawMime = await SerializeAsync(message);
            var stored = await _store.SaveDraftRevisionAsync(new DraftRevisionImport(
                _activeDraftId,
                rawMime,
                message.Subject ?? string.Empty,
                message.From.ToString(),
                string.Join(", ", message.To.Concat(message.Cc)),
                BoundedPreview(message.TextBody)));
            _activeDraftId = stored.DraftId;
            _drafts.Insert(0, new MailItem
            {
                Id = stored.DraftId,
                Folder = "Drafts",
                Sender = stored.Sender,
                Recipients = stored.Recipients,
                Subject = string.IsNullOrWhiteSpace(stored.Subject) ? "(No subject)" : stored.Subject,
                Preview = stored.BodySnippet,
                Body = message.TextBody ?? string.Empty,
                Timestamp = stored.SavedUtc,
            });
            ComposeLayer.Visibility = Visibility.Collapsed;
            ShowStatus("Draft saved locally.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Draft could not be saved: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void StageOutboxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            ShowStatus("Local storage is unavailable; nothing was staged.", InfoBarSeverity.Error);
            return;
        }
        try
        {
            var message = BuildDraftMime(requireRecipient: true);
            var rawMime = await SerializeAsync(message);
            var operation = await _store.QueueOutboundMessageAsync(
                MailAccountProfilePolicy.LocalOutboxStagingAccountId,
                Guid.NewGuid().ToString("N"),
                message.MessageId!,
                rawMime);
            _outbox.Insert(0, new MailItem
            {
                Id = operation.OperationId,
                Folder = "Outbox",
                Sender = message.From.ToString(),
                Recipients = string.Join(", ", message.To.Concat(message.Cc)),
                Subject = string.IsNullOrWhiteSpace(message.Subject) ? "(No subject)" : message.Subject,
                Preview = BoundedPreview(message.TextBody),
                Body = message.TextBody ?? string.Empty,
                Timestamp = DateTimeOffset.Now,
            });
            ComposeLayer.Visibility = Visibility.Collapsed;
            SelectFolder("Outbox");
            ShowStatus("Message staged in Outbox. Demo mode did not contact an SMTP server.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Message was not staged: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private MimeMessage BuildDraftMime(bool requireRecipient)
    {
        var senderText = string.IsNullOrWhiteSpace(_settings.SenderAddress)
            ? "demo@novaemail.local"
            : _settings.SenderAddress;
        var message = new MimeMessage
        {
            MessageId = MimeUtils.GenerateMessageId("novaemail.local"),
            Subject = ComposeSubjectBox.Text ?? string.Empty,
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain") { Text = ComposeBodyBox.Text ?? string.Empty },
        };
        message.From.Add(MailboxAddress.Parse(senderText));
        AddAddresses(message.To, ComposeToBox.Text);
        AddAddresses(message.Cc, ComposeCcBox.Text);
        if (requireRecipient && message.To.Count + message.Cc.Count == 0)
            throw new InvalidDataException("Add at least one recipient before staging the message.");
        return message;
    }

    private static void AddAddresses(InternetAddressList target, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var parsed = InternetAddressList.Parse(text.Replace(';', ','));
        target.AddRange(parsed);
    }

    private static async Task<byte[]> SerializeAsync(MimeMessage message)
    {
        await using var output = new MemoryStream();
        await message.WriteToAsync(output);
        return output.ToArray();
    }

    private async void SaveAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            ShowStatus("Local storage is unavailable.", InfoBarSeverity.Error);
            return;
        }
        try
        {
            var senderAddress = MailboxAddress.Parse(EmailAddressBox.Text).Address;
            var accountId = BuildAccountId(senderAddress);
            var userName = AccountUserBox.Text.Trim();
            var profile = await _store.SaveMailAccountProfileRevisionAsync(new MailAccountProfileInput(
                accountId,
                string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? senderAddress : DisplayNameBox.Text.Trim(),
                "Imap",
                ImapHostBox.Text.Trim(),
                ToPort(ImapPortBox.Value, "IMAP"),
                SelectedTls(ImapTlsBox),
                SmtpHostBox.Text.Trim(),
                ToPort(SmtpPortBox.Value, "SMTP"),
                SelectedTls(SmtpTlsBox),
                userName,
                senderAddress));
            if (!string.IsNullOrWhiteSpace(AccountPasswordBox.Password))
                _mailVault.Store(profile.AccountId, new StoredMailCredential(userName, AccountPasswordBox.Password));
            _settings = _settings with { SenderAddress = senderAddress };
            await ClientSettingsStore.SaveAsync(_settings);
            AccountPasswordBox.Password = string.Empty;
            ShowStatus("Mail account settings saved. Credentials are in Windows Credential Manager.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"Account settings were not saved: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void TestImapButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var senderAddress = MailboxAddress.Parse(EmailAddressBox.Text).Address;
            var accountId = BuildAccountId(senderAddress);
            var stored = _mailVault.Read(accountId);
            var password = string.IsNullOrWhiteSpace(AccountPasswordBox.Password)
                ? stored?.Password
                : AccountPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("Enter or save the account password first.");
            SyncButton.IsEnabled = false;
            ShowStatus("Testing the configured IMAP connection…");
            var transport = new SecureMailTransport(new EndpointPolicy(), new CertificateTrustPolicy());
            var messages = await transport.InspectImapInboxAsync(
                new SecureMailEndpoint(
                    ImapHostBox.Text.Trim(),
                    ToPort(ImapPortBox.Value, "IMAP"),
                    ParseTls(SelectedTls(ImapTlsBox))),
                new MailCredentials(AccountUserBox.Text.Trim(), password),
                maximumCount: 5);
            ShowStatus($"IMAP connection succeeded. The server returned {messages.Count} recent message header(s).",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"IMAP test failed safely: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private async void SaveAiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = new EmailIntelligenceProfile(
                new Uri(AiEndpointBox.Text.Trim(), UriKind.Absolute),
                AiModelBox.Text.Trim(),
                Enabled: true);
            profile.Validate();
            if (!string.IsNullOrWhiteSpace(AiApiKeyBox.Password))
                _secretVault.Store("ai-api-key", new StoredSecret(AiApiKeyBox.Password));
            _settings = _settings with
            {
                AiEndpoint = profile.Endpoint.AbsoluteUri,
                AiModel = profile.Model,
            };
            await ClientSettingsStore.SaveAsync(_settings);
            AiApiKeyBox.Password = string.Empty;
            ShowStatus("AI settings saved. Message content still requires consent for every request.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"AI settings were not saved: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void SummarizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null)
        {
            ShowStatus("Select a message to summarize.", InfoBarSeverity.Warning);
            return;
        }
        if (AiMessageConsentCheckBox.IsChecked is not true)
        {
            ShowStatus("Check the AI consent box for this request first.", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var coordinator = CreateAiCoordinator(out var transport);
            using (transport)
            {
                var result = await coordinator.SummarizeAsync(
                    EnabledAiProfile(),
                    ToAiSource(_selectedMessage),
                    new EmailIntelligenceConsent(
                        _selectedMessage.Id,
                        EmailIntelligenceOperation.Summarize,
                        now,
                        AllowMessageContentProcessing: true),
                    now);
                AiResultText.Text = result.ActionItems.Count == 0
                    ? result.Summary
                    : result.Summary + "\n\nAction items:\n• " + string.Join("\n• ", result.ActionItems);
                AiResultText.Visibility = Visibility.Visible;
            }
            AiMessageConsentCheckBox.IsChecked = false;
            ShowStatus("AI summary completed. Consent was consumed for this request only.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"AI summary failed safely: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void PolishDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (AiDraftConsentCheckBox.IsChecked is not true)
        {
            ShowStatus("Check the AI consent box for this request first.", InfoBarSeverity.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(ComposeBodyBox.Text))
        {
            ShowStatus("Write a draft before asking AI to polish it.", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var identity = _selectedMessage?.Id ?? $"draft-{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            var coordinator = CreateAiCoordinator(out var transport);
            using (transport)
            {
                ComposeBodyBox.Text = await coordinator.ReviseDraftAsync(
                    EnabledAiProfile(),
                    new EmailDraftRevisionSource(
                        identity,
                        ComposeSubjectBox.Text,
                        ComposeBodyBox.Text,
                        _selectedMessage is null ? null : ToAiSource(_selectedMessage)),
                    new EmailIntelligenceConsent(
                        identity,
                        EmailIntelligenceOperation.ReviseDraft,
                        now,
                        AllowMessageContentProcessing: true),
                    now);
            }
            AiDraftConsentCheckBox.IsChecked = false;
            ShowStatus("AI revision inserted for review. Nothing was sent.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus($"AI revision failed safely: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private EmailIntelligenceCoordinator CreateAiCoordinator(
        out OpenWebUiEmailIntelligenceTransport transport)
    {
        var secret = _secretVault.Read("ai-api-key") ??
            throw new InvalidOperationException("Save an AI API key in Settings first.");
        transport = new OpenWebUiEmailIntelligenceTransport(
            new AssistantEndpointPolicy(), secret.Secret);
        return new EmailIntelligenceCoordinator(transport);
    }

    private EmailIntelligenceProfile EnabledAiProfile() =>
        new(new Uri(_settings.AiEndpoint, UriKind.Absolute), _settings.AiModel, Enabled: true);

    private static EmailIntelligenceSource ToAiSource(MailItem item) =>
        new(item.Id, item.Subject, item.Sender, item.Timestamp, item.Body);

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            ShowStatus("Local storage is unavailable.", InfoBarSeverity.Error);
            return;
        }
        try
        {
            var profiles = await _store.ReadLatestMailAccountProfilesAsync();
            var senderAddress = MailboxAddress.Parse(EmailAddressBox.Text).Address;
            var accountId = BuildAccountId(senderAddress);
            var profile = profiles.FirstOrDefault(candidate =>
                candidate.AccountId.Equals(accountId, StringComparison.Ordinal));
            if (profile is null)
            {
                SelectFolder("Settings");
                throw new InvalidOperationException("Save an IMAP account in Settings first.");
            }
            if (!profile.ReceiveProtocol.Equals("Imap", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configured account is not an IMAP account.");
            var storedCredential = _mailVault.Read(profile.AccountId) ??
                throw new InvalidOperationException("Save the account password in Settings first.");

            SyncButton.IsEnabled = false;
            ShowStatus("Discovering folders and synchronizing mail over read-only IMAP…");
            var coordinator = new ImapAccountSyncCoordinator(
                _store,
                new SecureMailEndpoint(
                    profile.ReceiveHost,
                    profile.ReceivePort,
                    ParseTls(profile.ReceiveTlsMode)),
                new MailCredentials(storedCredential.UserName, storedCredential.Password),
                new EndpointPolicy(),
                new CertificateTrustPolicy());
            var result = await coordinator.SynchronizeAsync(profile.AccountId);
            await LoadSynchronizedInboxAsync(result.InboxFolderId);
            SelectFolder("Inbox");
            MessageList.SelectedIndex = MessageList.Items.Count == 0 ? -1 : 0;
            var conflictSuffix = result.Conflicts.Count == 0
                ? string.Empty
                : $" {result.Conflicts.Count} conflict(s) were retained for review.";
            ShowStatus(
                $"Synchronized {result.Folders.Count} folder(s): {result.Imported} new, " +
                $"{result.Reconciled} reconciled, {result.Tombstoned} removed.{conflictSuffix}",
                result.Conflicts.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowStatus($"IMAP synchronization failed safely: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void SelectFolder(string folder)
    {
        foreach (var candidate in FolderNavigation.Items.OfType<ListViewItem>())
        {
            if (string.Equals(candidate.Tag as string, folder, StringComparison.Ordinal))
            {
                FolderNavigation.SelectedItem = candidate;
                return;
            }
        }
    }

    private static string SelectedTls(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ImplicitTls";

    private static void SelectTls(ComboBox comboBox, string mode)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, mode, StringComparison.Ordinal)) ??
            comboBox.Items[0];
    }

    private static TlsConnectionMode ParseTls(string mode) => mode switch
    {
        "ImplicitTls" => TlsConnectionMode.ImplicitTls,
        "RequiredStartTls" => TlsConnectionMode.RequiredStartTls,
        _ => throw new InvalidDataException("Unsupported TLS mode."),
    };

    private static int ToPort(double value, string label)
    {
        if (double.IsNaN(value) || value is < 1 or > 65_535 || value != Math.Truncate(value))
            throw new InvalidDataException($"{label} port must be a whole number from 1 to 65535.");
        return checked((int)value);
    }

    private static string BoundedPreview(string? body)
    {
        var normalized = (body ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..237] + "…";
    }

    private static string ExtractMailboxOrFallback(string sender)
    {
        try
        {
            return MailboxAddress.Parse(sender).Address;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
