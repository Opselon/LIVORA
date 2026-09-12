using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;
/// <summary>Base abstraction for any future connected service or device.</summary>
public interface IDataSource
{
    string Id { get; }
    SourceType SourceType { get; }
    ConnectionState State { get; }
}
