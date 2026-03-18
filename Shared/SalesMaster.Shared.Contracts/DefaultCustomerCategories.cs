using System;
using System.Collections.Generic;
using System.Linq;

namespace SalesMaster.Shared.Contracts;

public sealed record DefaultCustomerCategoryDefinition(Guid Id, string Name);

public static class DefaultCustomerCategories
{
    public static readonly DefaultCustomerCategoryDefinition Government = new(
        Guid.Parse("8d6e0f78-3c57-4d30-b9df-4f54efef1001"),
        "관공서");

    public static readonly DefaultCustomerCategoryDefinition School = new(
        Guid.Parse("8d6e0f78-3c57-4d30-b9df-4f54efef1002"),
        "학교");

    public static readonly DefaultCustomerCategoryDefinition Company = new(
        Guid.Parse("8d6e0f78-3c57-4d30-b9df-4f54efef1003"),
        "기업");

    public static readonly DefaultCustomerCategoryDefinition Individual = new(
        Guid.Parse("8d6e0f78-3c57-4d30-b9df-4f54efef1004"),
        "개인");

    public static IReadOnlyList<DefaultCustomerCategoryDefinition> All { get; } =
    [
        Government,
        School,
        Company,
        Individual
    ];

    public static bool TryGetByName(string? name, out DefaultCustomerCategoryDefinition definition)
    {
        var normalized = NormalizeName(name);
        definition = All.FirstOrDefault(item => NormalizeName(item.Name) == normalized)
            ?? default!;

        return definition is not null;
    }

    public static string NormalizeName(string? name)
        => (name ?? string.Empty).Trim();
}
