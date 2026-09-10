using MimeKit;
using NovaEmail.Storage;

namespace NovaEmail.Mail;

public sealed record ContactRecipientResolution(
    IReadOnlyList<MailboxAddress> Recipients,
    int SelectedContactCount,
    int ExpandedGroupCount);

public static class ContactRecipientResolver
{
    private const int MaximumSelectedContacts = 100;
    private const int MaximumResolvedRecipients = 100;
    private const int MaximumGroupDepth = 32;
    private const int MaximumExpansionSteps = 10_000;
    private const int MaximumAvailableContacts = 100_000;

    public static ContactRecipientResolution Resolve(
        IReadOnlyList<StoredContactRevision> availableContacts,
        IReadOnlyList<string> selectedContactIds)
    {
        ArgumentNullException.ThrowIfNull(availableContacts);
        ArgumentNullException.ThrowIfNull(selectedContactIds);
        if (availableContacts.Count > MaximumAvailableContacts)
            throw new InvalidDataException("Contact book exceeds the recipient-resolution limit.");
        if (selectedContactIds.Count is 0 or > MaximumSelectedContacts)
            throw new InvalidDataException("Select between one and 100 contact entries.");

        var byId = new Dictionary<string, StoredContactRevision>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<StoredContactRevision>>(StringComparer.OrdinalIgnoreCase);
        foreach (var contact in availableContacts)
        {
            if (contact.IsTombstoned) continue;
            if (!byId.TryAdd(contact.ContactId, contact))
                throw new InvalidDataException("The active contact book repeats a contact identifier.");
            var name = contact.DisplayName.Trim();
            if (name.Length == 0) continue;
            if (!byName.TryGetValue(name, out var matches))
            {
                matches = [];
                byName.Add(name, matches);
            }
            matches.Add(contact);
        }

        var selectedIds = selectedContactIds
            .Select(value => value?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("A selected contact identifier is empty.");
        var recipients = new List<MailboxAddress>();
        var recipientAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansionSteps = 0;
        var expandedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMailbox(string value, string? preferredName)
        {
            if (!MailboxAddress.TryParse(value, out var parsed))
                throw new InvalidDataException("A selected contact resolves to an invalid mailbox address.");
            if (!recipientAddresses.Add(parsed.Address)) return;
            recipients.Add(string.IsNullOrWhiteSpace(parsed.Name) && !string.IsNullOrWhiteSpace(preferredName)
                ? new MailboxAddress(preferredName, parsed.Address)
                : parsed);
            if (recipients.Count > MaximumResolvedRecipients)
                throw new InvalidDataException("Contact selection expands beyond the 100-recipient limit.");
        }

        StoredContactRevision ResolveNamed(string value, bool requireGroup)
        {
            var name = value.Trim();
            if (!byName.TryGetValue(name, out var matches))
                throw new InvalidDataException(
                    requireGroup
                        ? "A nested contact group could not be resolved by name."
                        : "A friendly-name contact could not be resolved by name.");
            var typed = matches.Where(contact => contact.IsGroup == requireGroup).ToArray();
            if (typed.Length != 1)
                throw new InvalidDataException(
                    requireGroup
                        ? "A nested contact group name is ambiguous."
                        : "A friendly-name contact is ambiguous.");
            return typed[0];
        }

        void ResolveContact(StoredContactRevision contact, int depth)
        {
            expansionSteps = checked(expansionSteps + 1);
            if (expansionSteps > MaximumExpansionSteps)
                throw new InvalidDataException("Contact group expansion exceeds its traversal limit.");
            if (depth > MaximumGroupDepth)
                throw new InvalidDataException("Contact group nesting exceeds 32 levels.");
            if (contact.IsTombstoned)
                throw new InvalidDataException("A selected contact was deleted before recipient resolution.");
            if (!contact.IsGroup)
            {
                if (string.IsNullOrWhiteSpace(contact.EmailAddress))
                    throw new InvalidDataException("A selected individual contact has no mailbox address.");
                AddMailbox(contact.EmailAddress, contact.DisplayName);
                return;
            }
            if (!expansionStack.Add(contact.ContactId))
                throw new InvalidDataException("Contact group expansion contains a cycle.");
            expandedGroups.Add(contact.ContactId);
            try
            {
                foreach (var member in contact.GroupMembers.OrderBy(member => member.Ordinal))
                {
                    switch (member.Kind)
                    {
                        case ContactGroupMemberKind.EmailAddress:
                            AddMailbox(member.Value, preferredName: null);
                            break;
                        case ContactGroupMemberKind.FriendlyName:
                            ResolveContact(ResolveNamed(member.Value, requireGroup: false), checked(depth + 1));
                            break;
                        case ContactGroupMemberKind.NestedGroup:
                            ResolveContact(ResolveNamed(member.Value, requireGroup: true), checked(depth + 1));
                            break;
                        default:
                            throw new InvalidDataException("Contact group contains an unsupported member kind.");
                    }
                }
            }
            finally
            {
                expansionStack.Remove(contact.ContactId);
            }
        }

        foreach (var selectedId in selectedIds)
        {
            if (!byId.TryGetValue(selectedId, out var contact))
                throw new InvalidDataException("A selected contact is missing, deleted, or stale.");
            ResolveContact(contact, depth: 0);
        }
        if (recipients.Count == 0)
            throw new InvalidDataException("Contact selection resolved to no mailbox recipients.");
        return new ContactRecipientResolution(recipients, selectedIds.Length, expandedGroups.Count);
    }
}
