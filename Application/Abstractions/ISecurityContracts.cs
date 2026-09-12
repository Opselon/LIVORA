using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;
/// <summary>Platform permission abstraction — keeps platform permission code out of feature VMs.</summary>
public interface IPermissionService
{
    PermissionState GetStatus(AppPermission permission);
    Task<PermissionState> RequestAsync(AppPermission permission);
}

/// <summary>Privacy inventory: what LIVORA stores, where it came from, and delete controls.</summary>
public interface IPrivacyService
{
    Task<IReadOnlyList<StoredDataCategory>> DescribeStoredDataAsync();
    Task DeleteAllLocalDataAsync();
}

public sealed class StoredDataCategory
{
    public required string Key { get; init; }            // localization key
    public required DataOrigin Origin { get; init; }      // dominant origin (Mock/Manual today)
    public required string StorageLocationKey { get; init; }
    public string? LastUpdatedHint { get; init; }
}
