namespace BridgeCrew.Packs;

public sealed record ServiceHealth(string Service,int Protocol,string Mode,bool GameConnected,long MaxUploadBytes,bool OpenRegistration);
public sealed record CrewAccount(string Id,string Username,string DisplayName,bool CanPublish);
public sealed record AccountSession(CrewAccount User,string AccessToken,DateTimeOffset ExpiresAt,string? RecoveryCode);
public sealed record PackRelease(string Id,string Version,string Name,string Description,string PackLock,string PublisherId,string PublisherName,DateTimeOffset PublishedAt);
public sealed record PublishResult(string Id,string Version,string PackLock);
public sealed record UploadResult(string Sha256,long Bytes,bool Verified);
public sealed record CrewFriend(string UserId,string Username,string DisplayName,string Status);
public sealed record RoomMember(string UserId,string Username,string DisplayName,string Edition,string Build,bool Ready,string PackLock);
public sealed record CrewRoom(LobbyAdvertisement Advertisement,string HostId,string Visibility,int Capacity,RoomMember[] Members,bool GameConnected,string Status);
public sealed record HostedRoom(CrewRoom Room,string InviteCode);
public sealed record HostRoomRequest(string Name,string PackId,string PackVersion,string Visibility,int Capacity,string Edition,string Build,string PackLock);
public sealed record RoomJoinRequest(int Revision,string PackLock,string Edition,string Build,string? InviteCode);
public sealed record InviteResult(string InviteCode);
public sealed record CrewPresence(CrewRoom? Room);
