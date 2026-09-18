using System.Linq;

namespace Seeing.Session.Core
{
    /// <summary>会话关系类型（仅由 SessionGroup 承载）。</summary>
    public enum SessionRelation
    {
        /// <summary>锚点成员（无外部关系）</summary>
        None = 0,
        /// <summary>子 Agent / Task 会话</summary>
        Child = 1,
        /// <summary>分支会话（含 trim 备份）</summary>
        Fork = 2,
        /// <summary>交接后继</summary>
        HandoffSuccessor = 3,
        /// <summary>已被后续交接取代的主线节点</summary>
        HandoffPredecessor = 4
    }

    public sealed class SessionGroup
    {
        public string Id { get; set; } = string.Empty;
        public string PartitionId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string AnchorSessionId { get; set; } = string.Empty;
        public string? ActiveSessionId { get; set; }
        public List<SessionGroupMember> Members { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        /// <summary>每次变更 +1（组锁内），用于订阅方丢弃乱序快照。</summary>
        public long Version { get; set; }

        public string ResolveActiveId() =>
            ActiveSessionId is not null && Members.Any(m => m.SessionId == ActiveSessionId)
                ? ActiveSessionId
                : AnchorSessionId;

        public SessionGroup Clone() => new()
        {
            Id = Id,
            PartitionId = PartitionId,
            Title = Title,
            AnchorSessionId = AnchorSessionId,
            ActiveSessionId = ActiveSessionId,
            Members = Members.Select(m => m.Clone()).ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Version = Version
        };
    }

    public sealed class SessionGroupMember
    {
        public string SessionId { get; set; } = string.Empty;
        public SessionRelation Relation { get; set; }
        public string? ParentSessionId { get; set; }
        public string? Label { get; set; }
        public bool IsAnchor { get; set; }
        public int Order { get; set; }

        public SessionGroupMember Clone() => new()
        {
            SessionId = SessionId,
            Relation = Relation,
            ParentSessionId = ParentSessionId,
            Label = Label,
            IsAnchor = IsAnchor,
            Order = Order
        };
    }

    public sealed class SessionGroupChangedEventArgs : EventArgs
    {
        public required SessionGroup Group { get; init; }
    }
}
