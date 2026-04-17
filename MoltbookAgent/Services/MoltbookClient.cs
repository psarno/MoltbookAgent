using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoltbookAgent.Services;

/// <summary>
/// Client for interacting with Moltbook API
/// </summary>
public class MoltbookClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public MoltbookClient(string apiBase, string apiKey, ILogger logger)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBase),
            Timeout = TimeSpan.FromMinutes(5) // Increase timeout for overloaded API
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        _logger = logger;
    }

    /// <summary>
    /// Get agent status (claimed/pending)
    /// </summary>
    public async Task<MoltbookResponse<AgentStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        return await GetAsync<AgentStatus>("agents/status", ct);
    }

    /// <summary>
    /// Check for DM activity (requests and unread messages)
    /// </summary>
    public async Task<MoltbookResponse<DmCheck>> CheckDmsAsync(CancellationToken ct = default)
    {
        return await GetAsync<DmCheck>("agents/dm/check", ct);
    }

    /// <summary>
    /// Get personalized feed (subscribed submolts + followed agents)
    /// </summary>
    public async Task<MoltbookResponse<FeedResponse>> GetFeedAsync(
        string sort = "new",
        int limit = 15,
        CancellationToken ct = default)
    {
        return await GetAsync<FeedResponse>($"feed?sort={sort}&limit={limit}", ct);
    }

    /// <summary>
    /// Get global posts feed
    /// </summary>
    public async Task<MoltbookResponse<PostsResponse>> GetPostsAsync(
        string sort = "new",
        int limit = 15,
        CancellationToken ct = default)
    {
        return await GetAsync<PostsResponse>($"posts?sort={sort}&limit={limit}", ct);
    }

    /// <summary>
    /// Search posts and comments by semantic meaning
    /// </summary>
    public async Task<MoltbookResponse<SearchResponse>> SearchAsync(
        string query,
        string type = "all",
        int limit = 20,
        CancellationToken ct = default)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        return await GetAsync<SearchResponse>($"search?q={encodedQuery}&type={type}&limit={limit}", ct);
    }

    /// <summary>
    /// Get a single post with all comments
    /// </summary>
    public async Task<MoltbookResponse<PostDetail>> GetPostAsync(
        string postId,
        CancellationToken ct = default)
    {
        return await GetAsync<PostDetail>($"posts/{postId}", ct);
    }

    /// <summary>
    /// Get comments for a post
    /// </summary>
    public async Task<MoltbookResponse<CommentsResponse>> GetCommentsAsync(
        string postId,
        string sort = "top",
        CancellationToken ct = default)
    {
        return await GetAsync<CommentsResponse>($"posts/{postId}/comments?sort={sort}", ct);
    }

    /// <summary>
    /// List all submolts (communities)
    /// </summary>
    public async Task<MoltbookResponse<SubmoltsResponse>> ListSubmoltsAsync(CancellationToken ct = default)
    {
        return await GetAsync<SubmoltsResponse>("submolts", ct);
    }

    /// <summary>
    /// Get an agent's profile
    /// </summary>
    public async Task<MoltbookResponse<ProfileResponse>> GetProfileAsync(
        string agentName,
        CancellationToken ct = default)
    {
        var encodedName = Uri.EscapeDataString(agentName);
        return await GetAsync<ProfileResponse>($"agents/profile?name={encodedName}", ct);
    }

    /// <summary>
    /// Create a comment on a post
    /// </summary>
    public async Task<MoltbookResponse<CommentCreated>> CreateCommentAsync(
        string postId,
        string content,
        string? parentId = null,
        CancellationToken ct = default)
    {
        return await PostAsync<CommentCreated>($"posts/{postId}/comments", new
        {
            content,
            parent_id = parentId
        }, ct);
    }

    /// <summary>
    /// Create a new post
    /// </summary>
    public async Task<MoltbookResponse<PostCreated>> CreatePostAsync(
        string submolt,
        string title,
        string? content = null,
        string? url = null,
        CancellationToken ct = default)
    {
        return await PostAsync<PostCreated>("posts", new
        {
            submolt,
            title,
            content,
            url
        }, ct);
    }

    /// <summary>
    /// Upvote a post
    /// </summary>
    public async Task<MoltbookResponse<VoteResponse>> UpvotePostAsync(
        string postId,
        CancellationToken ct = default)
    {
        return await PostAsync<VoteResponse>($"posts/{postId}/upvote", null, ct);
    }

    /// <summary>
    /// Downvote a post
    /// </summary>
    public async Task<MoltbookResponse<VoteResponse>> DownvotePostAsync(
        string postId,
        CancellationToken ct = default)
    {
        return await PostAsync<VoteResponse>($"posts/{postId}/downvote", null, ct);
    }

    /// <summary>
    /// Upvote a comment
    /// </summary>
    public async Task<MoltbookResponse<VoteResponse>> UpvoteCommentAsync(
        string commentId,
        CancellationToken ct = default)
    {
        return await PostAsync<VoteResponse>($"comments/{commentId}/upvote", null, ct);
    }

    /// <summary>
    /// Follow an agent
    /// </summary>
    public async Task<MoltbookResponse<FollowResponse>> FollowAgentAsync(
        string agentName,
        CancellationToken ct = default)
    {
        return await PostAsync<FollowResponse>($"agents/{agentName}/follow", null, ct);
    }

    /// <summary>
    /// Subscribe to a submolt
    /// </summary>
    public async Task<MoltbookResponse<SubscribeResponse>> SubscribeSubmoltAsync(
        string submoltName,
        CancellationToken ct = default)
    {
        return await PostAsync<SubscribeResponse>($"submolts/{submoltName}/subscribe", null, ct);
    }

    /// <summary>
    /// Generic GET request
    /// </summary>
    private async Task<MoltbookResponse<T>> GetAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            var fullUrl = new Uri(_httpClient.BaseAddress!, path);
            _logger.LogDebug("Moltbook API GET full URL: {Url}", fullUrl);

            var response = await _httpClient.GetAsync(path, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation("Moltbook API GET {Path}: {Status}", path, response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Moltbook API GET {Path} failed: {Status} - {Json}", path, response.StatusCode, json);
                return new MoltbookResponse<T>
                {
                    Success = false,
                    HttpStatusCode = (int)response.StatusCode,
                    ErrorMessage = response.ReasonPhrase ?? "Request failed",
                    ErrorDetail = json
                };
            }

            _logger.LogDebug("Moltbook API GET {Path} response: {Json}", path, json);

            var result = JsonSerializer.Deserialize<MoltbookResponse<T>>(json);
            if (result != null && result.Success && result.Data == null)
            {
                // Some endpoints return data at root level instead of nested under "data"
                // Try deserializing the entire response as T
                try
                {
                    var directData = JsonSerializer.Deserialize<T>(json);
                    result.Data = directData;
                }
                catch
                {
                    // If that fails, leave Data as null
                }
            }
            return result ?? new MoltbookResponse<T> { Success = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Moltbook API: {Path}", path);
            return new MoltbookResponse<T>
            {
                Success = false,
                HttpStatusCode = null,
                ErrorMessage = "Exception occurred",
                ErrorDetail = ex.Message
            };
        }
    }

    /// <summary>
    /// Generic POST request with retry on 401 errors
    /// </summary>
    private async Task<MoltbookResponse<T>> PostAsync<T>(string path, object? body, CancellationToken ct)
    {
        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Debug: Log authorization header status
                var hasAuth = _httpClient.DefaultRequestHeaders.Authorization != null;
                _logger.LogDebug("POST {Path} - Authorization header present: {HasAuth}", path, hasAuth);

                HttpResponseMessage response;
                if (body != null)
                {
                    response = await _httpClient.PostAsJsonAsync(path, body, ct);
                }
                else
                {
                    // Use empty StringContent instead of null to ensure headers are preserved
                    var emptyContent = new StringContent("");
                    emptyContent.Headers.ContentType = null; // Clear content-type for empty body
                    response = await _httpClient.PostAsync(path, emptyContent, ct);
                }

                var json = await response.Content.ReadAsStringAsync(ct);

                _logger.LogInformation("Moltbook API POST {Path}: {Status} (attempt {Attempt}/{Max})",
                    path, response.StatusCode, attempt, maxRetries);

                // Retry on 401 (Unauthorized) - may be temporary during high load
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt < maxRetries)
                {
                    var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                    _logger.LogWarning("Moltbook API POST {Path} returned 401 Unauthorized - retrying in {Delay}ms (attempt {Attempt}/{Max})",
                        path, delayMs, attempt, maxRetries);
                    await Task.Delay(delayMs, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Moltbook API POST {Path} failed: {Status} - {Json}", path, response.StatusCode, json);
                    return new MoltbookResponse<T>
                    {
                        Success = false,
                        HttpStatusCode = (int)response.StatusCode,
                        ErrorMessage = response.ReasonPhrase ?? "Request failed",
                        ErrorDetail = json
                    };
                }

                _logger.LogDebug("Moltbook API POST {Path} response: {Json}", path, json);

                var result = JsonSerializer.Deserialize<MoltbookResponse<T>>(json);
                return result ?? new MoltbookResponse<T> { Success = false };
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                _logger.LogWarning(ex, "Error calling Moltbook API {Path} - retrying in {Delay}ms (attempt {Attempt}/{Max})",
                    path, delayMs, attempt, maxRetries);
                await Task.Delay(delayMs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Moltbook API: {Path} after {Max} attempts", path, maxRetries);
                return new MoltbookResponse<T>
                {
                    Success = false,
                    HttpStatusCode = null,
                    ErrorMessage = "Exception occurred",
                    ErrorDetail = ex.Message
                };
            }
        }

        // Should not reach here, but return failure if we somehow do
        return new MoltbookResponse<T>
        {
            Success = false,
            ErrorMessage = "All retry attempts exhausted"
        };
    }

    /// <summary>
    /// Submit an answer to a verification challenge returned by a write operation.
    /// Must be called within ~5 minutes of the write that returned the verification_code.
    /// </summary>
    public async Task<MoltbookResponse<VerifyResponse>> VerifyContentAsync(
        string verificationCode,
        string answer,
        CancellationToken ct = default)
    {
        return await PostAsync<VerifyResponse>("verify", new
        {
            verification_code = verificationCode,
            answer
        }, ct);
    }

    // DTOs for Moltbook API responses
    public class MoltbookResponse<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("verification")]
        public VerificationInfo? Verification { get; set; }

        // Error details for debugging
        public int? HttpStatusCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
    }

    public class VerificationInfo
    {
        [JsonPropertyName("verification_code")]
        public string? VerificationCode { get; set; }

        [JsonPropertyName("challenge_text")]
        public string? ChallengeText { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }
    }

    public class VerifyResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    public class AgentStatus
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("agent")]
        public AgentInfo? Agent { get; set; }
    }

    public class AgentInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("claimed_at")]
        public string? ClaimedAt { get; set; }
    }

    public class DmCheck
    {
        [JsonPropertyName("has_activity")]
        public bool HasActivity { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("requests")]
        public RequestsInfo? Requests { get; set; }

        [JsonPropertyName("messages")]
        public MessagesInfo? Messages { get; set; }
    }

    public class RequestsInfo
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }
    }

    public class MessagesInfo
    {
        [JsonPropertyName("total_unread")]
        public int TotalUnread { get; set; }
    }

    public class FeedResponse
    {
        [JsonPropertyName("posts")]
        public Post[]? Posts { get; set; }
    }

    public class PostsResponse
    {
        [JsonPropertyName("posts")]
        public Post[]? Posts { get; set; }
    }

    public class Post
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("author")]
        public Author? Author { get; set; }

        [JsonPropertyName("submolt")]
        public Submolt? Submolt { get; set; }

        [JsonPropertyName("upvotes")]
        public int Upvotes { get; set; }

        [JsonPropertyName("downvotes")]
        public int Downvotes { get; set; }

        [JsonPropertyName("comment_count")]
        public int CommentCount { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }
    }

    public class Author
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class Submolt
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("subscriber_count")]
        public int? SubscriberCount { get; set; }
    }

    public class SearchResponse
    {
        [JsonPropertyName("results")]
        public SearchResult[]? Results { get; set; }

        [JsonPropertyName("query")]
        public string? Query { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }
    }

    public class SearchResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("author")]
        public Author? Author { get; set; }

        [JsonPropertyName("submolt")]
        public Submolt? Submolt { get; set; }

        [JsonPropertyName("post_id")]
        public string? PostId { get; set; }

        [JsonPropertyName("upvotes")]
        public int Upvotes { get; set; }

        [JsonPropertyName("similarity")]
        public double Similarity { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }
    }

    public class PostDetail
    {
        [JsonPropertyName("post")]
        public Post? Post { get; set; }

        [JsonPropertyName("comments")]
        public Comment[]? Comments { get; set; }
    }

    public class CommentsResponse
    {
        [JsonPropertyName("comments")]
        public Comment[]? Comments { get; set; }
    }

    public class Comment
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public Author? Author { get; set; }

        [JsonPropertyName("upvotes")]
        public int Upvotes { get; set; }

        [JsonPropertyName("parent_id")]
        public string? ParentId { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("replies")]
        public Comment[]? Replies { get; set; }
    }

    public class SubmoltsResponse
    {
        [JsonPropertyName("submolts")]
        public Submolt[]? Submolts { get; set; }
    }

    public class ProfileResponse
    {
        [JsonPropertyName("agent")]
        public AgentProfile? Agent { get; set; }

        [JsonPropertyName("recent_posts")]
        public Post[]? RecentPosts { get; set; }
    }

    public class AgentProfile
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("karma")]
        public int Karma { get; set; }

        [JsonPropertyName("follower_count")]
        public int FollowerCount { get; set; }

        [JsonPropertyName("following_count")]
        public int FollowingCount { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("last_active")]
        public string? LastActive { get; set; }

        [JsonPropertyName("owner")]
        public OwnerInfo? Owner { get; set; }
    }

    public class OwnerInfo
    {
        [JsonPropertyName("x_handle")]
        public string? XHandle { get; set; }

        [JsonPropertyName("x_name")]
        public string? XName { get; set; }

        [JsonPropertyName("x_bio")]
        public string? XBio { get; set; }
    }

    public class CommentCreated
    {
        [JsonPropertyName("comment_id")]
        public string? CommentId { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class PostCreated
    {
        [JsonPropertyName("post_id")]
        public string? PostId { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class VoteResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("author")]
        public Author? Author { get; set; }

        [JsonPropertyName("already_following")]
        public bool? AlreadyFollowing { get; set; }

        [JsonPropertyName("suggestion")]
        public string? Suggestion { get; set; }
    }

    public class FollowResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class SubscribeResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
