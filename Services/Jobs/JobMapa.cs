using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;

namespace UsluzionicaServer.Services.Jobs;

/// <summary>
/// Posao → DTO, za gledaoca. Čisto mapiranje, bez upita: pozivalac učitava
/// autora, kategoriju, slike i javljanja (vidi <see cref="JobService"/>).
/// </summary>
public static class JobMapa
{
    /// <summary>Javljanja koja „važe" — prikazuju se u broju na kartici.</summary>
    public static bool Vazi(JobApplicationStatus s) =>
        s is JobApplicationStatus.Sent or JobApplicationStatus.Chosen;

    public static JobDto UDto(Job j, string? viewerId, JobPravila pravila, DateTime sada)
    {
        var mine  = viewerId is not null && j.AuthorId == viewerId;
        var prijave = j.Applications ?? [];

        var dto = new JobDto
        {
            Id           = j.Id,
            Title        = j.Title,
            Description  = j.Description,
            CategoryId   = j.CategoryId,
            CategoryName = j.Category?.Name ?? string.Empty,
            CategorySlug = j.Category?.Slug ?? string.Empty,
            Location     = j.Location,
            PayAmount    = j.PayAmount,
            Status       = j.Status,
            CreatedAt    = j.CreatedAt,
            ExpiresAt    = j.ExpiresAt,
            PurgeAt      = j.Status == JobStatus.Expired ? j.PurgeAt : null,
            IsBoosted    = JeIstaknut(j, sada),
            BoostExpiresAt = JeIstaknut(j, sada) ? j.BoostExpiresAt : null,
            BoostPausedDays = j.Status == JobStatus.Assigned && j.BoostRemainingOnAssign is { } b && b > TimeSpan.Zero
                ? Math.Round(b.TotalDays, 1) : null,
            ApplicationsCount = prijave.Count(a => Vazi(a.Status)),
            Images = (j.Images ?? [])
                .OrderBy(i => i.SortOrder)
                .Select(i => new JobImageDto { Id = i.Id, ImageUrl = i.ImageUrl, SortOrder = i.SortOrder })
                .ToList(),
            Author = Osoba(j.Author, kaoNarucilac: true, profil: null),
            IsMine = mine,
            CancellationsLeft = Math.Max(0, pravila.NajviseOtkazivanja - j.CancelCount)
        };

        if (mine)
        {
            dto.CanExtend = MozeProduzenje(j, pravila, sada);
            dto.NewApplicationsCount = prijave.Count(a =>
                a.Status == JobApplicationStatus.Sent &&
                (j.ApplicationsSeenAt is null || a.UpdatedAt > j.ApplicationsSeenAt));
        }

        if (viewerId is not null && !mine)
        {
            var moja = prijave.FirstOrDefault(a => a.ProviderUserId == viewerId);
            if (moja is not null)
                dto.MyApplication = MojaPrijava(moja, j);
        }

        return dto;
    }

    public static MyApplicationDto MojaPrijava(JobApplication a, Job j) => new()
    {
        Id           = a.Id,
        Status       = a.Status,
        Message      = a.Message,
        OfferAmount  = a.OfferAmount,
        CreatedAt    = a.CreatedAt,
        CancelReason = a.Status == JobApplicationStatus.Cancelled ? a.CancelReason : null,
        OtherChosen  = j.Status == JobStatus.Assigned && j.AssignedApplicationId != a.Id
    };

    /// <summary>
    /// Osoba uz posao. <paramref name="kaoNarucilac"/>: autor se ocenjuje kao
    /// naručilac (ClientRating), izvršilac kao uslugodavac (profil).
    /// </summary>
    public static JobPersonDto Osoba(ApplicationUser? u, bool kaoNarucilac, ProviderProfile? profil) => u is null
        ? new JobPersonDto { FullName = "Obrisan nalog" }
        : new JobPersonDto
        {
            UserId            = u.Id,
            FullName          = u.FullName,
            ProfileImageUrl   = u.ProfileImageUrl,
            Rating            = kaoNarucilac ? u.ClientRating      : profil?.AverageRating ?? 0m,
            RatingCount       = kaoNarucilac ? u.ClientReviewCount : profil?.TotalReviews  ?? 0,
            ProviderProfileId = profil?.Id,
            IsVerified        = profil?.IsVerified ?? false,
            MemberSince       = u.CreatedAt
        };

    /// <summary>
    /// Isticanje važi samo dok je posao aktivan i dok rok nije prošao —
    /// JobOdrzavanje gasi zastavicu tek na 15 minuta.
    /// </summary>
    public static bool JeIstaknut(Job j, DateTime sada) =>
        j.Status == JobStatus.Open && j.IsBoosted && j.BoostExpiresAt is { } d && d > sada;

    /// <summary>
    /// „Produži": aktivan posao u poslednjih N dana roka, ili istekao dok još
    /// nije obrisan.
    /// </summary>
    public static bool MozeProduzenje(Job j, JobPravila pravila, DateTime sada) => j.Status switch
    {
        JobStatus.Open    => j.ExpiresAt - sada <= TimeSpan.FromDays(pravila.ProzorProduzenja),
        JobStatus.Expired => j.PurgeAt is null || j.PurgeAt > sada,
        _                 => false
    };
}
