using MimeKit;
using NovaEmail.Storage;

namespace NovaEmail.Mail.Tests;

public sealed class ContactRecipientResolverTests
{
    [Fact]
    public void NestedGroupsResolveInOrderAndDeduplicateMailboxAddresses()
    {
        var ada = Contact(1, "Ada", "ada@example.test");
        var grace = Contact(2, "Grace", "Grace Hopper <grace@example.test>");
        var nested = Group(3, "Nested team",
            Member(0, ContactGroupMemberKind.EmailAddress, "direct@example.test"),
            Member(1, ContactGroupMemberKind.FriendlyName, "Ada"));
        var root = Group(4, "Root team",
            Member(0, ContactGroupMemberKind.FriendlyName, "Grace"),
            Member(1, ContactGroupMemberKind.NestedGroup, "Nested team"),
            Member(2, ContactGroupMemberKind.EmailAddress, "ADA@example.test"));

        var result = ContactRecipientResolver.Resolve([ada, grace, nested, root], [root.ContactId]);

        Assert.Equal(1, result.SelectedContactCount);
        Assert.Equal(2, result.ExpandedGroupCount);
        Assert.Equal(
            ["grace@example.test", "direct@example.test", "ada@example.test"],
            result.Recipients.Select(mailbox => mailbox.Address));
        Assert.Equal("Grace Hopper", result.Recipients[0].Name);
        Assert.Equal("Ada", result.Recipients[2].Name);
    }

    [Fact]
    public void CyclicNestedGroupsFailClosedWithoutReturningPartialRecipients()
    {
        var first = Group(10, "First",
            Member(0, ContactGroupMemberKind.EmailAddress, "before-cycle@example.test"),
            Member(1, ContactGroupMemberKind.NestedGroup, "Second"));
        var second = Group(11, "Second",
            Member(0, ContactGroupMemberKind.NestedGroup, "First"));

        var error = Assert.Throws<InvalidDataException>(() =>
            ContactRecipientResolver.Resolve([first, second], [first.ContactId]));
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousFriendlyNameAndDeletedSelectionsAreRejected()
    {
        var first = Contact(20, "Duplicate", "first@example.test");
        var second = Contact(21, "Duplicate", "second@example.test");
        var group = Group(22, "Ambiguous group",
            Member(0, ContactGroupMemberKind.FriendlyName, "Duplicate"));
        var ambiguous = Assert.Throws<InvalidDataException>(() =>
            ContactRecipientResolver.Resolve([first, second, group], [group.ContactId]));
        Assert.Contains("ambiguous", ambiguous.Message, StringComparison.OrdinalIgnoreCase);

        var deleted = Contact(23, "Deleted", "deleted@example.test", tombstoned: true);
        var stale = Assert.Throws<InvalidDataException>(() =>
            ContactRecipientResolver.Resolve([deleted], [deleted.ContactId]));
        Assert.Contains("missing, deleted, or stale", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpansionAndMailboxParsingAreBoundedBeforeDraftSerialization()
    {
        var oversized = Group(30, "Large group",
            Enumerable.Range(0, 101)
                .Select(index => Member(
                    index, ContactGroupMemberKind.EmailAddress, $"person{index}@example.test"))
                .ToArray());
        var expansion = Assert.Throws<InvalidDataException>(() =>
            ContactRecipientResolver.Resolve([oversized], [oversized.ContactId]));
        Assert.Contains("100-recipient", expansion.Message, StringComparison.OrdinalIgnoreCase);

        var invalid = Contact(31, "Invalid", "not a mailbox");
        var parse = Assert.Throws<InvalidDataException>(() =>
            ContactRecipientResolver.Resolve([invalid], [invalid.ContactId]));
        Assert.Contains("invalid mailbox", parse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvedContactsStayInTheExplicitlyChosenBccField()
    {
        var hidden = Contact(40, "Hidden recipient", "hidden@example.test");
        var resolution = ContactRecipientResolver.Resolve([hidden], [hidden.ContactId]);
        var message = OutboundMessageComposer.Compose(new OutboundDraft(
            "contact-bcc-boundary",
            MailboxAddress.Parse("sender@example.test"),
            [], [], resolution.Recipients,
            "Bcc boundary", "Body", null, []), DateTimeOffset.UtcNow);

        Assert.Empty(message.To);
        Assert.Empty(message.Cc);
        Assert.Equal("hidden@example.test", Assert.Single(message.Bcc.Mailboxes).Address);
    }

    private static StoredContactRevision Contact(
        int id,
        string name,
        string address,
        bool tombstoned = false) =>
        new(id.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
            1, name, address, string.Empty, false, tombstoned,
            DateTimeOffset.UtcNow, []);

    private static StoredContactRevision Group(
        int id,
        string name,
        params StoredContactGroupMember[] members) =>
        new(id.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
            1, name, string.Empty, string.Empty, true, false,
            DateTimeOffset.UtcNow, members);

    private static StoredContactGroupMember Member(
        int ordinal,
        ContactGroupMemberKind kind,
        string value) => new(ordinal, kind, value);
}
