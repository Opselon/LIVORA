namespace LIVORA.Application.Abstractions;
/// <summary>
/// Persistence abstraction. The UI never touches SQLite, JSON files or Preferences directly.
/// </summary>
public interface IRepository<T> where T : class
{
    Task<IReadOnlyList<T>> GetAllAsync();
    Task<T?> GetAsync(string id);
    Task SaveAsync(T item);
    Task DeleteAsync(string id);
    Task<bool> IsEmptyAsync();
}
