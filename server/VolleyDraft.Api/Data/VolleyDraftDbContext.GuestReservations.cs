using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Data;

public sealed partial class VolleyDraftDbContext
{
    public DbSet<ZaloGuestReservation> ZaloGuestReservations => Set<ZaloGuestReservation>();
}
