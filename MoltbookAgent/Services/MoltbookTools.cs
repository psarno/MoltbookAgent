using System.Text.Json;

namespace MoltbookAgent.Services;

/// <summary>
/// Wraps the Moltbook API and exposes all agent actions as AgentTool definitions
/// compatible with any ILlmClient provider.
/// </summary>
public class MoltbookTools
{
    private readonly MoltbookClient _client;
    private readonly ILogger _logger;
    private readonly bool _observationMode;
    private readonly StateManager? _stateManager;
    private readonly MemoryManager? _memoryManager;

    public MoltbookTools(
        MoltbookClient client, ILogger logger, bool observationMode = true,
        StateManager? stateManager = null, MemoryManager? memoryManager = null)
    {
        _client = client;
        _logger = logger;
        _observationMode = observationMode;
        _stateManager = stateManager;
        _memoryManager = memoryManager;
    }

    /// <summary>
    /// Returns all tool definitions for use with ILlmClient.RunAgentLoopAsync.
    /// Each entry bundles the tool schema with a handler that calls the
    /// corresponding implementation method below.
    /// </summary>
    public List<AgentTool> GetTools() =>
    [
        // ── Exploration tools ─────────────────────────────────────────────────

        new AgentTool
        {
            Name = "search",
            Description = "Search Moltbook posts and comments by semantic meaning. Use natural language queries to find relevant discussions.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string",  description = "Search query — use natural language, be specific" },
                    type  = new { type = "string",  description = "Content type to search: 'posts', 'comments', or 'all'" },
                    limit = new { type = "integer", description = "Maximum number of results to return" }
                },
                required = new[] { "query" }
            },
            Handler = async args =>
            {
                var query      = args.GetProperty("query").GetString()!;
                var searchType = args.TryGetProperty("type",  out var te) ? te.GetString() ?? "all" : "all";
                var limit      = args.TryGetProperty("limit", out var le) ? le.GetInt32()           : 20;
                return await Search(query, searchType, limit);
            }
        },

        new AgentTool
        {
            Name = "get_post",
            Description = "Get a full post with all its comments. Use this to read the entire discussion before deciding to engage.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    post_id = new { type = "string", description = "Post ID to retrieve" }
                },
                required = new[] { "post_id" }
            },
            Handler = async args =>
            {
                var postId = args.GetProperty("post_id").GetString()!;
                return await GetPost(postId);
            }
        },

        new AgentTool
        {
            Name = "list_submolts",
            Description = "List all submolts (communities) on Moltbook. Useful for discovering communities to subscribe to.",
            Parameters = new { type = "object", properties = new { } },
            Handler = async _ => await ListSubmolts()
        },

        new AgentTool
        {
            Name = "get_profile",
            Description = "Get an agent's profile, including their description, karma, posting history, and human owner info. Check this before following someone.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    agent_name = new { type = "string", description = "Agent name to look up" }
                },
                required = new[] { "agent_name" }
            },
            Handler = async args =>
            {
                var agentName = args.GetProperty("agent_name").GetString()!;
                return await GetProfile(agentName);
            }
        },

        // ── Memory tools ──────────────────────────────────────────────────────

        new AgentTool
        {
            Name = "add_memory",
            Description = "Add a memory note for future Claude instances. Use this to track ongoing situations, pending responses, or important context across cycles.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    content  = new { type = "string", description = "The memory content to store — be specific and actionable" },
                    priority = new { type = "string", description = "Priority level: 'low', 'normal', or 'high' (high surfaces first)" }
                },
                required = new[] { "content" }
            },
            Handler = async args =>
            {
                var content  = args.GetProperty("content").GetString()!;
                var priority = args.TryGetProperty("priority", out var pe) ? pe.GetString() ?? "normal" : "normal";
                return await AddMemory(content, priority);
            }
        },

        new AgentTool
        {
            Name = "remove_memory",
            Description = "Remove a memory by ID. Use this when a situation is resolved or a memory is no longer relevant.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "Memory ID to remove (from a previous add_memory response)" }
                },
                required = new[] { "memory_id" }
            },
            Handler = async args =>
            {
                var memoryId = args.GetProperty("memory_id").GetString()!;
                return await RemoveMemory(memoryId);
            }
        },

        // ── Control flow ──────────────────────────────────────────────────────

        new AgentTool
        {
            Name = "quit",
            Description = "End the current exploration cycle early. Use when the platform is down, the API is broken, or there is genuinely nothing worth engaging with.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    reason = new { type = "string", description = "Why are you ending the cycle early? Be specific." }
                },
                required = new[] { "reason" }
            },
            Handler = args =>
            {
                var reason = args.TryGetProperty("reason", out var re) ? re.GetString() ?? "" : "";
                return Task.FromResult(Quit(reason));
            }
        },

        // ── Action tools ──────────────────────────────────────────────────────

        new AgentTool
        {
            Name = "create_comment",
            Description = "Create a comment on a post. Use this after reading the full post and deciding you have something valuable to add.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    post_id   = new { type = "string", description = "Post ID to comment on" },
                    content   = new { type = "string", description = "Comment content — be thoughtful and substantive" },
                    parent_id = new { type = "string", description = "Parent comment ID if replying to a specific comment (optional)" }
                },
                required = new[] { "post_id", "content" }
            },
            Handler = async args =>
            {
                var postId   = args.GetProperty("post_id").GetString()!;
                var content  = args.GetProperty("content").GetString()!;
                var parentId = args.TryGetProperty("parent_id", out var pe) ? pe.GetString() : null;
                return await CreateComment(postId, content, parentId);
            }
        },

        new AgentTool
        {
            Name = "create_post",
            Description = "Create a new post in a submolt. Use sparingly — quality over quantity. You can post once per 30 minutes.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    submolt = new { type = "string", description = "Submolt (community) to post in" },
                    title   = new { type = "string", description = "Post title — concise and descriptive" },
                    content = new { type = "string", description = "Post body text (optional if url provided)" },
                    url     = new { type = "string", description = "URL to link (optional if content provided)" }
                },
                required = new[] { "submolt", "title" }
            },
            Handler = async args =>
            {
                var submolt = args.GetProperty("submolt").GetString()!;
                var title   = args.GetProperty("title").GetString()!;
                var content = args.TryGetProperty("content", out var ce) ? ce.GetString() : null;
                var url     = args.TryGetProperty("url",     out var ue) ? ue.GetString() : null;
                return await CreatePost(submolt, title, content, url);
            }
        },

        new AgentTool
        {
            Name = "upvote_post",
            Description = "Upvote a post to show you find it valuable. Be selective.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    post_id = new { type = "string", description = "Post ID to upvote" }
                },
                required = new[] { "post_id" }
            },
            Handler = async args =>
            {
                var postId = args.GetProperty("post_id").GetString()!;
                return await UpvotePost(postId);
            }
        },

        new AgentTool
        {
            Name = "upvote_comment",
            Description = "Upvote a comment. Use when a comment adds real value to the discussion.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    comment_id = new { type = "string", description = "Comment ID to upvote" }
                },
                required = new[] { "comment_id" }
            },
            Handler = async args =>
            {
                var commentId = args.GetProperty("comment_id").GetString()!;
                return await UpvoteComment(commentId);
            }
        },

        new AgentTool
        {
            Name = "follow_agent",
            Description = "Follow an agent. BE VERY SELECTIVE — only follow agents with consistently valuable content over multiple posts.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    agent_name = new { type = "string", description = "Agent name to follow" }
                },
                required = new[] { "agent_name" }
            },
            Handler = async args =>
            {
                var agentName = args.GetProperty("agent_name").GetString()!;
                return await FollowAgent(agentName);
            }
        },

        new AgentTool
        {
            Name = "subscribe_submolt",
            Description = "Subscribe to a submolt (community) to see its posts in your feed.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    submolt_name = new { type = "string", description = "Submolt name to subscribe to" }
                },
                required = new[] { "submolt_name" }
            },
            Handler = async args =>
            {
                var submoltName = args.GetProperty("submolt_name").GetString()!;
                return await SubscribeSubmolt(submoltName);
            }
        },

        new AgentTool
        {
            Name = "solve_verification",
            Description = "Submit the answer to a verification challenge. After creating a comment, post, or upvote that returns a 'verification' block, solve the math word problem and call this tool with the verification_code and your answer (2 decimal places, e.g. '30.00'). You must call this within ~5 minutes or the content will not go live.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    verification_code = new { type = "string", description = "The verification_code from the verification block (starts with 'moltbook_verify_')" },
                    answer            = new { type = "string", description = "Your numeric answer to the math problem, formatted to exactly 2 decimal places (e.g. '30.00')" }
                },
                required = new[] { "verification_code", "answer" }
            },
            Handler = async args =>
            {
                var verificationCode = args.GetProperty("verification_code").GetString()!;
                var answer           = args.GetProperty("answer").GetString()!;
                return await SolveVerification(verificationCode, answer);
            }
        }
    ];

    // ── Implementation methods ─────────────────────────────────────────────────

    private async Task<string> Search(string query, string type = "all", int limit = 20)
    {
        _logger.LogInformation("Tool: search(query={Query}, type={Type}, limit={Limit})", query, type, limit);
        var result = await _client.SearchAsync(query, type, limit, CancellationToken.None);
        if (!result.Success || result.Data?.Results == null)
            return FormatError("Search failed or no results", result);

        var response = new
        {
            success = true,
            query   = result.Data.Query,
            count   = result.Data.Count,
            results = result.Data.Results.Select(r => new
            {
                type     = r.Type,
                id       = r.Id,
                title    = r.Title,
                content  = r.Content?.Length > 300 ? r.Content[..300] + "..." : r.Content,
                author   = r.Author?.Name,
                submolt  = r.Submolt?.DisplayName ?? r.Submolt?.Name,
                post_id  = r.PostId,
                upvotes  = r.Upvotes,
                similarity = r.Similarity,
                url = r.Type == "post"
                    ? $"https://www.moltbook.com/post/{r.Id}"
                    : $"https://www.moltbook.com/post/{r.PostId}"
            }).ToArray()
        };
        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> GetPost(string postId)
    {
        _logger.LogInformation("Tool: get_post(postId={PostId})", postId);
        var result = await _client.GetPostAsync(postId, CancellationToken.None);
        if (!result.Success || result.Data?.Post == null)
            return FormatError("Post not found", result);

        var post     = result.Data.Post;
        var comments = result.Data.Comments ?? [];

        var response = new
        {
            success = true,
            post = new
            {
                id           = post.Id,
                title        = post.Title,
                content      = post.Content,
                url_link     = post.Url,
                author       = post.Author?.Name,
                submolt      = post.Submolt?.DisplayName ?? post.Submolt?.Name,
                upvotes      = post.Upvotes,
                downvotes    = post.Downvotes,
                created_at   = post.CreatedAt,
                moltbook_url = $"https://www.moltbook.com/post/{post.Id}"
            },
            comment_count = comments.Length,
            comments      = FormatComments(comments.Take(10).ToArray()),
            note          = comments.Length > 10 ? $"{comments.Length - 10} more comments not shown" : null
        };
        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> ListSubmolts()
    {
        _logger.LogInformation("Tool: list_submolts()");
        var result = await _client.ListSubmoltsAsync(CancellationToken.None);
        if (!result.Success || result.Data?.Submolts == null)
            return FormatError("Failed to retrieve submolts", result);

        var response = new
        {
            success  = true,
            count    = result.Data.Submolts.Length,
            submolts = result.Data.Submolts.Select(s => new
            {
                name             = s.Name,
                display_name     = s.DisplayName,
                description      = s.Description,
                subscriber_count = s.SubscriberCount,
                url              = $"https://www.moltbook.com/m/{s.Name}"
            }).ToArray()
        };
        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> GetProfile(string agentName)
    {
        _logger.LogInformation("Tool: get_profile(agentName={AgentName})", agentName);
        var result = await _client.GetProfileAsync(agentName, CancellationToken.None);
        if (!result.Success || result.Data?.Agent == null)
            return FormatError("Agent not found", result);

        var agent       = result.Data.Agent;
        var recentPosts = result.Data.RecentPosts ?? [];

        var response = new
        {
            success = true,
            agent = new
            {
                name            = agent.Name,
                description     = agent.Description,
                karma           = agent.Karma,
                follower_count  = agent.FollowerCount,
                following_count = agent.FollowingCount,
                created_at      = agent.CreatedAt,
                last_active     = agent.LastActive,
                owner = agent.Owner != null ? new
                {
                    x_handle = agent.Owner.XHandle,
                    x_name   = agent.Owner.XName,
                    x_bio    = agent.Owner.XBio
                } : null,
                profile_url = $"https://www.moltbook.com/u/{agent.Name}"
            },
            recent_posts = recentPosts.Select(p => new
            {
                id            = p.Id,
                title         = p.Title,
                submolt       = p.Submolt?.Name,
                upvotes       = p.Upvotes,
                comment_count = p.CommentCount,
                url           = $"https://www.moltbook.com/post/{p.Id}"
            }).ToArray()
        };
        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    private string Quit(string reason)
    {
        _logger.LogInformation("Tool: quit(reason={Reason})", reason);
        return JsonSerializer.Serialize(new
        {
            action      = "quit",
            reason      = reason,
            message     = "Ending exploration cycle early",
            quit_signal = true
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> AddMemory(string content, string priority = "normal")
    {
        _logger.LogInformation("Tool: add_memory(priority={Priority})", priority);
        _logger.LogInformation("Memory content: {Content}", content);

        if (_memoryManager is null)
            return JsonSerializer.Serialize(new { success = false, action = "add_memory", message = "Memory manager not available" });

        try
        {
            var memory   = await _memoryManager.AddMemoryAsync(content, priority);
            return JsonSerializer.Serialize(new
            {
                success    = true,
                action     = "add_memory",
                memory_id  = memory.Id,
                content    = memory.Content,
                priority   = memory.Priority,
                created_at = memory.CreatedAt,
                message    = "Memory saved successfully"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add memory");
            return JsonSerializer.Serialize(new { success = false, action = "add_memory", message = $"Failed to add memory: {ex.Message}" });
        }
    }

    private async Task<string> RemoveMemory(string memoryId)
    {
        _logger.LogInformation("Tool: remove_memory(memoryId={MemoryId})", memoryId);

        if (_memoryManager is null)
            return JsonSerializer.Serialize(new { success = false, action = "remove_memory", message = "Memory manager not available" });

        try
        {
            var removed = await _memoryManager.RemoveMemoryAsync(memoryId);
            return JsonSerializer.Serialize(removed
                ? new { success = true,  action = "remove_memory", memory_id = memoryId, message = "Memory removed successfully" }
                : new { success = false, action = "remove_memory", memory_id = memoryId, message = "Memory not found" },
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove memory");
            return JsonSerializer.Serialize(new { success = false, action = "remove_memory", message = $"Failed to remove memory: {ex.Message}" });
        }
    }

    private async Task<string> CreateComment(string postId, string content, string? parentId = null)
    {
        _logger.LogInformation("Tool: create_comment(postId={PostId}, parentId={ParentId})", postId, parentId);
        _logger.LogInformation("Comment content: {Content}", content);

        if (_observationMode)
        {
            return JsonSerializer.Serialize(new
            {
                action           = "create_comment",
                post_id          = postId,
                parent_id        = parentId,
                content          = content,
                url              = $"https://www.moltbook.com/post/{postId}",
                observation_mode = true,
                message          = "This action has been logged but not executed (observation mode)"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _client.CreateCommentAsync(postId, content, parentId, CancellationToken.None);
        if (!result.Success) return FormatError("Failed to create comment", result);

        if (_stateManager != null)
        {
            var state = await _stateManager.LoadAsync();
            state.Stats.TotalCommentsCreated++;
            await _stateManager.SaveAsync(state);
        }

        return JsonSerializer.Serialize(new
        {
            success      = true,
            action       = "create_comment",
            comment_id   = result.Data?.CommentId,
            post_id      = postId,
            parent_id    = parentId,
            url          = $"https://www.moltbook.com/post/{postId}",
            message      = result.Data?.Message ?? "Comment created successfully",
            verification = FormatVerification(result.Verification)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> CreatePost(string submolt, string title, string? content = null, string? url = null)
    {
        _logger.LogInformation("Tool: create_post(submolt={Submolt}, title={Title})", submolt, title);
        _logger.LogInformation("Post content: {Content}", content);

        if (_observationMode)
        {
            return JsonSerializer.Serialize(new
            {
                action           = "create_post",
                submolt          = submolt,
                title            = title,
                content          = content,
                url              = url,
                observation_mode = true,
                message          = "This action has been logged but not executed (observation mode)"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        if (_stateManager != null)
        {
            var state        = await _stateManager.LoadAsync();
            var lastPost     = state.Timestamps.GetLastPostTime();
            if (lastPost.HasValue)
            {
                var elapsed  = DateTime.UtcNow - lastPost.Value;
                var cooldown = TimeSpan.FromMinutes(30);
                if (elapsed < cooldown)
                {
                    var remaining = cooldown - elapsed;
                    return JsonSerializer.Serialize(new
                    {
                        success       = false,
                        action        = "create_post",
                        message       = "Rate limit: you can only post once per 30 minutes",
                        time_remaining = $"{remaining.TotalMinutes:F1} minutes",
                        last_post_time = lastPost.Value.ToString("O")
                    });
                }
            }
        }

        var result = await _client.CreatePostAsync(submolt, title, content, url, CancellationToken.None);
        if (!result.Success) return FormatError("Failed to create post", result);

        if (_stateManager != null)
        {
            var state = await _stateManager.LoadAsync();
            state.Stats.TotalPostsCreated++;
            state.Timestamps.SetLastPostTime(DateTime.UtcNow);
            await _stateManager.SaveAsync(state);
        }

        return JsonSerializer.Serialize(new
        {
            success      = true,
            action       = "create_post",
            post_id      = result.Data?.PostId,
            submolt      = submolt,
            title        = title,
            url          = result.Data?.PostId != null ? $"https://www.moltbook.com/post/{result.Data.PostId}" : null,
            message      = result.Data?.Message ?? "Post created successfully",
            verification = FormatVerification(result.Verification)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> UpvotePost(string postId)
    {
        _logger.LogInformation("Tool: upvote_post(postId={PostId})", postId);

        if (_observationMode)
        {
            return JsonSerializer.Serialize(new
            {
                action           = "upvote_post",
                post_id          = postId,
                url              = $"https://www.moltbook.com/post/{postId}",
                observation_mode = true,
                message          = "This action has been logged but not executed (observation mode)"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _client.UpvotePostAsync(postId, CancellationToken.None);
        if (!result.Success) return FormatError("Failed to upvote post", result);

        if (_stateManager != null)
        {
            var state = await _stateManager.LoadAsync();
            state.Stats.TotalUpvotes++;
            await _stateManager.SaveAsync(state);
        }

        return JsonSerializer.Serialize(new
        {
            success      = true,
            action       = "upvote_post",
            post_id      = postId,
            url          = $"https://www.moltbook.com/post/{postId}",
            message      = result.Data?.Message ?? "Post upvoted successfully",
            suggestion   = result.Data?.Suggestion,
            verification = FormatVerification(result.Verification)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> UpvoteComment(string commentId)
    {
        _logger.LogInformation("Tool: upvote_comment(commentId={CommentId})", commentId);

        if (_observationMode)
        {
            return JsonSerializer.Serialize(new
            {
                action           = "upvote_comment",
                comment_id       = commentId,
                observation_mode = true,
                message          = "This action has been logged but not executed (observation mode)"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _client.UpvoteCommentAsync(commentId, CancellationToken.None);
        if (!result.Success) return FormatError("Failed to upvote comment", result);

        if (_stateManager != null)
        {
            var state = await _stateManager.LoadAsync();
            state.Stats.TotalUpvotes++;
            await _stateManager.SaveAsync(state);
        }

        return JsonSerializer.Serialize(new
        {
            success      = true,
            action       = "upvote_comment",
            comment_id   = commentId,
            message      = result.Data?.Message ?? "Comment upvoted successfully",
            verification = FormatVerification(result.Verification)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> FollowAgent(string agentName)
    {
        _logger.LogInformation("Tool: follow_agent(agentName={AgentName})", agentName);

        if (_observationMode)
        {
            return JsonSerializer.Serialize(new
            {
                action           = "follow_agent",
                agent_name       = agentName,
                profile_url      = $"https://www.moltbook.com/u/{agentName}",
                observation_mode = true,
                message          = "This action has been logged but not executed (observation mode)",
                reminder         = "Only follow if you have seen multiple valuable posts from this agent"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _client.FollowAgentAsync(agentName, CancellationToken.None);
        if (!result.Success) return FormatError("Failed to follow agent", result);

        return JsonSerializer.Serialize(new
        {
            success     = true,
            action      = "follow_agent",
            agent_name  = agentName,
            profile_url = $"https://www.moltbook.com/u/{agentName}",
            message     = result.Data?.Message ?? "Now following agent"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> SubscribeSubmolt(string submoltName)
    {
        _logger.LogInformation("Tool: subscribe_submolt(submoltName={SubmoltName})", submoltName);

        if (_observationMode)
        {
            return JsonSerializer.Serialize(new
            {
                action           = "subscribe_submolt",
                submolt_name     = submoltName,
                url              = $"https://www.moltbook.com/m/{submoltName}",
                observation_mode = true,
                message          = "This action has been logged but not executed (observation mode)"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _client.SubscribeSubmoltAsync(submoltName, CancellationToken.None);
        if (!result.Success) return FormatError("Failed to subscribe to submolt", result);

        return JsonSerializer.Serialize(new
        {
            success      = true,
            action       = "subscribe_submolt",
            submolt_name = submoltName,
            url          = $"https://www.moltbook.com/m/{submoltName}",
            message      = result.Data?.Message ?? "Successfully subscribed to submolt"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> SolveVerification(string verificationCode, string answer)
    {
        _logger.LogInformation("Tool: solve_verification(code={Code}, answer={Answer})", verificationCode, answer);
        var result = await _client.VerifyContentAsync(verificationCode, answer, CancellationToken.None);
        if (!result.Success) return FormatError("Verification failed", result);

        return JsonSerializer.Serialize(new
        {
            success = true,
            action  = "solve_verification",
            status  = result.Data?.Status,
            message = result.Data?.Message ?? "Verification submitted"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string FormatError<T>(string message, MoltbookClient.MoltbookResponse<T> result) =>
        JsonSerializer.Serialize(new
        {
            success     = false,
            message     = message,
            http_status = result.HttpStatusCode,
            error       = result.ErrorMessage,
            detail      = result.ErrorDetail
        });

    private static object? FormatVerification(MoltbookClient.VerificationInfo? v) =>
        v is null ? null : new
        {
            verification_code = v.VerificationCode,
            challenge_text    = v.ChallengeText,
            expires_at        = v.ExpiresAt,
            instructions      = v.Instructions,
            action_required   = "Solve the math word problem in challenge_text and call solve_verification with the verification_code and your answer (2 decimal places). Do this now before continuing."
        };

    private static object[] FormatComments(MoltbookClient.Comment[] comments, int depth = 0)
    {
        const int maxDepth          = 2;
        const int maxRepliesPerLevel = 5;

        return comments.Select(c => (object)new
        {
            id          = c.Id,
            author      = c.Author?.Name,
            content     = c.Content?.Length > 500 ? c.Content[..500] + "..." : c.Content,
            upvotes     = c.Upvotes,
            created_at  = c.CreatedAt,
            parent_id   = c.ParentId,
            reply_count = c.Replies?.Length ?? 0,
            replies     = depth < maxDepth && c.Replies != null
                ? FormatComments(c.Replies.Take(maxRepliesPerLevel).ToArray(), depth + 1)
                : Array.Empty<object>()
        }).ToArray();
    }
}
