using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Data;

public sealed class VolleyDraftDbContext(DbContextOptions<VolleyDraftDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MatchSession> MatchSessions => Set<MatchSession>();
    public DbSet<SessionPlayer> SessionPlayers => Set<SessionPlayer>();
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
            entity.Property(session => session.Status).HasConversion<string>();
            entity.HasOne(session => session.AdminUser)
                .WithMany(user => user.AdminSessions)
                .HasForeignKey(session => session.AdminUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SessionPlayer>(entity =>
        {
            entity.Property(player => player.DisplayName).HasMaxLength(120);
            entity.Property(player => player.Role).HasConversion<string>();
            entity.Property(player => player.Level).HasConversion<string>();
            entity.Property(player => player.Gender).HasConversion<string>();
            entity.HasIndex(player => new { player.SessionId, player.UserId });
            entity.HasOne(player => player.Session)
                .WithMany(session => session.Players)
                .HasForeignKey(player => player.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(player => player.User)
                .WithMany(user => user.SessionPlayers)
                .HasForeignKey(player => player.UserId)
                .OnDelete(DeleteBehavior.SetNull);
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
