using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Ime autora na javnom poslu. Gost vidi „Sanja M.", nikad puno prezime —
/// sajt ime ne skraćuje sam, pa je ovo jedino mesto koje to štiti.
/// </summary>
public class KratkoImeTests
{
    [Theory]
    [InlineData("Sanja Marković",       "Sanja M.")]
    [InlineData("  Sanja   Marković  ", "Sanja M.")]
    [InlineData("Ana Marija Petrović",  "Ana P.")]
    [InlineData("Đorđe Šaponjić",       "Đorđe Š.")]
    [InlineData("Milan Ljubić",         "Milan Lj.")]
    [InlineData("Ivana Njegoš",         "Ivana Nj.")]
    [InlineData("Marko Džajić",         "Marko Dž.")]
    [InlineData("Petar LJUBIĆ",         "Petar Lj.")]
    [InlineData("Jovan petrović",       "Jovan P.")]
    [InlineData("Sanja",                "Sanja")]
    [InlineData("",                     "")]
    [InlineData(null,                   "")]
    public void Skracuje(string? puno, string ocekivano) =>
        JobMapa.KratkoIme(puno).Should().Be(ocekivano);
}
