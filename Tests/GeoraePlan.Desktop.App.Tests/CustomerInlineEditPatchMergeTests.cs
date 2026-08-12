using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerInlineEditPatchMergeTests
{
    [Fact]
    public void Overlay_ChangesOnlySixEditableFields_AndPreservesFreshMetadata()
    {
        var customer = CreateCustomer();
        var expectedId = customer.Id;
        var expectedMasterId = customer.CustomerMasterId;
        var expectedRevision = customer.Revision;
        var expectedName = customer.NameOriginal;
        var expectedNameKey = customer.NameMatchKey;
        var expectedTenant = customer.TenantCode;
        var expectedOffice = customer.OfficeCode;
        var expectedResponsibleOffice = customer.ResponsibleOfficeCode;
        var expectedTradeType = customer.TradeType;
        var expectedEmail = customer.Email;
        var expectedCreatedAt = customer.CreatedAtUtc;
        var expectedUpdatedAt = customer.UpdatedAtUtc;
        var expectedDirty = customer.IsDirty;

        var desired = new CustomerInlineEditableFields(
            "new-business-number",
            "new-phone",
            "new-department",
            "new-contact",
            "new-address",
            "new-notes");

        CustomerInlineEditPatchMerge.Overlay(customer, desired);

        Assert.Equal(desired, CustomerInlineEditableFields.Capture(customer));
        Assert.Equal(expectedId, customer.Id);
        Assert.Equal(expectedMasterId, customer.CustomerMasterId);
        Assert.Equal(expectedRevision, customer.Revision);
        Assert.Equal(expectedName, customer.NameOriginal);
        Assert.Equal(expectedNameKey, customer.NameMatchKey);
        Assert.Equal(expectedTenant, customer.TenantCode);
        Assert.Equal(expectedOffice, customer.OfficeCode);
        Assert.Equal(expectedResponsibleOffice, customer.ResponsibleOfficeCode);
        Assert.Equal(expectedTradeType, customer.TradeType);
        Assert.Equal(expectedEmail, customer.Email);
        Assert.Equal(expectedCreatedAt, customer.CreatedAtUtc);
        Assert.Equal(expectedUpdatedAt, customer.UpdatedAtUtc);
        Assert.Equal(expectedDirty, customer.IsDirty);
    }

    [Fact]
    public void TryMerge_NonOverlappingChanges_PreservesRemoteAndAppliesLocalPatch()
    {
        var customerId = Guid.NewGuid();
        var baseline = new CustomerInlineEditableFields(
            "business-base",
            "phone-base",
            "department-base",
            "contact-base",
            "address-base",
            "notes-base");
        var desired = baseline with
        {
            BusinessNumber = "business-local",
            Address = "address-already-desired",
            Notes = "notes-local"
        };
        var patch = CreatePatch(
            customerId,
            baseline,
            desired,
            CustomerInlineFieldMask.BusinessNumber |
            CustomerInlineFieldMask.Address |
            CustomerInlineFieldMask.Notes);
        var current = CreateCustomer(customerId);
        current.Revision = 91;
        current.BusinessNumber = baseline.BusinessNumber;
        current.Phone = "phone-remote";
        current.Department = "department-remote";
        current.ContactPerson = baseline.ContactPerson;
        current.Address = desired.Address;
        current.Notes = baseline.Notes;
        var expectedName = current.NameOriginal;
        var expectedTenant = current.TenantCode;
        var expectedOffice = current.OfficeCode;

        var result = CustomerInlineEditPatchMerge.TryMerge(current, patch);

        Assert.True(result.Succeeded);
        Assert.Empty(result.ConflictingFields);
        Assert.Equal("business-local", current.BusinessNumber);
        Assert.Equal("phone-remote", current.Phone);
        Assert.Equal("department-remote", current.Department);
        Assert.Equal("contact-base", current.ContactPerson);
        Assert.Equal("address-already-desired", current.Address);
        Assert.Equal("notes-local", current.Notes);
        Assert.Equal(91, current.Revision);
        Assert.Equal(expectedName, current.NameOriginal);
        Assert.Equal(expectedTenant, current.TenantCode);
        Assert.Equal(expectedOffice, current.OfficeCode);
    }

    [Fact]
    public void TryMerge_SameFieldDiverged_ReportsConflictWithoutPartialMutation()
    {
        var customerId = Guid.NewGuid();
        var baseline = new CustomerInlineEditableFields(
            "business-base",
            "phone-base",
            "department-base",
            "contact-base",
            "address-base",
            "notes-base");
        var desired = baseline with
        {
            BusinessNumber = "business-local",
            Notes = "notes-local"
        };
        var patch = CreatePatch(
            customerId,
            baseline,
            desired,
            CustomerInlineFieldMask.BusinessNumber |
            CustomerInlineFieldMask.Notes);
        var current = CreateCustomer(customerId);
        current.Revision = 123;
        current.BusinessNumber = "business-remote";
        current.Phone = "phone-remote";
        current.Department = baseline.Department;
        current.ContactPerson = baseline.ContactPerson;
        current.Address = baseline.Address;
        current.Notes = baseline.Notes;
        var beforeFields = CustomerInlineEditableFields.Capture(current);
        var beforeRevision = current.Revision;
        var beforeName = current.NameOriginal;
        var beforeTenant = current.TenantCode;
        var beforeOffice = current.OfficeCode;

        var result = CustomerInlineEditPatchMerge.TryMerge(current, patch);

        Assert.False(result.Succeeded);
        Assert.Equal([nameof(LocalCustomer.BusinessNumber)], result.ConflictingFields);
        Assert.Equal(beforeFields, CustomerInlineEditableFields.Capture(current));
        Assert.Equal(beforeRevision, current.Revision);
        Assert.Equal(beforeName, current.NameOriginal);
        Assert.Equal(beforeTenant, current.TenantCode);
        Assert.Equal(beforeOffice, current.OfficeCode);
    }

    [Fact]
    public void OverlayChangedFields_PreservesFreshRemoteValuesForUntouchedFields()
    {
        var customer = CreateCustomer();
        customer.Phone = "phone-remote";
        customer.Department = "department-remote";
        var desired = CustomerInlineEditableFields.Capture(customer) with
        {
            BusinessNumber = "business-local",
            Phone = "phone-stale-ui",
            Notes = "notes-local"
        };

        CustomerInlineEditPatchMerge.OverlayChangedFields(
            customer,
            desired,
            CustomerInlineFieldMask.BusinessNumber |
            CustomerInlineFieldMask.Notes);

        Assert.Equal("business-local", customer.BusinessNumber);
        Assert.Equal("notes-local", customer.Notes);
        Assert.Equal("phone-remote", customer.Phone);
        Assert.Equal("department-remote", customer.Department);
    }

    [Fact]
    public void RebaseAfterSupersededSave_PreservesExplicitRevertAndAdoptsUntouchedRemoteValue()
    {
        var customerId = Guid.NewGuid();
        var baseline = new CustomerInlineEditableFields(
            "business-original",
            "phone-original",
            "department-original",
            "contact-original",
            "address-original",
            "notes-original");
        var pending = CreatePatch(
            customerId,
            baseline,
            baseline with
            {
                BusinessNumber = "business-original",
                Notes = "notes-latest"
            },
            CustomerInlineFieldMask.BusinessNumber |
            CustomerInlineFieldMask.Notes);
        var saved = CreateCustomer(customerId);
        saved.Revision = 88;
        saved.BusinessNumber = "business-earlier-save";
        saved.Phone = "phone-remote";
        saved.Department = "department-remote";
        saved.ContactPerson = baseline.ContactPerson;
        saved.Address = baseline.Address;
        saved.Notes = "notes-earlier-save";

        var rebased = CustomerInlineEditPatchMerge.RebaseAfterSupersededSave(
            pending,
            saved);

        Assert.Equal(88, rebased.BaseRevision);
        Assert.Equal(CustomerInlineEditableFields.Capture(saved), rebased.Baseline);
        Assert.Equal("business-original", rebased.Desired.BusinessNumber);
        Assert.Equal("notes-latest", rebased.Desired.Notes);
        Assert.Equal("phone-remote", rebased.Desired.Phone);
        Assert.Equal("department-remote", rebased.Desired.Department);

        var mergeTarget = CreateCustomer(customerId);
        CustomerInlineEditPatchMerge.Overlay(
            mergeTarget,
            CustomerInlineEditableFields.Capture(saved));
        mergeTarget.Revision = saved.Revision;
        var result = CustomerInlineEditPatchMerge.TryMerge(mergeTarget, rebased);

        Assert.True(result.Succeeded);
        Assert.Equal("business-original", mergeTarget.BusinessNumber);
        Assert.Equal("notes-latest", mergeTarget.Notes);
        Assert.Equal("phone-remote", mergeTarget.Phone);
    }

    [Fact]
    public void RebaseAfterSupersededSave_ClearsBitsAlreadyCommittedByEarlierAttempt()
    {
        var customerId = Guid.NewGuid();
        var baseline = new CustomerInlineEditableFields(
            "business-base",
            "phone-base",
            "department-base",
            "contact-base",
            "address-base",
            "notes-base");
        var pending = CreatePatch(
            customerId,
            baseline,
            baseline with
            {
                Phone = "phone-earlier-save",
                Notes = "notes-latest"
            },
            CustomerInlineFieldMask.Phone |
            CustomerInlineFieldMask.Notes);
        var saved = CreateCustomer(customerId);
        CustomerInlineEditPatchMerge.Overlay(saved, baseline with
        {
            Phone = "phone-earlier-save",
            Notes = "notes-earlier-save"
        });
        saved.Revision = 89;

        var rebased = CustomerInlineEditPatchMerge.RebaseAfterSupersededSave(
            pending,
            saved);

        Assert.Equal(CustomerInlineFieldMask.Notes, rebased.ChangedFields);
        Assert.Equal("phone-earlier-save", rebased.Desired.Phone);
        Assert.Equal("notes-latest", rebased.Desired.Notes);

        var mergeTarget = CreateCustomer(customerId);
        CustomerInlineEditPatchMerge.Overlay(
            mergeTarget,
            CustomerInlineEditableFields.Capture(saved) with
            {
                Phone = "phone-remote-after-earlier-save"
            });
        mergeTarget.Revision = 90;
        var result = CustomerInlineEditPatchMerge.TryMerge(mergeTarget, rebased);

        Assert.True(result.Succeeded);
        Assert.Equal("phone-remote-after-earlier-save", mergeTarget.Phone);
        Assert.Equal("notes-latest", mergeTarget.Notes);
    }

    [Fact]
    public void ScopeIdentity_SameValuesAfterAbaButDifferentEpoch_IsNotEqual()
    {
        var sessionId = Guid.NewGuid();
        var before = new CustomerInlineEditScopeIdentity(
            sessionId,
            SyncScopeEpoch: 7,
            TenantCode: "USENET",
            OfficeCode: "SEOUL",
            BusinessOfficeCode: "SEOUL",
            ScopeType: "OfficeOnly",
            BusinessDatabaseName: "georaeplan_usenet");
        var afterAba = before with { SyncScopeEpoch = 9 };

        Assert.NotEqual(before, afterAba);
        Assert.Equal(before, before with { });
    }

    private static CustomerInlineEditPatch CreatePatch(
        Guid customerId,
        CustomerInlineEditableFields baseline,
        CustomerInlineEditableFields desired,
        CustomerInlineFieldMask changedFields)
        => new(
            customerId,
            Label: "테스트 거래처",
            BaseRevision: 40,
            Scope: new CustomerInlineEditScopeIdentity(
                Guid.NewGuid(),
                SyncScopeEpoch: 3,
                TenantCode: "USENET",
                OfficeCode: "SEOUL",
                BusinessOfficeCode: "SEOUL",
                ScopeType: "OfficeOnly",
                BusinessDatabaseName: "georaeplan_usenet"),
            baseline,
            desired,
            changedFields);

    private static LocalCustomer CreateCustomer(Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            CustomerMasterId = Guid.NewGuid(),
            Revision = 77,
            IsDirty = false,
            IsDeleted = false,
            CreatedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            TenantCode = "TENANT-REMOTE",
            OfficeCode = "OFFICE-REMOTE",
            ResponsibleOfficeCode = "RESPONSIBLE-REMOTE",
            NameOriginal = "remote-name",
            NameMatchKey = "REMOTE-NAME",
            CategoryId = Guid.NewGuid(),
            TradeType = "remote-trade-type",
            Department = "department-base",
            ContactPerson = "contact-base",
            BusinessNumber = "business-base",
            Address = "address-base",
            DetailAddress = "remote-detail-address",
            Phone = "phone-base",
            MobilePhone = "remote-mobile",
            FaxNumber = "remote-fax",
            Email = "remote@example.test",
            HomePage = "https://remote.example.test",
            Representative = "remote-representative",
            BusinessType = "remote-business-type",
            BusinessItem = "remote-business-item",
            Recipient = "remote-recipient",
            PriceGrade = "remote-price-grade",
            Notes = "notes-base"
        };
}
