using MoltbookAgent.Models;
using Tomlyn;

namespace MoltbookAgent.Services;

/// <summary>
/// Manages reading and writing agent memories to/from TOML file
/// </summary>
public class MemoryManager
{
    private readonly string _memoriesPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MemoryManager(string memoriesPath, ILogger logger)
    {
        _memoriesPath = memoriesPath;
        _logger = logger;
    }

    /// <summary>
    /// Load memories from disk, or create empty list if not exists
    /// </summary>
    public async Task<List<Memory>> LoadMemoriesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_memoriesPath))
            {
                _logger.LogDebug("Memories file not found, returning empty list");
                return new List<Memory>();
            }

            var toml = await File.ReadAllTextAsync(_memoriesPath);
            var root = Toml.ToModel<MemoriesRoot>(toml);

            _logger.LogDebug("Loaded {Count} memories from {Path}", root.Memories.Count, _memoriesPath);
            return root.Memories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memories, returning empty list");
            return new List<Memory>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Save memories to disk
    /// </summary>
    public async Task SaveMemoriesAsync(List<Memory> memories)
    {
        await _lock.WaitAsync();
        try
        {
            var root = new MemoriesRoot { Memories = memories };
            var toml = Toml.FromModel(root);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(_memoriesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(_memoriesPath, toml);
            _logger.LogDebug("Saved {Count} memories to {Path}", memories.Count, _memoriesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save memories");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get N most recent memories, prioritizing High priority
    /// </summary>
    public async Task<List<Memory>> GetRecentMemoriesAsync(int limit)
    {
        var memories = await LoadMemoriesAsync();

        // Sort: High priority first, then by created date descending
        var sorted = memories
            .OrderByDescending(m => m.GetPriorityEnum())
            .ThenByDescending(m => m.GetCreatedAtTime() ?? DateTime.MinValue)
            .Take(limit)
            .ToList();

        return sorted;
    }

    /// <summary>
    /// Add a new memory
    /// </summary>
    public async Task<Memory> AddMemoryAsync(string content, string priority = "normal")
    {
        var memories = await LoadMemoriesAsync();

        var memory = new Memory
        {
            Id = Guid.NewGuid().ToString(),
            Content = content,
            Priority = priority.ToLowerInvariant(),
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        memories.Add(memory);
        await SaveMemoriesAsync(memories);

        _logger.LogInformation("Added memory {Id}: {Content}", memory.Id, content);
        return memory;
    }

    /// <summary>
    /// Remove a memory by ID
    /// </summary>
    public async Task<bool> RemoveMemoryAsync(string id)
    {
        var memories = await LoadMemoriesAsync();
        var initialCount = memories.Count;

        memories.RemoveAll(m => m.Id == id);

        if (memories.Count < initialCount)
        {
            await SaveMemoriesAsync(memories);
            _logger.LogInformation("Removed memory {Id}", id);
            return true;
        }

        _logger.LogWarning("Memory {Id} not found", id);
        return false;
    }
}
