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
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<DraftSlot> DraftSlots => Set<DraftSlot>();
    public DbSet<DraftSlotPlayer> DraftSlotPlayers => Set<DraftSlotPlayer>();
    public DbSet<DraftRound> DraftRounds => Set<DraftRound>();
    public DbSet<BlindBag> BlindBags => Set<BlindBag>();
    public DbSet<DraftTurn> DraftTurns => Set<DraftTurn>();
    public DbSet<TeamPreferenceGroup> TeamPreferenceGroups => Set<TeamPreferenceGroup>();
    public DbSet<TeamPreferenceGroupPlayer> TeamPreferenceGroupPlayers => Set<TeamPreferenceGroupPlayer>();

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
            entity.Property(session => session.BotCustomInstructions).HasMaxLength(2000);
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
            entity.HasIndex(message => new { message.ZaloConnectionId, message.MessageId }).IsUnique();
            entity.HasIndex(message => new { message.ZaloConnectionId, message.GroupId, message.SentAt });
            entity.HasOne(message => message.ZaloConnection)
                .WithMany(connection => connection.GroupMessages)
                .HasForeignKey(message => message.ZaloConnectionId)
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
