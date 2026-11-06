namespace UsluzionicaServer.Services.Jobs;

/// <summary>
/// Gde na disku stoje fotografije posla i kako se brišu. Javne su, kao slike
/// oglasa (wwwroot/uploads/jobs/{id}/), jer ih vide svi uslugodavci.
///
/// Brisanje fajlova uvek ide PRE brisanja reda u bazi — isti razlog kao kod
/// oglasa: obrnutim redom bi pad ostavio fajlove bez ijednog reda koji ih
/// pominje, smeće koje niko više ne može da poveže sa poslom.
/// </summary>
public sealed class JobSlike(IWebHostEnvironment env, ILogger<JobSlike> logger)
{
    public string Folder(int jobId) =>
        Path.Combine(env.WebRootPath, "uploads", "jobs", jobId.ToString());

    public static string Url(int jobId, string fileName) => $"/uploads/jobs/{jobId}/{fileName}";

    public void ObrisiFajl(int jobId, string imageUrl)
    {
        try
        {
            var putanja = Path.Combine(Folder(jobId), Path.GetFileName(imageUrl));
            if (File.Exists(putanja)) File.Delete(putanja);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Brisanje slike posla {JobId} nije uspelo.", jobId);
        }
    }

    public void ObrisiSve(int jobId)
    {
        try
        {
            var folder = Folder(jobId);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Brisanje foldera slika posla {JobId} nije uspelo.", jobId);
        }
    }
}
