using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.ViewModels;

internal readonly record struct CustomerInlineEditScopeIdentity(
    Guid SessionId,
    long SyncScopeEpoch,
    string TenantCode,
    string OfficeCode,
    string BusinessOfficeCode,
    string ScopeType,
    string BusinessDatabaseName);

internal readonly record struct CustomerInlineEditableFields(
    string BusinessNumber,
    string Phone,
    string Department,
    string ContactPerson,
    string Address,
    string Notes)
{
    internal static CustomerInlineEditableFields Capture(LocalCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return new CustomerInlineEditableFields(
            customer.BusinessNumber,
            customer.Phone,
            customer.Department,
            customer.ContactPerson,
            customer.Address,
            customer.Notes);
    }
}

[Flags]
internal enum CustomerInlineFieldMask
{
    None = 0,
    BusinessNumber = 1 << 0,
    Phone = 1 << 1,
    Department = 1 << 2,
    ContactPerson = 1 << 3,
    Address = 1 << 4,
    Notes = 1 << 5,
    All = BusinessNumber | Phone | Department | ContactPerson | Address | Notes
}

internal sealed record CustomerInlineEditPatch(
    Guid CustomerId,
    string Label,
    long BaseRevision,
    CustomerInlineEditScopeIdentity Scope,
    CustomerInlineEditableFields Baseline,
    CustomerInlineEditableFields Desired,
    CustomerInlineFieldMask ChangedFields);

internal sealed record CustomerInlineEditMergeResult(
    bool Succeeded,
    IReadOnlyList<string> ConflictingFields)
{
    internal static CustomerInlineEditMergeResult Success { get; } =
        new(true, Array.Empty<string>());
}

internal static class CustomerInlineEditPatchMerge
{
    internal static void Overlay(
        LocalCustomer freshCustomer,
        CustomerInlineEditableFields fields)
    {
        ArgumentNullException.ThrowIfNull(freshCustomer);

        freshCustomer.BusinessNumber = fields.BusinessNumber;
        freshCustomer.Phone = fields.Phone;
        freshCustomer.Department = fields.Department;
        freshCustomer.ContactPerson = fields.ContactPerson;
        freshCustomer.Address = fields.Address;
        freshCustomer.Notes = fields.Notes;
    }

    internal static void OverlayChangedFields(
        LocalCustomer freshCustomer,
        CustomerInlineEditableFields fields,
        CustomerInlineFieldMask changedFields)
    {
        ArgumentNullException.ThrowIfNull(freshCustomer);

        if (Includes(changedFields, CustomerInlineFieldMask.BusinessNumber))
            freshCustomer.BusinessNumber = fields.BusinessNumber;
        if (Includes(changedFields, CustomerInlineFieldMask.Phone))
            freshCustomer.Phone = fields.Phone;
        if (Includes(changedFields, CustomerInlineFieldMask.Department))
            freshCustomer.Department = fields.Department;
        if (Includes(changedFields, CustomerInlineFieldMask.ContactPerson))
            freshCustomer.ContactPerson = fields.ContactPerson;
        if (Includes(changedFields, CustomerInlineFieldMask.Address))
            freshCustomer.Address = fields.Address;
        if (Includes(changedFields, CustomerInlineFieldMask.Notes))
            freshCustomer.Notes = fields.Notes;
    }

    internal static CustomerInlineEditPatch RebaseAfterSupersededSave(
        CustomerInlineEditPatch pending,
        LocalCustomer savedCustomer)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(savedCustomer);
        if (pending.CustomerId != savedCustomer.Id)
        {
            throw new ArgumentException(
                "The saved customer does not match the pending patch customer.",
                nameof(savedCustomer));
        }

        var saved = CustomerInlineEditableFields.Capture(savedCustomer);
        var remainingChanges = CustomerInlineFieldMask.None;
        remainingChanges = RetainUncommittedField(
            remainingChanges,
            pending.ChangedFields,
            CustomerInlineFieldMask.BusinessNumber,
            pending.Desired.BusinessNumber,
            saved.BusinessNumber);
        remainingChanges = RetainUncommittedField(
            remainingChanges,
            pending.ChangedFields,
            CustomerInlineFieldMask.Phone,
            pending.Desired.Phone,
            saved.Phone);
        remainingChanges = RetainUncommittedField(
            remainingChanges,
            pending.ChangedFields,
            CustomerInlineFieldMask.Department,
            pending.Desired.Department,
            saved.Department);
        remainingChanges = RetainUncommittedField(
            remainingChanges,
            pending.ChangedFields,
            CustomerInlineFieldMask.ContactPerson,
            pending.Desired.ContactPerson,
            saved.ContactPerson);
        remainingChanges = RetainUncommittedField(
            remainingChanges,
            pending.ChangedFields,
            CustomerInlineFieldMask.Address,
            pending.Desired.Address,
            saved.Address);
        remainingChanges = RetainUncommittedField(
            remainingChanges,
            pending.ChangedFields,
            CustomerInlineFieldMask.Notes,
            pending.Desired.Notes,
            saved.Notes);
        var desired = new CustomerInlineEditableFields(
            Includes(remainingChanges, CustomerInlineFieldMask.BusinessNumber)
                ? pending.Desired.BusinessNumber
                : saved.BusinessNumber,
            Includes(remainingChanges, CustomerInlineFieldMask.Phone)
                ? pending.Desired.Phone
                : saved.Phone,
            Includes(remainingChanges, CustomerInlineFieldMask.Department)
                ? pending.Desired.Department
                : saved.Department,
            Includes(remainingChanges, CustomerInlineFieldMask.ContactPerson)
                ? pending.Desired.ContactPerson
                : saved.ContactPerson,
            Includes(remainingChanges, CustomerInlineFieldMask.Address)
                ? pending.Desired.Address
                : saved.Address,
            Includes(remainingChanges, CustomerInlineFieldMask.Notes)
                ? pending.Desired.Notes
                : saved.Notes);

        return pending with
        {
            BaseRevision = savedCustomer.Revision,
            Baseline = saved,
            Desired = desired,
            ChangedFields = remainingChanges
        };
    }

    internal static CustomerInlineEditMergeResult TryMerge(
        LocalCustomer freshCurrent,
        CustomerInlineEditPatch patch)
    {
        ArgumentNullException.ThrowIfNull(freshCurrent);
        ArgumentNullException.ThrowIfNull(patch);

        if (freshCurrent.Id != patch.CustomerId)
        {
            throw new ArgumentException(
                "The current customer does not match the patch customer.",
                nameof(freshCurrent));
        }

        var current = CustomerInlineEditableFields.Capture(freshCurrent);
        var conflicts = new List<string>(capacity: 6);

        var businessNumber = MergeField(
            nameof(LocalCustomer.BusinessNumber),
            Includes(patch.ChangedFields, CustomerInlineFieldMask.BusinessNumber),
            patch.Baseline.BusinessNumber,
            patch.Desired.BusinessNumber,
            current.BusinessNumber,
            conflicts);
        var phone = MergeField(
            nameof(LocalCustomer.Phone),
            Includes(patch.ChangedFields, CustomerInlineFieldMask.Phone),
            patch.Baseline.Phone,
            patch.Desired.Phone,
            current.Phone,
            conflicts);
        var department = MergeField(
            nameof(LocalCustomer.Department),
            Includes(patch.ChangedFields, CustomerInlineFieldMask.Department),
            patch.Baseline.Department,
            patch.Desired.Department,
            current.Department,
            conflicts);
        var contactPerson = MergeField(
            nameof(LocalCustomer.ContactPerson),
            Includes(patch.ChangedFields, CustomerInlineFieldMask.ContactPerson),
            patch.Baseline.ContactPerson,
            patch.Desired.ContactPerson,
            current.ContactPerson,
            conflicts);
        var address = MergeField(
            nameof(LocalCustomer.Address),
            Includes(patch.ChangedFields, CustomerInlineFieldMask.Address),
            patch.Baseline.Address,
            patch.Desired.Address,
            current.Address,
            conflicts);
        var notes = MergeField(
            nameof(LocalCustomer.Notes),
            Includes(patch.ChangedFields, CustomerInlineFieldMask.Notes),
            patch.Baseline.Notes,
            patch.Desired.Notes,
            current.Notes,
            conflicts);

        if (conflicts.Count != 0)
            return new CustomerInlineEditMergeResult(false, conflicts.ToArray());

        Overlay(
            freshCurrent,
            new CustomerInlineEditableFields(
                businessNumber,
                phone,
                department,
                contactPerson,
                address,
                notes));

        return CustomerInlineEditMergeResult.Success;
    }

    private static string MergeField(
        string fieldName,
        bool changed,
        string baseline,
        string desired,
        string current,
        ICollection<string> conflicts)
    {
        if (!changed)
            return current;

        if (string.Equals(current, baseline, StringComparison.Ordinal) ||
            string.Equals(current, desired, StringComparison.Ordinal))
        {
            return desired;
        }

        conflicts.Add(fieldName);
        return current;
    }

    private static bool Includes(
        CustomerInlineFieldMask changedFields,
        CustomerInlineFieldMask field)
        => (changedFields & field) != 0;

    private static CustomerInlineFieldMask RetainUncommittedField(
        CustomerInlineFieldMask result,
        CustomerInlineFieldMask pendingChanges,
        CustomerInlineFieldMask field,
        string desired,
        string saved)
        => Includes(pendingChanges, field) &&
           !string.Equals(desired, saved, StringComparison.Ordinal)
            ? result | field
            : result;
}
