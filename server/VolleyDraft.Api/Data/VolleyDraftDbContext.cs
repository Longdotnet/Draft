using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Data;

public sealed class VolleyDraftDbContext(DbContextOptions<VolleyDraftDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MatchSession> MatchSessions => Set<MatchSession>();
    public DbSet<SessionPlayer> SessionPlayers => Set<SessionPlayer>();
    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<ZaloConnection> ZaloConnections => Set<ZaloConnection>();
    public DbSet<PollImport> PollImports => Set<PollImport>();
    public DbSet<ZaloGroupMessage> ZaloGroupMessages => Set<ZaloGroupMessage>();
    public DbSet<ZaloBotLearnedRule> ZaloBotLearnedRules => Set<ZaloBotLearnedRule>();
    public DbSet<ZaloBotConversationState> ZaloBotConversationStates => Set<ZaloBotConversationState>();
    public DbSet<ZaloBotImageAsset> ZaloBotImageAssets => Set<ZaloBotImageAsset>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<DraftSlot> DraftSlots => Set<DraftSlot>();
    public DbSet<DraftSlotPlayer> DraftSlotPlayers => Set<DraftSlotPlayer>();
    public DbSet<DraftRound> DraftRounds => Set<DraftRound>();
    public DbSet<BlindBag> BlindBags => Set<BlindBag>();
    public DbSet<DraftTurn> DraftTurns => Set<DraftTurn>();
    public DbSet<TeamPreferenceGroup> TeamPreferenceGroups => Set<TeamPreferenceGroup>();
    public DbSet<TeamPreferenceGroupPlayer> TeamPreferenceGroupPlayers => Set<TeamPreferenceGroupPlayer>();
    public DbSet<ZaloReminderSchedule> ZaloReminderSchedules => Set<ZaloReminderSchedule>();
    public DbSet<SessionWaitlistEntry> SessionWaitlistEntries => Set<SessionWaitlistEntry>();
    public DbSet<ZaloBotActionHistory> ZaloBotActionHistory => Set<ZaloBotActionHistory>();
    public DbSet<ZaloGroupMember> ZaloGroupMembers => Set<ZaloGroupMember>();
    public DbSet<ZaloPollSnapshot> ZaloPollSnapshots => Set<ZaloPollSnapshot>();
    public DbSet<ZaloPollOptionSnapshot> ZaloPollOptionSnapshots => Set<ZaloPollOptionSnapshot>();
    public DbSet<ZaloPollVoteActivity> ZaloPollVoteActivities => Set<ZaloPollVoteActivity>();
    public DbSet<ZaloActivityBackfillJob> ZaloActivityBackfillJobs => Set<ZaloActivityBackfillJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(user => user.Email).IsUnique();
            entity.Property(user => user.DisplayName).HasMaxLength(120);
            entity.Property(user => user.Email).HasMaxLength(240);
            entity.Property(user => user.PasswordHash).HasMaxLength(512);
        });

        modelBuilder.Entity<MatchSession>(entity =>
        {
            entity.Property(session => session.Name).HasMaxLength(160);
            entity.Property(session => session.ZaloGroupId).HasMaxLength(80);
            entity.Property(session => session.ZaloGroupName).HasMaxLength(160);
            entity.Property(session => session.ZaloGroupAvatarUrl).HasMaxLength(2048);
            entity.Property(session => session.Location).HasMaxLength(500);
            entity.Property(session => session.ParkingInstructions).HasMaxLength(1000);
            entity.Property(session => session.LocationImageUrl).HasMaxLength(2048);
            entity.Property(session => session.PaymentInstructions).HasMaxLength(1000);
            entity.Property(session => session.PaymentQrImageUrl).HasMaxLength(2048);
            entity.Property(session => session.BotCustomInstructions).HasMaxLength(2000);
            entity.Property(session => session.BotOperatorZaloUserIdsJson).HasMaxLength(4000);
            entity.Property(session => session.BotActionLeaseToken).HasMaxLength(80);
            entity.Property(session => session.BotActionLeaseName).HasMaxLength(80);
            entity.Property(session => session.ReminderLeaseToken).HasMaxLength(80);
            entity.Property(session => session.LastReminderError).HasMaxLength(1000);
            entity.Property(session => session.Status).HasConversion<string>();
            entity.HasOne(session => session.AdminUser)
                .WithMany(user => user.AdminSessions)
                .HasForeignKey(session => session.AdminUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.ZaloConnection)
                .WithMany(connection => connection.MatchSessions)
                .HasForeignKey(session => session.ZaloConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PlayerProfile>(entity =>
        {
            entity.Property(profile => profile.ZaloUserId).HasMaxLength(100);
            entity.Property(profile => profile.DisplayName).HasMaxLength(160);
            entity.Property(profile => profile.AvatarUrl).HasMaxLength(2048);
            entity.Property(profile => profile.Gender).HasConversion<string>();
            entity.Property(profile => profile.DefaultRole).HasConversion<string>();
            entity.Property(profile => profile.DefaultLevel).HasConversion<string>();
            entity.HasIndex(profile => profile.ZaloUserId).IsUnique();
        });

        modelBuilder.Entity<ZaloConnection>(entity =>
        {
            entity.Property(connection => connection.AccountZaloId).HasMaxLength(100);
            entity.Property(connection => connection.DisplayName).HasMaxLength(160);
            entity.Property(connection => connection.AvatarUrl).HasMaxLength(2048);
            entity.Property(connection => connection.Status).HasConversion<string>();
            entity.HasIndex(connection => new { connection.AdminUserId, connection.AccountZaloId }).IsUnique();
            entity.HasOne(connection => connection.AdminUser)
                .WithMany(user => user.ZaloConnections)
                .HasForeignKey(connection => connection.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PollImport>(entity =>
        {
            entity.Property(import => import.ZaloGroupId).HasMaxLength(100);
            entity.Property(import => import.PollId).HasMaxLength(100);
            entity.Property(import => import.PollQuestion).HasMaxLength(500);
            entity.HasIndex(import => new { import.SessionId, import.PollId });
            entity.HasOne(import => import.Session)
                .WithMany(session => session.PollImports)
                .HasForeignKey(import => import.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(import => import.ImportedByUser)
                .WithMany()
                .HasForeignKey(import => import.ImportedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ZaloGroupMessage>(entity =>
        {
            entity.Property(message => message.GroupId).HasMaxLength(100);
            entity.Property(message => message.MessageId).HasMaxLength(160);
            entity.Property(message => message.SenderId).HasMaxLength(100);
            entity.Property(message => message.SenderName).HasMaxLength(160);
            entity.Property(message => message.Content).HasMaxLength(4000);
            entity.Property(message => message.MessageType).HasMaxLength(80);
            entity.Property(message => message.ObservationSource).HasMaxLength(40);
            entity.Property(message => message.ProcessingToken).HasMaxLength(80);
            entity.Property(message => message.SelectedIntent).HasMaxLength(80);
            entity.Property(message => message.ReplyOutcome).HasMaxLength(80);
            entity.HasIndex(message => new { message.ZaloConnectionId, message.MessageId }).IsUnique();
            entity.HasIndex(message => new { message.ZaloConnectionId, message.GroupId, message.SentAt });
            entity.HasOne(message => message.ZaloConnection)
                .WithMany(connection => connection.GroupMessages)
                .HasForeignKey(message => message.ZaloConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloGroupMember>(entity =>
        {
            entity.Property(member => member.GroupId).HasMaxLength(100);
            entity.Property(member => member.ZaloUserId).HasMaxLength(100);
            entity.Property(member => member.DisplayName).HasMaxLength(160);
            entity.Property(member => member.AvatarUrl).HasMaxLength(2048);
            entity.HasIndex(member => new { member.ZaloConnectionId, member.GroupId, member.ZaloUserId }).IsUnique();
            entity.HasIndex(member => new { member.ZaloConnectionId, member.GroupId, member.IsCurrentMember });
            entity.HasOne(member => member.ZaloConnection)
                .WithMany(connection => connection.GroupMembers)
                .HasForeignKey(member => member.ZaloConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloPollSnapshot>(entity =>
        {
            entity.Property(poll => poll.GroupId).HasMaxLength(100);
            entity.Property(poll => poll.PollId).HasMaxLength(100);
            entity.Property(poll => poll.Question).HasMaxLength(1000);
            entity.Property(poll => poll.CreatorZaloUserId).HasMaxLength(100);
            entity.Property(poll => poll.ExclusionReason).HasMaxLength(500);
            entity.HasIndex(poll => new { poll.ZaloConnectionId, poll.GroupId, poll.PollId }).IsUnique();
            entity.HasIndex(poll => new { poll.ZaloConnectionId, poll.GroupId, poll.CreatedAtFromZalo });
            entity.HasOne(poll => poll.ZaloConnection)
                .WithMany(connection => connection.PollSnapshots)
                .HasForeignKey(poll => poll.ZaloConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloPollOptionSnapshot>(entity =>
        {
            entity.Property(option => option.ZaloOptionId).HasMaxLength(100);
            entity.Property(option => option.Content).HasMaxLength(1000);
            entity.HasIndex(option => new { option.PollSnapshotId, option.ZaloOptionId }).IsUnique();
            entity.HasOne(option => option.PollSnapshot)
                .WithMany(poll => poll.Options)
                .HasForeignKey(option => option.PollSnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloPollVoteActivity>(entity =>
        {
            entity.Property(vote => vote.ZaloUserId).HasMaxLength(100);
            entity.HasIndex(vote => new { vote.PollOptionSnapshotId, vote.ZaloUserId }).IsUnique();
            entity.HasIndex(vote => new { vote.PollSnapshotId, vote.ZaloUserId, vote.IsCurrentlySelected });
            entity.HasOne(vote => vote.PollSnapshot)
                .WithMany()
                .HasForeignKey(vote => vote.PollSnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(vote => vote.PollOptionSnapshot)
                .WithMany(option => option.Votes)
                .HasForeignKey(vote => vote.PollOptionSnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloActivityBackfillJob>(entity =>
        {
            entity.Property(job => job.GroupId).HasMaxLength(100);
            entity.Property(job => job.Stage).HasConversion<string>();
            entity.Property(job => job.Status).HasConversion<string>();
            entity.Property(job => job.MessageHistoryCapability).HasConversion<string>();
            entity.Property(job => job.BoardCursor).HasMaxLength(500);
            entity.Property(job => job.MessageCursor).HasMaxLength(500);
            entity.Property(job => job.LastBoardPageFingerprint).HasMaxLength(128);
            entity.Property(job => job.LastErrorSummary).HasMaxLength(2000);
            entity.Property(job => job.LeaseToken).HasMaxLength(80);
            entity.HasIndex(job => new { job.ZaloConnectionId, job.GroupId }).IsUnique();
            entity.HasIndex(job => new { job.Status, job.NextAttemptAt });
            entity.HasOne(job => job.ZaloConnection)
                .WithMany(connection => connection.ActivityBackfillJobs)
                .HasForeignKey(job => job.ZaloConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloBotLearnedRule>(entity =>
        {
            entity.Property(rule => rule.GroupId).HasMaxLength(100);
            entity.Property(rule => rule.Trigger).HasMaxLength(500);
            entity.Property(rule => rule.NormalizedTrigger).HasMaxLength(500);
            entity.Property(rule => rule.Answer).HasMaxLength(4000);
            entity.Property(rule => rule.CreatedBySenderId).HasMaxLength(100);
            entity.Property(rule => rule.CreatedBySenderName).HasMaxLength(160);
            entity.Property(rule => rule.Status).HasConversion<string>();
            entity.Property(rule => rule.Scope).HasMaxLength(40);
            entity.Property(rule => rule.ApprovedByUserId).HasMaxLength(100);
            entity.Property(rule => rule.ReviewNote).HasMaxLength(500);
            entity.HasIndex(rule => new { rule.ZaloConnectionId, rule.GroupId, rule.NormalizedTrigger }).IsUnique();
            entity.HasOne(rule => rule.ZaloConnection)
                .WithMany()
                .HasForeignKey(rule => rule.ZaloConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloBotConversationState>(entity =>
        {
            entity.Property(state => state.GroupId).HasMaxLength(100);
            entity.Property(state => state.SenderZaloUserId).HasMaxLength(100);
            entity.Property(state => state.PendingIntent).HasMaxLength(80);
            entity.Property(state => state.PendingPayloadJson).HasMaxLength(8000);
            entity.Property(state => state.PreviousCommand).HasMaxLength(80);
            entity.HasIndex(state => new { state.ZaloConnectionId, state.GroupId, state.SenderZaloUserId }).IsUnique();
            entity.HasIndex(state => state.ExpiresAt);
            entity.HasOne(state => state.ZaloConnection)
                .WithMany()
                .HasForeignKey(state => state.ZaloConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloBotImageAsset>(entity =>
        {
            entity.Property(asset => asset.FileName).HasMaxLength(160);
            entity.Property(asset => asset.ContentType).HasMaxLength(80);
            entity.HasIndex(asset => new { asset.AdminUserId, asset.CreatedAt });
            entity.HasOne(asset => asset.AdminUser)
                .WithMany()
                .HasForeignKey(asset => asset.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ZaloReminderSchedule>(entity =>
        {
            entity.Property(schedule => schedule.CreatedBySenderId).HasMaxLength(100);
            entity.Property(schedule => schedule.CreatedBySenderName).HasMaxLength(160);
            entity.Property(schedule => schedule.Message).HasMaxLength(2000);
            entity.Property(schedule => schedule.Audience).HasConversion<string>();
            entity.Property(schedule => schedule.LeaseToken).HasMaxLength(80);
            entity.Property(schedule => schedule.LastError).HasMaxLength(1000);
            entity.HasIndex(schedule => new { schedule.Enabled, schedule.NextRunAt });
            entity.HasIndex(schedule => schedule.SessionId);
            entity.HasOne(schedule => schedule.Session)
                .WithMany(session => session.ReminderSchedules)
                .HasForeignKey(schedule => schedule.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionWaitlistEntry>(entity =>
        {
            entity.Property(entry => entry.ZaloUserId).HasMaxLength(100);
            entity.Property(entry => entry.DisplayName).HasMaxLength(160);
            entity.Property(entry => entry.Status).HasConversion<string>();
            entity.HasIndex(entry => new { entry.SessionId, entry.ZaloUserId }).IsUnique();
            entity.HasIndex(entry => new { entry.SessionId, entry.Status, entry.CreatedAt });
            entity.HasIndex(entry => entry.InviteExpiresAt);
            entity.HasOne(entry => entry.Session)
                .WithMany(session => session.WaitlistEntries)
                .HasForeignKey(entry => entry.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(entry => entry.SessionPlayer)
                .WithMany()
                .HasForeignKey(entry => entry.SessionPlayerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ZaloBotActionHistory>(entity =>
        {
            entity.Property(action => action.ActorZaloUserId).HasMaxLength(100);
            entity.Property(action => action.ActorName).HasMaxLength(160);
            entity.Property(action => action.ActionType).HasMaxLength(80);
            entity.Property(action => action.Summary).HasMaxLength(1000);
            entity.Property(action => action.BeforeHash).HasMaxLength(64);
            entity.Property(action => action.AfterHash).HasMaxLength(64);
            entity.Property(action => action.UndoneByZaloUserId).HasMaxLength(100);
            entity.Property(action => action.UndoFailure).HasMaxLength(1000);
            entity.HasIndex(action => new { action.SessionId, action.CreatedAt });
            entity.HasIndex(action => new { action.SessionId, action.IsUndoable, action.UndoneAt });
            entity.HasOne(action => action.Session)
                .WithMany(session => session.BotActionHistory)
                .HasForeignKey(action => action.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionPlayer>(entity =>
        {
            entity.Property(player => player.DisplayName).HasMaxLength(120);
            entity.Property(player => player.AvatarUrl).HasMaxLength(2048);
            entity.Property(player => player.Role).HasConversion<string>();
            entity.Property(player => player.Level).HasConversion<string>();
            entity.Property(player => player.Gender).HasConversion<string>();
            entity.HasIndex(player => new { player.SessionId, player.UserId });
            entity.HasIndex(player => new { player.SessionId, player.PlayerProfileId }).IsUnique();
            entity.HasOne(player => player.Session)
                .WithMany(session => session.Players)
                .HasForeignKey(player => player.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(player => player.User)
                .WithMany(user => user.SessionPlayers)
                .HasForeignKey(player => player.UserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(player => player.PlayerProfile)
                .WithMany(profile => profile.SessionPlayers)
                .HasForeignKey(player => player.PlayerProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.Property(team => team.Name).HasMaxLength(40);
            entity.HasIndex(team => new { team.SessionId, team.Name }).IsUnique();
            entity.HasOne(team => team.Session)
                .WithMany(session => session.Teams)
                .HasForeignKey(team => team.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(team => team.CaptainSessionPlayer)
                .WithMany()
                .HasForeignKey(team => team.CaptainSessionPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DraftSlot>(entity =>
        {
            entity.Property(slot => slot.DisplayName).HasMaxLength(160);
            entity.Property(slot => slot.Type).HasConversion<string>();
            entity.Property(slot => slot.Role).HasConversion<string>();
            entity.Property(slot => slot.Gender).HasConversion<string>();
            entity.HasIndex(slot => new { slot.SessionId, slot.AssignedTeamId });
            entity.HasOne(slot => slot.Session)
                .WithMany(session => session.DraftSlots)
                .HasForeignKey(slot => slot.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(slot => slot.AssignedTeam)
                .WithMany(team => team.AssignedSlots)
                .HasForeignKey(slot => slot.AssignedTeamId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DraftSlotPlayer>(entity =>
        {
            entity.HasIndex(slotPlayer => new { slotPlayer.DraftSlotId, slotPlayer.SessionPlayerId })
                .IsUnique();
            entity.HasOne(slotPlayer => slotPlayer.DraftSlot)
                .WithMany(slot => slot.Players)
                .HasForeignKey(slotPlayer => slotPlayer.DraftSlotId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(slotPlayer => slotPlayer.SessionPlayer)
                .WithMany(player => player.DraftSlotPlayers)
                .HasForeignKey(slotPlayer => slotPlayer.SessionPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DraftRound>(entity =>
        {
            entity.Property(round => round.Label).HasMaxLength(80);
            entity.Property(round => round.Status).HasConversion<string>();
            entity.HasIndex(round => new { round.SessionId, round.RoundNumber }).IsUnique();
            entity.HasOne(round => round.Session)
                .WithMany(session => session.DraftRounds)
                .HasForeignKey(round => round.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BlindBag>(entity =>
        {
            entity.HasIndex(bag => new { bag.RoundId, bag.BagNumber }).IsUnique();
            entity.HasIndex(bag => bag.DraftSlotId).IsUnique();
            entity.HasOne(bag => bag.Session)
                .WithMany(session => session.BlindBags)
                .HasForeignKey(bag => bag.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(bag => bag.Round)
                .WithMany(round => round.BlindBags)
                .HasForeignKey(bag => bag.RoundId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(bag => bag.DraftSlot)
                .WithOne(slot => slot.BlindBag)
                .HasForeignKey<BlindBag>(bag => bag.DraftSlotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(bag => bag.PreparedDraftSlot)
                .WithMany()
                .HasForeignKey(bag => bag.PreparedDraftSlotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(bag => bag.OpenedByUser)
                .WithMany()
                .HasForeignKey(bag => bag.OpenedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(bag => bag.OpenedForTeam)
                .WithMany()
                .HasForeignKey(bag => bag.OpenedForTeamId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DraftTurn>(entity =>
        {
            entity.Property(turn => turn.Status).HasConversion<string>();
            var activeTurnFilter = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? "\"Status\" = 'Active'"
                : "Status = 'Active'";
            entity.HasIndex(turn => new { turn.SessionId, turn.Status })
                .IsUnique()
                .HasFilter(activeTurnFilter);
            entity.HasIndex(turn => new { turn.SessionId, turn.TurnOrder }).IsUnique();
            entity.HasOne(turn => turn.Session)
                .WithMany(session => session.DraftTurns)
                .HasForeignKey(turn => turn.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(turn => turn.Round)
                .WithMany(round => round.DraftTurns)
                .HasForeignKey(turn => turn.RoundId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(turn => turn.Team)
                .WithMany()
                .HasForeignKey(turn => turn.TeamId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(turn => turn.CaptainSessionPlayer)
                .WithMany()
                .HasForeignKey(turn => turn.CaptainSessionPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(turn => turn.OpenedBag)
                .WithMany()
                .HasForeignKey(turn => turn.OpenedBagId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TeamPreferenceGroup>(entity =>
        {
            entity.HasOne(group => group.Session)
                .WithMany(session => session.TeamPreferenceGroups)
                .HasForeignKey(group => group.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamPreferenceGroupPlayer>(entity =>
        {
            entity.HasKey(groupPlayer => new
            {
                groupPlayer.TeamPreferenceGroupId,
                groupPlayer.SessionPlayerId
            });
            entity.HasIndex(groupPlayer => groupPlayer.SessionPlayerId).IsUnique();
            entity.HasOne(groupPlayer => groupPlayer.TeamPreferenceGroup)
                .WithMany(group => group.Players)
                .HasForeignKey(groupPlayer => groupPlayer.TeamPreferenceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(groupPlayer => groupPlayer.SessionPlayer)
                .WithMany(player => player.TeamPreferenceGroupPlayers)
                .HasForeignKey(groupPlayer => groupPlayer.SessionPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
