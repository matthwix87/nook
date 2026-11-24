using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nook.Core.Api;

public sealed class Budget
{
    public bool Unlimited { get; set; }
    public long DailyAllowance { get; set; }
    public long DailyLeft { get; set; }
    public long Topup { get; set; }
    public long TotalLeft { get; set; }
    public double ResetsAt { get; set; }
}

public sealed class Account
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public bool IsOwner { get; set; }
    public Budget? Budget { get; set; }
}

public sealed class Status
{
    public bool Backend { get; set; }
    public bool Ollama { get; set; }
    public string Model { get; set; } = "";
    public bool ModelAvailable { get; set; }
    public bool Available { get; set; }
    public bool NookOff { get; set; }
    public bool Maintenance { get; set; }
    public string Announcement { get; set; } = "";
}

public sealed class ConversationSummary
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public double Created { get; set; }
    public double Updated { get; set; }
    public int Pinned { get; set; }
    public long? ProjectId { get; set; }
    public string? Snippet { get; set; }
}

public sealed class AttachmentInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public List<AttachmentInfo> Attachments { get; set; } = new();
    public double Created { get; set; }
}

public sealed class Conversation
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public int Pinned { get; set; }
    public long? ProjectId { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
}

public sealed class ProjectFile
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

public sealed class Project
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<ProjectFile> Files { get; set; } = new();
    public long TotalBytes { get; set; }
}

public sealed class UserSettings
{
    public string Instructions { get; set; } = "";
    public string ReplyStyle { get; set; } = "";
    public int MaxInstructionsChars { get; set; } = 2000;
}

public sealed class Usage
{
    public long Prompt { get; set; }
    public long Completion { get; set; }
    public long Total { get; set; }
}

/// <summary>One server-sent event from /api/chat or /api/agent.</summary>
public sealed class StreamEvent
{
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? State { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public long? ConversationId { get; set; }
    public long? UserMessageId { get; set; }
    public long? AssistantMessageId { get; set; }
    public Usage? Usage { get; set; }
    public Budget? Budget { get; set; }
    public int? Trimmed { get; set; }
    public JsonElement? ToolCalls { get; set; }
}

public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string message, JsonElement? body = null) : base(message)
    {
        Status = status;
        Code = code;
        Body = body;
    }

    public int Status { get; }
    public string Code { get; }
    public JsonElement? Body { get; }

    public double? Number(string field) =>
        Body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    public bool IsOffline => Status == 0 || Code == "offline";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
