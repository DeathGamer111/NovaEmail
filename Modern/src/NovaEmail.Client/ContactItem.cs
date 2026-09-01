using NovaEmail.Storage;

namespace NovaEmail.Client;

public sealed class ContactItem
{
    public required string ContactId { get; init; }
    public required string DisplayName { get; init; }
    public required string EmailAddress { get; init; }
    public required string Notes { get; init; }
    public required int Revision { get; init; }
    public required bool IsGroup { get; init; }

    public string Initial => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : DisplayName[..1].ToUpperInvariant();

    public string SecondaryText => IsGroup ? "Contact group" : EmailAddress;

    public static ContactItem FromStored(StoredContactRevision contact) => new()
    {
        ContactId = contact.ContactId,
        DisplayName = contact.DisplayName,
        EmailAddress = contact.EmailAddress,
        Notes = contact.Notes,
        Revision = contact.Revision,
        IsGroup = contact.IsGroup,
    };
}
