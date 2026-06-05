using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting.Conversion;

public sealed record ConversionData(
    IReadOnlyList<ConversionMember> Members,
    IReadOnlyList<ConversionRole> Roles,
    IReadOnlyList<ConversionChannel> Channels
)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ConversionMember(
    string Id,
    string DisplayName,
    string? AvatarUrl,
    string? ColorHex,
    IReadOnlyList<string> RoleIds
);

public sealed record ConversionRole(string Id, string Name, string? ColorHex, int Position);

public sealed record ConversionChannel(string Id, string Name);
