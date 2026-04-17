using MoltbookAgent.Models;
using Tomlyn;

namespace MoltbookAgent.Services;

/// <summary>
/// Manages reading and writing agent state to/from TOML file
/// </summary>
public class StateManager
{
    private readonly string _statePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StateManager(string statePath, ILogger logger)
    {
        _statePath = statePath;
        _logger = logger;
    }

    /// <summary>
    /// Load state from disk, or create default if not exists
    /// </summary>
    public async Task<AgentState> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_statePath))
            {
                _logger.LogInformation("State file not found, creating default state");
                return new AgentState();
            }

            var toml = await File.ReadAllTextAsync(_statePath);
            var state = Toml.ToModel<AgentState>(toml);

            _logger.LogDebug("State loaded from {Path}", _statePath);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load state, using default");
            return new AgentState();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Save state to disk
    /// </summary>
    public async Task SaveAsync(AgentState state)
    {
        await _lock.WaitAsync();
        try
        {
            var toml = Toml.FromModel(state);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(_statePath, toml);
            _logger.LogDebug("State saved to {Path}", _statePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save state");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Update state atomically with a transform function
    /// </summary>
    public async Task UpdateAsync(Func<AgentState, Task> updateAction)
    {
        var state = await LoadAsync();
        await updateAction(state);
        await SaveAsync(state);
    }
}
